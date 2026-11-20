using System.Globalization;
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

    private const RecurrenceDays MonWedFri =
        RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday;

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
        // A daily series whose occurrences run two and a half days, made before such a length was
        // refused. The live one is still running at the slots of the next two days, so the first
        // date listed after it is the third day — not the next, as it would be for an occurrence
        // of an hour. Each occurrence holds the next two dates back as well, so the series is
        // listed date by date, every third day.
        const long guildId = 713;
        var ev = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Long sessions", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day)));
        await MakeLongAsync(ev, 60 * 60);

        var entries = await SeriesEntriesAsync(guildId, ev);

        Assert.DoesNotContain(entries, e => e.Contains("RRULE:"));
        var liveStart = Instant.FromDateTimeOffset(ev.StartsAtUtc).InUtc().Date;
        Assert.Equal(liveStart.PlusDays(3), IcsText.DtStartDate(entries[0], DateTimeZone.Utc));
        Assert.Equal(liveStart.PlusDays(6), IcsText.DtStartDate(entries[1], DateTimeZone.Utc));
    }

    [Fact]
    public async Task A_series_whose_occurrences_hold_every_second_date_back_is_listed_date_by_date()
    {
        // Daily, 4 times, every occurrence a day long, made before such a length was refused: the
        // next date begins as each occurrence ends, so the series posts every second date. The
        // feed lists those, each an entry of its own, and no repeat rule names the others.
        const long guildId = 714;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "A day each", WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 4));
        await MakeLongAsync(live, 24 * 60);

        var entries = await SeriesEntriesAsync(guildId, live);

        Assert.DoesNotContain(entries, e => e.Contains("RRULE:"));
        Assert.Equal([2, 4, 6], entries.Select(e => DaysAfter(monday, e)));
        Assert.All(entries, e => Assert.Equal(24 * 60, IcsText.LengthMinutes(e)));
        Assert.All(entries, e => Assert.Matches(@"DTSTART:\d{8}T180000Z", e));

        // Each entry keeps its UID from one read to the next: the series' own, with the date.
        Assert.All(entries, e => Assert.Contains(
            $"UID:{live.SeriesId}-{IcsText.DtStartDate(e, DateTimeZone.Utc).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}@calcrony", e));
    }

    [Fact]
    public async Task A_day_set_series_whose_occurrences_hold_dates_back_lists_the_dates_it_posts()
    {
        // Mon, Wed, Fri, 6 times, every occurrence two and a half days long. Monday's ends at 06:00
        // on Thursday, past Wednesday's date; Friday's ends at 06:00 on Monday, before Monday's.
        const long guildId = 715;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Two and a half days", WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, MonWedFri), RepeatCount: 6));
        await MakeLongAsync(live, 60 * 60);

        var entries = await SeriesEntriesAsync(guildId, live);

        Assert.DoesNotContain(entries, e => e.Contains("RRULE:"));
        Assert.Equal([4, 7, 11, 14, 18], entries.Select(e => DaysAfter(monday, e)));
    }

    [Fact]
    public async Task A_weekly_series_a_week_long_is_listed_every_second_week()
    {
        const long guildId = 716;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "A week each", WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatCount: 3));
        await MakeLongAsync(live, 7 * 24 * 60);

        var entries = await SeriesEntriesAsync(guildId, live);

        Assert.DoesNotContain(entries, e => e.Contains("RRULE:"));
        Assert.Equal([14, 28], entries.Select(e => DaysAfter(monday, e)));
    }

    [Fact]
    public async Task A_series_without_an_end_listed_date_by_date_is_listed_a_year_ahead()
    {
        // Daily without an end, every occurrence a day long: every second day, from the second
        // day after the live occurrence until a year (366 days) after the feed is read, and none
        // after that.
        const long guildId = 717;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "A day each, no end", WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day)));
        await MakeLongAsync(live, 24 * 60);

        var before = SystemClock.Instance.GetCurrentInstant();
        var entries = await SeriesEntriesAsync(guildId, live);
        var after = SystemClock.Instance.GetCurrentInstant();

        Assert.DoesNotContain(entries, e => e.Contains("RRULE:"));
        var days = entries.Select(e => DaysAfter(monday, e)).ToList();
        Assert.Equal(Enumerable.Range(1, days.Count).Select(k => 2 * k), days);
        var year = Duration.FromDays(366);
        var last = Instant.FromDateTimeOffset(monday.AddDays(days[^1]));
        Assert.True(last < after + year, $"{last} is more than a year after the read");
        Assert.True(last + Duration.FromDays(2) >= before + year, $"{last} leaves out a date within the year");
    }

    [Fact]
    public async Task A_length_within_a_clock_change_of_its_gap_keeps_its_repeat_rule_and_leaves_out_the_date_it_holds_back()
    {
        // Daily at 09:00, 5 times, every occurrence a minute short of a day, from three days before
        // a night the clocks go forward. That night is an hour short, so the occurrence that begins
        // the day before it ends at 09:59 on the next day, after that day's 09:00: the series does
        // not post that date. The feed keeps its one entry with the repeat rule, with that date
        // written into it as an exception, and ends the rule on the last date the series posts, so
        // no count is spent on the date it leaves out.
        const long guildId = 718;
        var (zoneId, zone, change) = await ClocksGoForwardSoonestAsync(guildId);
        var first = change.PlusDays(-3);
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "A minute short of a day", WhenText(first.At(new LocalTime(9, 0))), ChannelId,
            DurationMinutes: 23 * 60 + 59, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 5));

        var entry = Assert.Single(await SeriesEntriesAsync(guildId, live));

        Assert.Contains($"UID:{live.SeriesId}@calcrony", entry);
        Assert.Contains("RRULE:FREQ=DAILY;UNTIL=", entry);
        Assert.DoesNotContain("COUNT=", entry);
        Assert.Contains($"EXDATE;TZID={zoneId}:{change.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}T090000", entry);
        Assert.Equal(
            [first.PlusDays(1), first.PlusDays(2), first.PlusDays(4), first.PlusDays(5)],
            (await ReadStartsAsync(guildId, live, first, first.PlusDays(30), zone)).Select(x => x.InZone(zone).Date));
    }

    [Fact]
    public async Task A_clock_change_series_without_an_end_leaves_out_the_one_date_a_year_it_holds_back()
    {
        // A minute short of a day, every day at 18:00, without an end: within the year ahead, the
        // night the clocks go forward holds back one date, the day of the change, and no other.
        const long guildId = 720;
        var (_, zone, change) = await ClocksGoForwardSoonestAsync(guildId);
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Every day, a minute short", WhenText(change.PlusDays(-3).At(new LocalTime(18, 0))), ChannelId,
            DurationMinutes: 23 * 60 + 59, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day)));

        var entry = Assert.Single(await SeriesEntriesAsync(guildId, live));

        Assert.Contains("RRULE:FREQ=DAILY", entry);
        Assert.DoesNotContain("COUNT=", entry);
        Assert.DoesNotContain("UNTIL=", entry);
        var exception = Assert.Single(entry.Split('\n'), l => l.StartsWith("EXDATE", StringComparison.Ordinal));
        Assert.EndsWith($":{change.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}T180000", exception.TrimEnd('\r'));
        Assert.Equal(
            Enumerable.Range(-2, 30).Select(change.PlusDays).Where(day => day != change),
            (await ReadStartsAsync(guildId, live, change.PlusDays(-2), change.PlusDays(28), zone)).Select(x => x.InZone(zone).Date));
    }

    [Fact]
    public async Task A_clock_change_series_whose_count_runs_past_the_year_counts_only_the_dates_it_posts()
    {
        // 500 times, a minute short of a day: the count runs past the year ahead, so the rule keeps
        // a count, raised by the one date left out within the year, and every other date is listed.
        const long guildId = 721;
        var (_, zone, change) = await ClocksGoForwardSoonestAsync(guildId);
        var first = change.PlusDays(-3);
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Five hundred, a minute short", WhenText(first.At(new LocalTime(18, 0))), ChannelId,
            DurationMinutes: 23 * 60 + 59, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 500));

        var entry = Assert.Single(await SeriesEntriesAsync(guildId, live));

        Assert.Contains("RRULE:FREQ=DAILY;COUNT=500", entry);
        Assert.Equal(
            Enumerable.Range(1, 30).Select(first.PlusDays).Where(day => day != change),
            (await ReadStartsAsync(guildId, live, first, first.PlusDays(31), zone)).Select(x => x.InZone(zone).Date));

        // A calendar app reads 499 dates in all, one for each occurrence left to post: the count
        // counts the date left out as well, so it is raised by one.
        Assert.Equal(499, (await ReadStartsAsync(guildId, live, first, first.PlusDays(600), zone)).Count);
    }

    [Fact]
    public async Task An_entry_listed_on_its_own_carries_the_series_text_and_the_date_it_has_where_it_happens()
    {
        // Daily at 19:00 Chicago time, a day long (made before such a length was refused), with a
        // place and a description. 19:00 there is 00:00 or 01:00 UTC the next day: the entry's UID
        // carries the date in Chicago, and its start is written in UTC.
        const long guildId = 723;
        const string zoneId = "America/Chicago";
        (await Client.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto(zoneId, ChannelId))).EnsureSuccessStatusCode();
        var first = LocalDate.FromDateTime(AMondayEvening().UtcDateTime).At(new LocalTime(19, 0));
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Evening in Chicago", WhenText(first), ChannelId, Description: "bring dice", Location: "Back room",
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 3));
        await MakeLongAsync(live, 24 * 60);

        // The series posts every second date: the first entry after the posted occurrence is the
        // date two days on.
        var entry = (await SeriesEntriesAsync(guildId, live))[0];

        Assert.Contains($"UID:{live.SeriesId}-{first.Date.PlusDays(2).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}@calcrony", entry);
        Assert.Contains($"DTSTART:{first.Date.PlusDays(3).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}T0", entry);
        Assert.Contains("LOCATION:Back room", entry);
        Assert.Contains("bring dice", entry);
        Assert.Contains($"URL:https://web.test/app/guilds/{guildId}/events", entry);
    }

    [Fact]
    public async Task A_count_raised_by_a_date_left_out_stops_at_the_largest_count_there_is()
    {
        // A count no route stores, as large as a count can be, on a series a minute short of a day:
        // raised by the date the night the clocks go forward holds back, it stays the largest
        // count there is.
        const long guildId = 724;
        var (_, _, change) = await ClocksGoForwardSoonestAsync(guildId);
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "As many as there can be", WhenText(change.PlusDays(-3).At(new LocalTime(18, 0))), ChannelId,
            DurationMinutes: 23 * 60 + 59, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 10));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.EventSeries.Where(s => s.Id == live.SeriesId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.MaxOccurrences, int.MaxValue)
                .SetProperty(x => x.OccurrenceCount, 0));
        }

        var entry = Assert.Single(await SeriesEntriesAsync(guildId, live));

        Assert.Contains($"COUNT={int.MaxValue}", entry);
        Assert.Contains("EXDATE", entry);
    }

    [Fact]
    public async Task A_series_whose_rule_cannot_be_stepped_is_left_out_and_the_feed_still_answers()
    {
        // A repeat rule no route stores — every 0 days — cannot be stepped. The feed leaves that
        // series' upcoming dates out and lists everything else: its posted occurrence, and the
        // other series.
        const long guildId = 722;
        var broken = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Cannot be stepped", "in 6 hours", ChannelId, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day)));
        var fine = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Steps fine", "in 7 hours", ChannelId, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day)));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.EventSeries.Where(s => s.Id == broken.SeriesId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Interval, 0));
        }

        var entries = await FetchEntriesAsync(guildId);

        Assert.Contains(entries, e => e.Contains($"UID:{broken.Id}@calcrony"));
        Assert.DoesNotContain(entries, e => e.Contains($"UID:{broken.SeriesId}"));
        Assert.Contains(entries, e => e.Contains($"UID:{fine.SeriesId}@calcrony"));
    }

    [Fact]
    public async Task A_series_whose_length_cannot_reach_its_next_date_keeps_one_entry_with_the_repeat_rule()
    {
        // 23 hours a day in UTC, whose clocks never change: no occurrence reaches the next date,
        // so the series keeps its one entry with the repeat rule, as before.
        const long guildId = 719;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Twenty-three hours", WhenText(monday), ChannelId, DurationMinutes: 23 * 60,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 4));

        var entry = Assert.Single(await SeriesEntriesAsync(guildId, live));

        Assert.Contains($"UID:{live.SeriesId}@calcrony", entry);
        Assert.Contains("RRULE:FREQ=DAILY;COUNT=3", entry);
        Assert.Equal(1, DaysAfter(monday, entry));
    }

    /// <summary>Gives a series and its live occurrence a length in the database, as a series made
    /// before a length that reaches its next date was refused at creation.</summary>
    private async Task MakeLongAsync(EventDto live, int minutes)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.EventSeries.Where(s => s.Id == live.SeriesId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DurationMinutes, minutes));
        await db.Events.Where(e => e.Id == live.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DurationMinutes, minutes));
    }

    /// <summary>The entries a server's feed has for a series, other than its live occurrence's,
    /// in start order.</summary>
    private async Task<List<string>> SeriesEntriesAsync(long guildId, EventDto live) =>
        [.. (await FetchEntriesAsync(guildId))
            .Where(e => e.Contains($"UID:{live.SeriesId}"))
            .OrderBy(e => IcsText.DtStartDate(e, DateTimeZone.Utc))];

    /// <summary>Of two zones whose clocks go forward half a year apart — New York in March, Sydney
    /// in October — the one whose clocks go forward soonest after a week from now, set as a
    /// server's zone, with the date of that night. That night is never more than about seven months
    /// away, well inside the year the feed looks ahead, whatever day a test runs on.</summary>
    private async Task<(string ZoneId, DateTimeZone Zone, LocalDate Change)> ClocksGoForwardSoonestAsync(long guildId)
    {
        var from = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(7);
        var (zoneId, change) = new[] { "America/New_York", "Australia/Sydney" }
            .Select(id =>
            {
                var intervals = DateTimeZoneProviders.Tzdb[id].GetZoneIntervals(from, from + Duration.FromDays(400)).ToList();
                return (Id: id, At: intervals.Zip(intervals.Skip(1)).First(pair => pair.Second.WallOffset > pair.First.WallOffset).Second.Start);
            })
            .MinBy(found => found.At);
        var zone = DateTimeZoneProviders.Tzdb[zoneId];
        (await Client.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto(zoneId, ChannelId))).EnsureSuccessStatusCode();
        return (zoneId, zone, change.InZone(zone).Date);
    }

    /// <summary>The starts a calendar app reads for a series from a server's feed, other than its
    /// live occurrence's: its entries read back and expanded by Ical.Net, between two dates.</summary>
    private async Task<List<Instant>> ReadStartsAsync(long guildId, EventDto live, LocalDate from, LocalDate until, DateTimeZone zone)
    {
        var token = await Client.PostAsync($"/guilds/{guildId}/feed-token", null);
        token.EnsureSuccessStatusCode();
        using var anonymous = fixture.Factory.CreateClient();
        var ics = await anonymous.GetStringAsync((await token.Content.ReadFromJsonAsync<FeedTokenDto>())!.Path);
        var stop = until.AtStartOfDayInZone(zone).ToInstant().ToDateTimeUtc();
        return [.. Ical.Net.Calendar.Load(ics)!.Events
            .Where(e => e.Uid is { } uid && uid.StartsWith(live.SeriesId!.Value.ToString(), StringComparison.Ordinal))
            .SelectMany(e => e.GetOccurrences(new Ical.Net.DataTypes.CalDateTime(from.AtStartOfDayInZone(zone).ToInstant().ToDateTimeUtc()))
                .TakeWhile(o => o.Period.StartTime.AsUtc < stop))
            .Select(o => Instant.FromDateTimeUtc(DateTime.SpecifyKind(o.Period.StartTime.AsUtc, DateTimeKind.Utc)))
            .Order()];
    }

    /// <summary>The days from <paramref name="monday"/> to the start of an entry, in UTC.</summary>
    private static int DaysAfter(DateTimeOffset monday, string entry) =>
        Period.DaysBetween(LocalDate.FromDateTime(monday.UtcDateTime), IcsText.DtStartDate(entry, DateTimeZone.Utc));

    /// <summary>18:00 UTC on a Monday one to two weeks ahead, so a test's dates fall on the same
    /// weekdays whatever day it runs on.</summary>
    private static DateTimeOffset AMondayEvening()
    {
        var today = DateTimeOffset.UtcNow.Date;
        var monday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7 + 7);
        return new DateTimeOffset(monday.AddHours(18), TimeSpan.Zero);
    }

    private static string WhenText(DateTimeOffset at) =>
        at.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture);

    private static string WhenText(LocalDateTime at) =>
        at.ToDateTimeUnspecified().ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture);

    private async Task<EventDto> CreateAsync(long guildId, CreateEventRequest request)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
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
