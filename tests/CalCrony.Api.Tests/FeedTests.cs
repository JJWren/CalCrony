using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Extensions;

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
    public async Task Running_series_emit_the_live_occurrence_concretely_and_project_from_the_next_slot()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Weekly Standup", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, 2)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var zone = DateTimeZoneProviders.Tzdb[ev.TimeZone];
        var anchor = ev.StartsAtUtc.ToInstant().InZone(zone).Date;

        var ics = await FetchFeedAsync();

        Assert.Contains($"UID:{ev.SeriesId}@calcrony", ics);
        Assert.Contains("RRULE:", ics);
        Assert.Contains("FREQ=WEEKLY", ics);
        Assert.Contains("INTERVAL=2", ics);
        // The live occurrence is a concrete VEVENT; the series projects from the NEXT slot, so
        // the two never overlap and the RRULE's phase is the engine's, not the live row's.
        Assert.Contains($"UID:{ev.Id}@calcrony", ics);
        Assert.Equal(anchor, IcsText.DtStartDate(ics, $"{ev.Id}@calcrony", zone));
        Assert.Equal(anchor.PlusWeeks(2), IcsText.DtStartDate(ics, $"{ev.SeriesId}@calcrony", zone));
    }

    [Fact]
    public async Task Occurrence_scoped_move_of_the_live_row_neither_doubles_it_nor_projects_slots_before_it()
    {
        const string title = "Moved Live Row";
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var zone = DateTimeZoneProviders.Tzdb[ev.TimeZone];
        var first = ev.StartsAtUtc.ToInstant().InZone(zone);
        var anchor = first.Date;

        // Move the live occurrence onto the NEXT nominal slot the way an Occurrence-scoped edit
        // does: only StartsAt changes, the series cursor stays on the anchor slot.
        await MoveLiveAsync(ev.Id, (anchor.PlusWeeks(1) + first.TimeOfDay).InZoneLeniently(zone).ToInstant());
        var ics = await FetchFeedAsync();
        var mine = IcsText.Events(ics).Where(b => b.Contains($"SUMMARY:{title}")).ToList();

        // Exactly two VEVENTs for this series — the moved live row and the projection — and the
        // projection starts AFTER the live row, so the moved date appears once.
        Assert.Equal(2, mine.Count);
        Assert.Equal(anchor.PlusWeeks(1), IcsText.DtStartDate(ics, $"{ev.Id}@calcrony", zone));
        Assert.Equal(anchor.PlusWeeks(2), IcsText.DtStartDate(ics, $"{ev.SeriesId}@calcrony", zone));
        Assert.Single(mine, b => IcsText.DtStartDate(b, zone) == anchor.PlusWeeks(1));

        // Two slots ahead: the skipped-over nominal slot is never projected — the scheduler can't
        // materialize it while the moved row stays live.
        await MoveLiveAsync(ev.Id, (anchor.PlusWeeks(2) + first.TimeOfDay).InZoneLeniently(zone).ToInstant());
        ics = await FetchFeedAsync();
        mine = IcsText.Events(ics).Where(b => b.Contains($"SUMMARY:{title}")).ToList();

        Assert.Equal(2, mine.Count);
        Assert.Equal(anchor.PlusWeeks(2), IcsText.DtStartDate(ics, $"{ev.Id}@calcrony", zone));
        Assert.Equal(anchor.PlusWeeks(3), IcsText.DtStartDate(ics, $"{ev.SeriesId}@calcrony", zone));
        Assert.DoesNotContain(mine, b => IcsText.DtStartDate(b, zone) == anchor.PlusWeeks(1));
    }

    [Fact]
    public async Task Interval_edit_keeps_the_projected_series_on_the_engines_grid()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Phase Shift", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, 2)));
        var first = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var zone = DateTimeZoneProviders.Tzdb[first.TimeZone];
        var anchor = first.StartsAtUtc.ToInstant().InZone(zone).Date;

        // Skip: the live occurrence now sits in week 2 of the anchor's every-2-weeks grid…
        var skip = await Client.PostAsync($"/events/{first.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var live = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.Equal(anchor.PlusWeeks(2), live.StartsAtUtc.ToInstant().InZone(zone).Date);

        // …then the rule becomes every 3 weeks. The engine's next slot is week 3 (anchor-based);
        // an RRULE anchored on the week-2 live row would have claimed week 5 instead.
        var edit = await Client.PatchAsJsonAsync($"/series/{first.SeriesId}", new UpdateSeriesRequest(Interval: 3));
        edit.EnsureSuccessStatusCode();

        var ics = await FetchFeedAsync();

        Assert.Equal(anchor.PlusWeeks(2), IcsText.DtStartDate(ics, $"{live.Id}@calcrony", zone));
        Assert.Equal(anchor.PlusWeeks(3), IcsText.DtStartDate(ics, $"{first.SeriesId}@calcrony", zone));
        Assert.Contains("INTERVAL=3", ics);
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

    [Fact]
    public async Task Calendar_is_named_after_the_guild_once_a_snapshot_exists()
    {
        var before = Unfold(await FetchFeedAsync());
        Assert.Contains("X-WR-CALNAME:CalCrony events", before);

        var named = await Client.PutAsJsonAsync(
            $"/guilds/{GuildId}/presence", new GuildPresenceRequest(true, "The Grove"));
        named.EnsureSuccessStatusCode();

        var after = Unfold(await FetchFeedAsync());
        Assert.Contains("X-WR-CALNAME:CalCrony · The Grove", after);
    }

    [Fact]
    public async Task Descriptions_carry_channel_web_link_and_discord_jump_link()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Context Party", "in 6 hours", ChannelId, Description: "bring chips"));
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var posted = await Client.PutAsJsonAsync($"/events/{ev.Id}/message",
            new SetEventMessageRequest(ChannelId, 987654, "events-hall"));
        posted.EnsureSuccessStatusCode();

        // A second event with no posted message must not render a Discord jump link.
        var linkless = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Linkless", "in 7 hours", ChannelId));
        linkless.EnsureSuccessStatusCode();

        var ics = Unfold(await FetchFeedAsync());

        Assert.Contains("📍 #events-hall", ics);
        Assert.Contains($"🔗 Event page: https://web.test/app/events/{ev.Id}", ics);
        Assert.Contains($"💬 Open in Discord: https://discord.com/channels/{GuildId}/{ChannelId}/987654", ics);
        Assert.Contains($"URL:https://web.test/app/events/{ev.Id}", ics);
        // Exactly one event has a message, so exactly one jump link renders.
        Assert.Equal(2, ics.Split("Open in Discord").Length);
    }

    [Fact]
    public async Task Series_vevents_link_to_the_guild_events_list_not_the_live_occurrence()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Linked Series", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        create.EnsureSuccessStatusCode();

        var ics = Unfold(await FetchFeedAsync());

        Assert.Contains($"🔗 Events page: https://web.test/app/guilds/{GuildId}/events", ics);
        Assert.Contains($"URL:https://web.test/app/guilds/{GuildId}/events", ics);
    }

    // Servers of their own, so the zone set here reaches no other test. A series entry is written
    // in its series' zone: with a Z for a server in UTC, in local time with a TZID for another.
    [Theory]
    [InlineData(710, null)]
    [InlineData(711, "America/Chicago")]
    public async Task Every_entry_ends_its_length_after_its_start(long guildId, string? zone)
    {
        if (zone is not null)
        {
            var settings = await Client.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto(zone, ChannelId));
            settings.EnsureSuccessStatusCode();
        }

        // An event with a length, one with none set (which runs an hour), and a series, whose
        // live occurrence is an entry of its own beside the one that carries the rule.
        foreach (var request in new[]
                 {
                     new CreateEventRequest(CreatorId, "Ninety minutes", "in 6 hours", ChannelId, DurationMinutes: 90),
                     new CreateEventRequest(CreatorId, "No length set", "in 7 hours", ChannelId),
                     new CreateEventRequest(
                         CreatorId, "Two-hour series", "in 8 hours", ChannelId, DurationMinutes: 120,
                         Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)),
                 })
        {
            var created = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var entries = await FetchEntriesAsync(guildId);

        Assert.Equal(90, IcsText.LengthMinutes(Assert.Single(entries, e => e.Contains("SUMMARY:Ninety minutes"))));
        Assert.Equal(60, IcsText.LengthMinutes(Assert.Single(entries, e => e.Contains("SUMMARY:No length set"))));

        var series = entries.Where(e => e.Contains("SUMMARY:Two-hour series")).ToList();
        Assert.Equal(2, series.Count);
        var rule = Assert.Single(series, e => e.Contains("RRULE:"));
        Assert.All(series, e => Assert.Equal(120, IcsText.LengthMinutes(e)));
        Assert.Contains(zone is null ? "DTEND:" : $"DTEND;TZID={zone}:", rule);
    }

    [Fact]
    public async Task A_series_whose_stored_zone_is_not_known_is_written_in_utc_and_ends_its_length_after_its_start()
    {
        const long guildId = 712;
        var created = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", new CreateEventRequest(
            CreatorId, "Zone not known", "in 8 hours", ChannelId, DurationMinutes: 120,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ev = (await created.Content.ReadFromJsonAsync<EventDto>())!;

        // A zone id the zone database does not have, which the API never stores. The schedule is
        // then read in UTC, and the feed writes the entry on a path of its own.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.EventSeries.Where(s => s.Id == ev.SeriesId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TimeZone, "Nowhere/Unknown"));
        }

        var rule = Assert.Single(await FetchEntriesAsync(guildId), e => e.Contains("RRULE:"));

        Assert.Contains("DTSTART:", rule);
        Assert.Contains("DTEND:", rule);
        Assert.DoesNotContain("TZID", rule);
        Assert.Equal(120, IcsText.LengthMinutes(rule));
    }

    [Fact]
    public async Task A_series_is_projected_from_the_end_of_its_live_occurrence_by_that_occurrences_own_length()
    {
        // A daily series whose occurrences run two and a half days. The live one is still running
        // at the slots of the next two days, so the entry that carries the rule starts on the
        // third day — not on the next, as it would for an occurrence of an hour.
        const long guildId = 713;
        var created = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", new CreateEventRequest(
            CreatorId, "Long sessions", "in 6 hours", ChannelId, DurationMinutes: 60 * 60,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ev = (await created.Content.ReadFromJsonAsync<EventDto>())!;

        var rule = Assert.Single(await FetchEntriesAsync(guildId), e => e.Contains("RRULE:"));

        var liveStart = Instant.FromDateTimeOffset(ev.StartsAtUtc).InUtc().Date;
        Assert.Equal(liveStart.PlusDays(3), IcsText.DtStartDate(rule, DateTimeZone.Utc));
    }

    /// <summary>Re-times a live occurrence without touching its series — what an
    /// Occurrence-scoped time edit does, minus the natural-language parse.</summary>
    private async Task MoveLiveAsync(Guid eventId, Instant startsAt)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.Events.Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, startsAt));
    }

    /// <summary>Reverses RFC 5545 line folding so assertions can match full URLs.</summary>
    private static string Unfold(string ics) => ics.Replace("\r\n ", "");

    private async Task<string> FetchFeedAsync()
    {
        var token = await ReadTokenAsync();
        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(token.Path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The VEVENT blocks of another server's feed, for tests that keep to a server of
    /// their own.</summary>
    private async Task<List<string>> FetchEntriesAsync(long guildId)
    {
        var token = await Client.PostAsync($"/guilds/{guildId}/feed-token", null);
        token.EnsureSuccessStatusCode();
        using var anonymous = fixture.Factory.CreateClient();
        var ics = await anonymous.GetStringAsync((await token.Content.ReadFromJsonAsync<FeedTokenDto>())!.Path);
        return [.. IcsText.Events(ics)];
    }

    private async Task<FeedTokenDto> ReadTokenAsync()
    {
        var response = await Client.PostAsync($"/guilds/{GuildId}/feed-token", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedTokenDto>())!;
    }
}
