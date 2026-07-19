using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class SeriesEditApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9800;
    private const long ChannelId = 9801;
    private const long CreatorId = 9802;

    private HttpClient Client => fixture.Client;

    private static readonly RecurrenceRuleDto Weekly = new(RecurrenceUnit.Week);

    [Fact]
    public async Task Patch_validates_inputs()
    {
        var ev = await CreateSeriesEventAsync("Validate", Weekly);
        var id = ev.SeriesId!.Value;

        var badInterval = await PatchAsync(id, new UpdateSeriesRequest(Interval: 13));
        Assert.Equal(HttpStatusCode.BadRequest, badInterval.StatusCode);
        Assert.Contains("between 1 and 12", await ErrorAsync(badInterval));

        var untilWithoutChoice = await PatchAsync(id, new UpdateSeriesRequest(RepeatUntilText: "in 2 weeks"));
        Assert.Equal(HttpStatusCode.BadRequest, untilWithoutChoice.StatusCode);
        Assert.Contains("Choose an end option", await ErrorAsync(untilWithoutChoice));

        var untilPlusCount = await PatchAsync(id, new UpdateSeriesRequest(
            End: SeriesEndChoice.Until, RepeatUntilText: "in 2 weeks", RepeatCount: 5));
        Assert.Equal(HttpStatusCode.BadRequest, untilPlusCount.StatusCode);
        Assert.Contains("not both", await ErrorAsync(untilPlusCount));

        var untilMissing = await PatchAsync(id, new UpdateSeriesRequest(End: SeriesEndChoice.Until));
        Assert.Equal(HttpStatusCode.BadRequest, untilMissing.StatusCode);
        Assert.Contains("Enter the date", await ErrorAsync(untilMissing));

        var countMissing = await PatchAsync(id, new UpdateSeriesRequest(End: SeriesEndChoice.Count));
        Assert.Equal(HttpStatusCode.BadRequest, countMissing.StatusCode);
        Assert.Contains("Enter how many times", await ErrorAsync(countMissing));

        var badUntil = await PatchAsync(id, new UpdateSeriesRequest(
            End: SeriesEndChoice.Until, RepeatUntilText: "flurble"));
        Assert.Equal(HttpStatusCode.BadRequest, badUntil.StatusCode);

        var pastUntil = await PatchAsync(id, new UpdateSeriesRequest(
            End: SeriesEndChoice.Until, RepeatUntilText: "yesterday 5pm"));
        Assert.Equal(HttpStatusCode.BadRequest, pastUntil.StatusCode);
        Assert.Contains("in the past", await ErrorAsync(pastUntil));
    }

    [Fact]
    public async Task Patch_rule_change_updates_summary_and_next_spawn_without_moving_live()
    {
        var ev = await CreateSeriesEventAsync("Rule change", Weekly);

        var patch = await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(
            Unit: RecurrenceUnit.Day, Interval: 2, MonthlyMode: MonthlyMode.DayOfMonth));
        patch.EnsureSuccessStatusCode();
        var updated = (await patch.Content.ReadFromJsonAsync<SeriesDto>())!;
        Assert.Equal("Repeats every 2 days", updated.Summary);

        // The live occurrence didn't move…
        var live = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(ev.StartsAtUtc, live!.StartsAtUtc);

        // …but the next spawn follows the new 2-day grid from the cursor.
        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.Equal(ev.StartsAtUtc.AddDays(2), next.StartsAtUtc);
    }

    [Fact]
    public async Task Patch_monthly_nth_weekday_derives_from_anchor()
    {
        var ev = await CreateSeriesEventAsync("Nth weekday", Weekly);

        var patch = await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(
            Unit: RecurrenceUnit.Month, MonthlyMode: MonthlyMode.NthWeekday));
        patch.EnsureSuccessStatusCode();
        var updated = (await patch.Content.ReadFromJsonAsync<SeriesDto>())!;
        Assert.Matches("Repeats monthly on the (1st|2nd|3rd|4th|last) ", updated.Summary);
    }

    [Fact]
    public async Task Patch_end_condition_transitions()
    {
        var ev = await CreateSeriesEventAsync("Transitions", Weekly);
        var id = ev.SeriesId!.Value;

        var until = await ReadSeriesAsync(await PatchAsync(id, new UpdateSeriesRequest(
            End: SeriesEndChoice.Until, RepeatUntilText: "in 10 weeks")));
        Assert.NotNull(until.UntilDate);
        Assert.Null(until.MaxOccurrences);
        Assert.Contains("until", until.Summary);

        var counted = await ReadSeriesAsync(await PatchAsync(id, new UpdateSeriesRequest(
            End: SeriesEndChoice.Count, RepeatCount: 10)));
        Assert.Null(counted.UntilDate);
        Assert.Equal(10, counted.MaxOccurrences);
        Assert.Contains("1 of 10", counted.Summary);

        var never = await ReadSeriesAsync(await PatchAsync(id, new UpdateSeriesRequest(End: SeriesEndChoice.Never)));
        Assert.Null(never.UntilDate);
        Assert.Null(never.MaxOccurrences);
        Assert.DoesNotContain("·", never.Summary);
    }

    [Fact]
    public async Task Patch_count_at_or_below_occurrence_count_rejected()
    {
        var ev = await CreateSeriesEventAsync("Count floor", Weekly);
        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null); // OccurrenceCount -> 2
        skip.EnsureSuccessStatusCode();

        var patch = await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(
            End: SeriesEndChoice.Count, RepeatCount: 2));
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Contains("already run 2 times", await ErrorAsync(patch));
    }

    [Fact]
    public async Task Patch_leaving_no_future_occurrences_rejected_and_state_untouched()
    {
        // Weekly live at +7d after a skip; until "tomorrow" can't fit another slot.
        var ev = await CreateSeriesEventAsync("No future", Weekly);
        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();

        var patch = await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(
            End: SeriesEndChoice.Until, RepeatUntilText: "tomorrow"));
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Contains("no upcoming occurrences", await ErrorAsync(patch));

        var series = await GetSeriesAsync(ev.SeriesId.Value);
        Assert.Null(series.UntilDate); // half-applied edits never persist
        Assert.False(series.Ended);
    }

    [Fact]
    public async Task Revive_from_stop_via_empty_patch()
    {
        var ev = await CreateSeriesEventAsync("Stopped", Weekly);
        (await Client.PostAsync($"/series/{ev.SeriesId}/stop", null)).EnsureSuccessStatusCode();

        var revived = await ReadSeriesAsync(await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest()));
        Assert.False(revived.Ended);
        Assert.Equal(ev.Id, revived.LiveEventId); // the surviving occurrence continues the series

        var live = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.NotNull(live!.RecurrenceSummary); // 🔁 comes back
    }

    [Fact]
    public async Task Revive_from_count_spawns_via_sweep()
    {
        var ev = await CreateSeriesEventAsync("Count revive", Weekly, repeatCount: 2);
        var first = await SkipAsync(ev.Id);
        var second = await SkipAsync(first.NextEvent!.Id);
        Assert.True(second.Series.Ended);

        var revived = await ReadSeriesAsync(await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(
            End: SeriesEndChoice.Count, RepeatCount: 4)));
        Assert.False(revived.Ended);
        Assert.Null(revived.LiveEventId);

        await SweepAsync();
        var after = await GetSeriesAsync(ev.SeriesId.Value);
        Assert.NotNull(after.LiveEventId);
        Assert.Equal(3, after.OccurrenceCount);
        Assert.Contains("3 of 4", after.Summary);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.True(await db.Deliveries.AnyAsync(d =>
            d.Type == DeliveryType.PostEventMessage && d.PayloadJson.Contains(after.LiveEventId.ToString()!)));
    }

    [Fact]
    public async Task Keep_only_patch_on_exhausted_series_rejected_and_stays_ended()
    {
        var ev = await CreateSeriesEventAsync("Still done", Weekly, repeatCount: 2);
        var first = await SkipAsync(ev.Id);
        await SkipAsync(first.NextEvent!.Id); // count exhausted -> Ended

        // Interval-only edit keeps the exhausted count: revive must be refused, and the
        // count-floor check names the actual blocker (the count, not the interval).
        var patch = await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(Interval: 2));
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Contains("already run 2 times", await ErrorAsync(patch));
        Assert.True((await GetSeriesAsync(ev.SeriesId.Value)).Ended);
    }

    [Fact]
    public async Task Revive_with_live_does_not_double_spawn()
    {
        var ev = await CreateSeriesEventAsync("Safe revive", Weekly);
        (await Client.PostAsync($"/series/{ev.SeriesId}/stop", null)).EnsureSuccessStatusCode();
        (await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest())).EnsureSuccessStatusCode();

        await SweepAsync();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var liveCount = await db.Events.CountAsync(e =>
            e.SeriesId == ev.SeriesId
            && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started));
        Assert.Equal(1, liveCount);
    }

    [Fact]
    public async Task Web_patch_enqueues_embed_sync_and_bot_does_not()
    {
        await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        var (member, session) = await fixture.LoginAsync(9850, (GuildId, "G", false));
        var create = await member.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            0, "Web schedule", "in 2 hours", 0, Recurrence: Weekly));
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/message",
            new SetEventMessageRequest(ChannelId, 888100))).EnsureSuccessStatusCode();

        (await member.PatchAsJsonAsync($"/series/{ev.SeriesId}",
            new UpdateSeriesRequest(Interval: 2))).EnsureSuccessStatusCode();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            Assert.Equal(1, await db.Deliveries.CountAsync(d =>
                d.Type == DeliveryType.SyncEventMessage && d.PayloadJson.Contains(ev.Id.ToString())));
        }

        (await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(Interval: 3))).EnsureSuccessStatusCode();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            Assert.Equal(1, await db.Deliveries.CountAsync(d =>
                d.Type == DeliveryType.SyncEventMessage && d.PayloadJson.Contains(ev.Id.ToString())));
        }

        _ = session;
    }

    [Fact]
    public async Task Guards_hide_patch_from_outsiders_and_block_non_creators()
    {
        var ev = await CreateSeriesEventAsync("Guarded edit", Weekly);

        var (outsider, _) = await fixture.LoginAsync(9860, (123456, "Elsewhere", true));
        var probe = await outsider.PatchAsJsonAsync($"/series/{ev.SeriesId}", new UpdateSeriesRequest(Interval: 2));
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);

        var (member, _) = await fixture.LoginAsync(9861, (GuildId, "G", false));
        var forbidden = await member.PatchAsJsonAsync($"/series/{ev.SeriesId}", new UpdateSeriesRequest(Interval: 2));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("series creator or a server manager", await ErrorAsync(forbidden));
    }

    private Task<HttpResponseMessage> PatchAsync(Guid seriesId, UpdateSeriesRequest request) =>
        Client.PatchAsJsonAsync($"/series/{seriesId}", request);

    private async Task<EventDto> CreateSeriesEventAsync(string title, RecurrenceRuleDto rule, int? repeatCount = null)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
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

    private static async Task<SeriesDto> ReadSeriesAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SeriesDto>())!;
    }

    private async Task<SkipOccurrenceResponse> SkipAsync(Guid eventId)
    {
        var response = await Client.PostAsync($"/events/{eventId}/skip", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!;
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
}
