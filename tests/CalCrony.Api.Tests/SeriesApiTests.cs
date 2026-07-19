using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class SeriesApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9900;
    private const long ChannelId = 9901;
    private const long CreatorId = 9902;

    private HttpClient Client => fixture.Client;

    private static readonly RecurrenceRuleDto Weekly = new(RecurrenceUnit.Week);

    [Fact]
    public async Task Create_validates_recurrence_inputs()
    {
        var noRule = await PostEventAsync(new CreateEventRequest(
            CreatorId, "No rule", "in 2 hours", ChannelId, RepeatCount: 3));
        Assert.Equal(HttpStatusCode.BadRequest, noRule.StatusCode);
        Assert.Contains("Set a repeat rule", await ErrorAsync(noRule));

        var badInterval = await PostEventAsync(new CreateEventRequest(
            CreatorId, "Bad interval", "in 2 hours", ChannelId, Recurrence: new(RecurrenceUnit.Day, 13)));
        Assert.Equal(HttpStatusCode.BadRequest, badInterval.StatusCode);

        var both = await PostEventAsync(new CreateEventRequest(
            CreatorId, "Both ends", "in 2 hours", ChannelId,
            Recurrence: Weekly, RepeatUntilText: "in 3 weeks", RepeatCount: 3));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("not both", await ErrorAsync(both));

        var badCount = await PostEventAsync(new CreateEventRequest(
            CreatorId, "One time", "in 2 hours", ChannelId, Recurrence: Weekly, RepeatCount: 1));
        Assert.Equal(HttpStatusCode.BadRequest, badCount.StatusCode);

        var badUntil = await PostEventAsync(new CreateEventRequest(
            CreatorId, "Gibberish until", "in 2 hours", ChannelId,
            Recurrence: Weekly, RepeatUntilText: "flurble"));
        Assert.Equal(HttpStatusCode.BadRequest, badUntil.StatusCode);
    }

    [Fact]
    public async Task Create_with_recurrence_exposes_series_and_summary()
    {
        var ev = await CreateSeriesEventAsync("Weekly standup", new(RecurrenceUnit.Week, 2));

        Assert.NotNull(ev.SeriesId);
        Assert.StartsWith("Repeats every 2 weeks on", ev.RecurrenceSummary);

        var series = await GetSeriesAsync(ev.SeriesId!.Value);
        Assert.Equal(ev.Id, series.LiveEventId);
        Assert.Equal(1, series.OccurrenceCount);
        Assert.False(series.Ended);
        Assert.Equal("Weekly standup", series.Title);
    }

    [Fact]
    public async Task Skip_cancels_deletes_embed_and_spawns_next_from_template()
    {
        var ev = await CreateSeriesEventAsync("Skip me", Weekly);
        await SetMessageAsync(ev.Id, 777001);

        // Diverge the live occurrence first — the replacement must come from the TEMPLATE.
        var diverge = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Title: "One-off title", Scope: EditScope.Occurrence));
        diverge.EnsureSuccessStatusCode();

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        var response = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!;

        Assert.NotNull(response.NextEvent);
        Assert.Equal("Skip me", response.NextEvent!.Title); // template title, not the diverged one
        Assert.Equal(EventStatus.Scheduled, response.NextEvent.Status);
        Assert.Equal(ev.SeriesId, response.NextEvent.SeriesId);
        Assert.True(response.NextEvent.StartsAtUtc > ev.StartsAtUtc);
        Assert.Equal(2, response.Series.OccurrenceCount);

        var old = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(EventStatus.Cancelled, old!.Status);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.True(await db.Deliveries.AnyAsync(d =>
            d.Type == DeliveryType.DeleteEventMessage && d.PayloadJson.Contains("777001")));
        Assert.True(await db.Deliveries.AnyAsync(d =>
            d.Type == DeliveryType.PostEventMessage && d.PayloadJson.Contains(response.NextEvent.Id.ToString())));

        // The skipped occurrence is no longer live: skipping it again conflicts.
        var again = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Skip_on_final_occurrence_ends_the_series()
    {
        var ev = await CreateSeriesEventAsync("Two shots", Weekly, repeatCount: 2);

        var first = await SkipAsync(ev.Id);
        Assert.NotNull(first.NextEvent);

        var second = await SkipAsync(first.NextEvent!.Id);
        Assert.Null(second.NextEvent);
        Assert.True(second.Series.Ended);
    }

    [Fact]
    public async Task Skip_rejects_one_offs()
    {
        var response = await PostEventAsync(new CreateEventRequest(CreatorId, "Solo", "in 2 hours", ChannelId));
        var ev = (await response.Content.ReadFromJsonAsync<EventDto>())!;

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        Assert.Equal(HttpStatusCode.BadRequest, skip.StatusCode);
        Assert.Contains("doesn't repeat", await ErrorAsync(skip));
    }

    [Fact]
    public async Task Stop_is_idempotent_and_live_occurrence_survives()
    {
        var ev = await CreateSeriesEventAsync("Stop me", Weekly);

        var stop = await Client.PostAsync($"/series/{ev.SeriesId}/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var series = (await stop.Content.ReadFromJsonAsync<SeriesDto>())!;
        Assert.True(series.Ended);
        Assert.Equal(ev.Id, series.LiveEventId);

        var live = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(EventStatus.Scheduled, live!.Status);
        Assert.Null(live.RecurrenceSummary); // ended series reads as a one-off

        var again = await Client.PostAsync($"/series/{ev.SeriesId}/stop", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Web_stop_enqueues_embed_sync_and_web_delete_stops_series()
    {
        await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        var (member, session) = await fixture.LoginAsync(9950, (GuildId, "G", false));

        var create = await member.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            0, "Web series", "in 2 hours", 0, Recurrence: Weekly));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        await SetMessageAsync(ev.Id, 777002);

        (await member.PostAsync($"/series/{ev.SeriesId}/stop", null)).EnsureSuccessStatusCode();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            Assert.True(await db.Deliveries.AnyAsync(d =>
                d.Type == DeliveryType.SyncEventMessage && d.PayloadJson.Contains(ev.Id.ToString())));
        }

        // Second web-created series; deleting its live occurrence stops the whole series.
        var create2 = await member.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            0, "Web series 2", "in 2 hours", 0, Recurrence: Weekly));
        var ev2 = (await create2.Content.ReadFromJsonAsync<EventDto>())!;
        (await member.DeleteAsync($"/events/{ev2.Id}")).EnsureSuccessStatusCode();

        var series2 = await GetSeriesAsync(ev2.SeriesId!.Value);
        Assert.True(series2.Ended);
        Assert.Null(series2.LiveEventId);
        _ = session;
    }

    [Fact]
    public async Task Update_scope_matrix()
    {
        var ev = await CreateSeriesEventAsync("Scoped", Weekly);

        // Live series occurrence: scope is mandatory.
        var noScope = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Title: "Renamed"));
        Assert.Equal(HttpStatusCode.BadRequest, noScope.StatusCode);
        Assert.Contains("this occurrence or the whole series", await ErrorAsync(noScope));

        // Series scope: content lands on the template AND the event; time re-anchors the schedule.
        var seriesBefore = await GetSeriesAsync(ev.SeriesId!.Value);
        var seriesEdit = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Title: "Whole series title", WhenText: "in 3 days", Scope: EditScope.Series));
        seriesEdit.EnsureSuccessStatusCode();
        var seriesAfter = await GetSeriesAsync(ev.SeriesId.Value);
        Assert.Equal("Whole series title", seriesAfter.Title);
        Assert.NotEqual(seriesBefore.AnchorDate, seriesAfter.AnchorDate);

        // Occurrence scope: the template is untouched.
        var occurrenceEdit = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Title: "Just this one", Scope: EditScope.Occurrence));
        occurrenceEdit.EnsureSuccessStatusCode();
        Assert.Equal("Whole series title", (await GetSeriesAsync(ev.SeriesId.Value)).Title);

        // Non-live occurrences can't edit the series.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Ended));
        }

        var pastSeriesEdit = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Title: "Too late", Scope: EditScope.Series));
        Assert.Equal(HttpStatusCode.Conflict, pastSeriesEdit.StatusCode);

        // And past occurrences edit like one-offs — no scope required.
        var pastPlain = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Title: "History cleanup"));
        pastPlain.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Notification_scope_matrix()
    {
        var ev = await CreateSeriesEventAsync("Notify series", Weekly);

        var noScope = await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30));
        Assert.Equal(HttpStatusCode.BadRequest, noScope.StatusCode);

        // Series scope: spec lands on the template and clones onto the next occurrence.
        var seriesAdd = await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, "warmup", Scope: EditScope.Series));
        Assert.Equal(HttpStatusCode.Created, seriesAdd.StatusCode);

        // Occurrence scope: no spec.
        var occurrenceAdd = await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(10, "one-off ping", Scope: EditScope.Occurrence));
        Assert.Equal(HttpStatusCode.Created, occurrenceAdd.StatusCode);

        var series = await GetSeriesAsync(ev.SeriesId!.Value);
        var spec = Assert.Single(series.NotificationSpecs);
        Assert.Equal(30, spec.MinutesBefore);

        var next = (await SkipAsync(ev.Id)).NextEvent!;
        var cloned = await Client.GetFromJsonAsync<List<EventNotificationDto>>($"/events/{next.Id}/notifications");
        var clonedOne = Assert.Single(cloned!);
        Assert.Equal("warmup", clonedOne.Message); // template spec cloned; the one-off ping wasn't

        // Series-scoped delete retires the spec too, so later occurrences drop it.
        var del = await Client.DeleteAsync($"/events/{next.Id}/notifications/{clonedOne.Id}?scope=Series");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Empty((await GetSeriesAsync(ev.SeriesId.Value)).NotificationSpecs);
    }

    [Fact]
    public async Task Guards_hide_series_from_non_members_and_block_non_creators()
    {
        var ev = await CreateSeriesEventAsync("Guarded", Weekly);

        var (outsider, _) = await fixture.LoginAsync(9960, (123456, "Elsewhere", true));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/series/{ev.SeriesId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"/events/{ev.Id}/skip", null)).StatusCode);

        var (member, _) = await fixture.LoginAsync(9961, (GuildId, "G", false));
        var stop = await member.PostAsync($"/series/{ev.SeriesId}/stop", null);
        Assert.Equal(HttpStatusCode.Forbidden, stop.StatusCode);
        Assert.Contains("series creator or a server manager", await ErrorAsync(stop));
    }

    private Task<HttpResponseMessage> PostEventAsync(CreateEventRequest request) =>
        Client.PostAsJsonAsync($"/guilds/{GuildId}/events", request);

    private async Task<EventDto> CreateSeriesEventAsync(
        string title, RecurrenceRuleDto rule, int? repeatCount = null)
    {
        var response = await PostEventAsync(new CreateEventRequest(
            CreatorId, title, "in 2 hours", ChannelId, Recurrence: rule, RepeatCount: repeatCount));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<SeriesDto> GetSeriesAsync(Guid seriesId)
    {
        var response = await Client.GetAsync($"/series/{seriesId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SeriesDto>())!;
    }

    private async Task<SkipOccurrenceResponse> SkipAsync(Guid eventId)
    {
        var response = await Client.PostAsync($"/events/{eventId}/skip", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!;
    }

    private async Task SetMessageAsync(Guid eventId, long messageId)
    {
        var response = await Client.PutAsJsonAsync($"/events/{eventId}/message",
            new SetEventMessageRequest(ChannelId, messageId));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
}
