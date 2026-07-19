using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class FeedTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 700;
    private const long ChannelId = 701;
    private const long CreatorId = 702;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Feed_token_is_stable_and_serves_ics_anonymously()
    {
        var create = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Feed Party", "in 6 hours", ChannelId, Description: "bring dip", Location: "The couch"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var first = await ReadTokenAsync();
        var second = await ReadTokenAsync();
        Assert.Equal(first.Token, second.Token);

        // The feed itself requires no API key.
        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(first.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/calendar", response.Content.Headers.ContentType!.MediaType);
        var ics = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", ics);
        Assert.Contains("SUMMARY:Feed Party", ics);
        Assert.Contains("LOCATION:The couch", ics);
        Assert.Contains("END:VCALENDAR", ics);
    }

    [Fact]
    public async Task Unknown_feed_token_is_not_found()
    {
        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync("/feeds/deadbeefdeadbeefdeadbeefdeadbeefdeadbeef.ics");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Running_series_emit_one_rrule_vevent_instead_of_the_live_occurrence()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Weekly Standup", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, 2)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        var ics = await FetchFeedAsync();

        Assert.Contains($"UID:{ev.SeriesId}@calcrony", ics);
        Assert.Contains("RRULE:", ics);
        Assert.Contains("FREQ=WEEKLY", ics);
        Assert.Contains("INTERVAL=2", ics);
        // The live occurrence is represented by the series VEVENT — never doubled.
        Assert.DoesNotContain($"UID:{ev.Id}@calcrony", ics);
    }

    [Fact]
    public async Task Past_occurrences_stay_concrete_and_count_series_emit_count()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Counted Series", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatCount: 5));
        var first = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        // Skip: the first occurrence becomes history-adjacent (Cancelled — excluded), the
        // replacement becomes the live anchor. Then past-date the replacement's predecessor…
        var skip = await Client.PostAsync($"/events/{first.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;

        // …by ending a concrete past row directly: mark the live one Ended and past-dated, so the
        // sweep-free feed shows it as history while the series projects from a computed next.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(2));
            await db.Events.Where(e => e.Id == next.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.StartsAt, past)
                    .SetProperty(e => e.Status, EventStatus.Ended));
        }

        var ics = await FetchFeedAsync();

        // The ended occurrence is concrete history; the series still projects with a COUNT.
        Assert.Contains($"UID:{next.Id}@calcrony", ics);
        Assert.Contains($"UID:{first.SeriesId}@calcrony", ics);
        Assert.Contains("COUNT=", ics);
    }

    [Fact]
    public async Task Until_series_emit_until_and_stopped_series_lose_their_rrule()
    {
        var untilCreate = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Until Series", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatUntilText: "in 10 weeks"));
        untilCreate.EnsureSuccessStatusCode();

        var stoppedCreate = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Stopped Series", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        var stopped = (await stoppedCreate.Content.ReadFromJsonAsync<EventDto>())!;
        (await Client.PostAsync($"/series/{stopped.SeriesId}/stop", null)).EnsureSuccessStatusCode();

        var ics = await FetchFeedAsync();

        Assert.Contains("UNTIL=", ics);
        // Stopped: the surviving occurrence returns as a concrete event; no series VEVENT.
        Assert.Contains($"UID:{stopped.Id}@calcrony", ics);
        Assert.DoesNotContain($"UID:{stopped.SeriesId}@calcrony", ics);
    }

    [Fact]
    public async Task Count_exhausted_series_awaiting_its_ended_sweep_does_not_project()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Exhausted Series", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatCount: 2));
        var first = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var skip = await Client.PostAsync($"/events/{first.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var second = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;

        // The final occurrence ends, but the sweep hasn't marked the series Ended yet: the gap
        // path must not resurrect it with a phantom projected instance.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Events.Where(e => e.Id == second.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Ended));
        }

        var ics = await FetchFeedAsync();
        Assert.DoesNotContain($"UID:{first.SeriesId}@calcrony", ics);
    }

    [Fact]
    public async Task Series_vevents_carry_the_series_zone_so_projections_survive_dst()
    {
        await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings",
            new GuildSettingsDto("America/Chicago", ChannelId));
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Zoned Series", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        create.EnsureSuccessStatusCode();

        var ics = await FetchFeedAsync();

        Assert.Contains("DTSTART;TZID=America/Chicago", ics);
        Assert.Contains("BEGIN:VTIMEZONE", ics);
        Assert.Contains("TZID:America/Chicago", ics);
    }

    private async Task<string> FetchFeedAsync()
    {
        var token = await ReadTokenAsync();
        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(token.Path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<FeedTokenDto> ReadTokenAsync()
    {
        var response = await Client.PostAsync($"/guilds/{GuildId}/feed-token", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedTokenDto>())!;
    }
}
