using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The opt-in public web calendar (issue #121): default off, slug lifecycle (mint /
/// regenerate / disable), manager-only changes, and the anonymous month view — what it shows,
/// what it must never show, and how running series project into it.</summary>
public class PublicCalendarApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long ChannelId = 12101;
    private const long CreatorId = 12102;

    private const RecurrenceDays MonWedFri =
        RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday;

    /// <summary>How many weeks past a series' first occurrence the day-set tests look.</summary>
    private const int SweepWeeks = 10;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Default_is_off_and_unknown_or_malformed_slugs_are_not_found()
    {
        const long guildId = 12100;
        var settings = await ReadAsync<PublicCalendarSettingsDto>(await Client.GetAsync($"/guilds/{guildId}/public-calendar"));
        Assert.False(settings.Enabled);
        Assert.Null(settings.Slug);
        Assert.Null(settings.Path);

        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/public/calendars/{new string('0', 32)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/public/calendars/not-a-slug")).StatusCode);
    }

    [Fact]
    public async Task Enabling_mints_a_stable_slug_regenerating_revokes_it_and_disabling_removes_it()
    {
        const long guildId = 12110;
        var enabled = await PutAsync(guildId, new PublicCalendarRequest(true));
        Assert.True(enabled.Enabled);
        Assert.Matches("^[0-9a-f]{32}$", enabled.Slug);
        Assert.Equal($"/c/{enabled.Slug}", enabled.Path);

        // Enabling again keeps the link — only an explicit regenerate breaks shared URLs.
        Assert.Equal(enabled.Slug, (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug);
        Assert.Equal(enabled.Slug, (await ReadAsync<PublicCalendarSettingsDto>(
            await Client.GetAsync($"/guilds/{guildId}/public-calendar"))).Slug);

        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/public/calendars/{enabled.Slug}")).StatusCode);

        var regenerated = await PutAsync(guildId, new PublicCalendarRequest(true, Regenerate: true));
        Assert.NotEqual(enabled.Slug, regenerated.Slug);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/public/calendars/{enabled.Slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/public/calendars/{regenerated.Slug}")).StatusCode);

        var disabled = await PutAsync(guildId, new PublicCalendarRequest(false));
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.Slug);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/public/calendars/{regenerated.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Regenerating_while_off_is_rejected_and_does_not_turn_sharing_on()
    {
        const long guildId = 12180;
        var response = await Client.PutAsJsonAsync(
            $"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true, Regenerate: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await ReadAsync<PublicCalendarSettingsDto>(await Client.GetAsync($"/guilds/{guildId}/public-calendar"));
        Assert.False(after.Enabled);
        Assert.Null(after.Slug);
    }

    [Fact]
    public async Task Concurrent_first_enables_agree_on_a_single_slug()
    {
        const long guildId = 12170;
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Client.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true))));

        var stored = (await ReadAsync<PublicCalendarSettingsDto>(
            await Client.GetAsync($"/guilds/{guildId}/public-calendar"))).Slug;
        Assert.NotNull(stored);
        foreach (var response in responses)
        {
            // Every caller got the ONE link that actually exists — never a slug a racing
            // request immediately replaced.
            Assert.Equal(stored, (await ReadAsync<PublicCalendarSettingsDto>(response)).Slug);
        }
    }

    [Fact]
    public async Task Only_managers_change_the_setting_while_members_can_read_it()
    {
        const long guildId = 12120;
        // Web membership checks are scoped to guilds the bot is actually in.
        (await Client.PutAsJsonAsync($"/guilds/{guildId}/presence", new GuildPresenceRequest(true, "G"))).EnsureSuccessStatusCode();
        var (member, _) = await fixture.LoginAsync(12191, (guildId, "G", false));
        var denied = await member.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/guilds/{guildId}/public-calendar")).StatusCode);

        var (manager, _) = await fixture.LoginAsync(12192, (guildId, "G", true));
        var allowed = await manager.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True((await allowed.Content.ReadFromJsonAsync<PublicCalendarSettingsDto>())!.Enabled);
    }

    [Fact]
    public async Task Month_view_shows_titles_times_places_and_jump_links_but_nothing_private()
    {
        const long guildId = 12130;
        (await Client.PutAsJsonAsync($"/guilds/{guildId}/presence", new GuildPresenceRequest(true, "Test Guild")))
            .EnsureSuccessStatusCode();
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;

        var ev = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Council of Elrond", "in 6 hours", ChannelId,
            Description: "secret sauce recipe", Location: "The keep"));
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/message", new SetEventMessageRequest(ChannelId, 424242)))
            .EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/12150", new RsvpRequest(ev.Options.OrderBy(o => o.SortOrder).First().Id)))
            .EnsureSuccessStatusCode();

        // A cancelled event never shows.
        var cancelled = await CreateAsync(guildId, new CreateEventRequest(CreatorId, "Called off", "in 7 hours", ChannelId));
        (await Client.PatchAsJsonAsync($"/events/{cancelled.Id}", new UpdateEventRequest(CreatorId, Status: EventStatus.Cancelled)))
            .EnsureSuccessStatusCode();

        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(MonthUrl(slug, ev.StartsAtUtc));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.True(response.Headers.CacheControl!.NoStore); // the slug is revocable — never cache

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret sauce", json); // descriptions stay private
        Assert.DoesNotContain("12150", json);        // so do RSVPs and member ids
        Assert.DoesNotContain("Called off", json);

        var calendar = System.Text.Json.JsonSerializer.Deserialize<PublicCalendarDto>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("Test Guild", calendar.GuildName);
        Assert.Equal("UTC", calendar.TimeZone);
        Assert.True(calendar.EarliestMonth < calendar.LatestMonth);
        var entry = Assert.Single(calendar.Events, e => e.Title == "Council of Elrond");
        Assert.Equal("The keep", entry.Location);
        Assert.Equal($"https://discord.com/channels/{guildId}/{ChannelId}/424242", entry.DiscordUrl);
        Assert.False(entry.Projected);
        Assert.Equal(ev.StartsAtUtc, entry.StartsAtUtc);
        Assert.Equal(ev.StartsAtUtc.UtcDateTime, entry.StartsAtLocal); // server zone is UTC here
    }

    [Fact]
    public async Task Running_series_project_future_occurrences_without_doubling_the_live_one()
    {
        const long guildId = 12140;
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Weekly Standup", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatCount: 3));

        // Sweep the months the three occurrences (and a would-be fourth) can land in.
        using var anonymous = fixture.Factory.CreateClient();
        var seen = new Dictionary<DateTimeOffset, PublicCalendarEventDto>();
        for (var week = 0; week <= 3; week++)
        {
            var month = await ReadAsync<PublicCalendarDto>(
                await anonymous.GetAsync(MonthUrl(slug, live.StartsAtUtc.AddDays(7 * week))));
            foreach (var entry in month.Events.Where(e => e.Title == "Weekly Standup"))
            {
                seen[entry.StartsAtUtc] = entry;
            }
        }

        // Exactly the count-limited three: the concrete live occurrence plus two projections.
        Assert.Equal(
            [live.StartsAtUtc, live.StartsAtUtc.AddDays(7), live.StartsAtUtc.AddDays(14)],
            seen.Keys.Order());
        Assert.False(seen[live.StartsAtUtc].Projected);
        Assert.All(seen.Values.Where(e => e.StartsAtUtc > live.StartsAtUtc), e =>
        {
            Assert.True(e.Projected);
            Assert.Null(e.DiscordUrl);
        });
    }

    [Fact]
    public async Task A_day_set_with_a_count_shows_what_the_feed_lists_and_the_series_goes_on_to_post()
    {
        const long guildId = 12200;
        const string title = "Mon Wed Fri class";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, DaysOfWeek: MonWedFri), RepeatCount: 7));
        Assert.Equal(monday, live.StartsAtUtc);

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, monday);
        var feed = await FeedStartsAsync(anonymous, guildId, title, monday);
        var posted = await PostedBySkippingAsync(live.Id);

        // Seven day-instances: the posted Monday, then Wed, Fri, Mon, Wed, Fri, Mon — and nothing
        // in the weeks after the count runs out.
        DateTimeOffset[] expected = [.. new[] { 0, 2, 4, 7, 9, 11, 14 }.Select(days => monday.AddDays(days))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
        Assert.False(shown[0].Projected);
        Assert.All(shown.Skip(1), e => Assert.True(e.Projected));
    }

    [Fact]
    public async Task A_day_set_with_an_until_date_shows_what_the_feed_lists_and_the_series_goes_on_to_post()
    {
        const long guildId = 12210;
        const string title = "Mon Wed Fri until";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, DaysOfWeek: MonWedFri),
            RepeatUntilText: monday.AddDays(16).ToString("M/d/yyyy", CultureInfo.InvariantCulture)));
        Assert.Equal(monday, live.StartsAtUtc);

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, monday);
        var feed = await FeedStartsAsync(anonymous, guildId, title, monday);
        var posted = await PostedBySkippingAsync(live.Id);

        // Every Monday, Wednesday and Friday up to and including the until date, a Wednesday.
        DateTimeOffset[] expected = [.. new[] { 0, 2, 4, 7, 9, 11, 14, 16 }.Select(days => monday.AddDays(days))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
    }

    // A series posts its next occurrence only once the posted one has ended, and then takes the
    // first of its dates that starts after that moment. The tests below compare what the calendar
    // shows with what the series really posts when each occurrence runs its length, and with what
    // the ICS feed lists. All but the last move the posted occurrence, or change its length, by an
    // edit of that occurrence alone; the last is a series whose own length reaches its next date.

    [Fact]
    public async Task A_posted_occurrence_moved_past_a_date_shows_what_the_feed_lists_and_the_series_goes_on_to_post()
    {
        const long guildId = 12220;
        const string title = "Daily moved past a date";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 4));
        Assert.Equal(monday, live.StartsAtUtc);

        // Monday 18:00 becomes Tuesday 20:00, for this occurrence alone.
        var tuesdayLate = monday.AddDays(1).AddHours(2);
        await EditOccurrenceAsync(live.Id, new UpdateEventRequest(
            CreatorId, WhenText: WhenText(tuesdayLate), Scope: EditScope.Occurrence));

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, monday);
        var feed = await FeedStartsAsync(anonymous, guildId, title, monday);
        var posted = await PostedByRunningAsync(live.SeriesId!.Value);

        // Four in all: the moved occurrence, then Wednesday, Thursday and Friday at 18:00.
        // Tuesday's own 18:00 is never posted, and it uses up none of the four.
        DateTimeOffset[] expected = [tuesdayLate, monday.AddDays(2), monday.AddDays(3), monday.AddDays(4)];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
        Assert.False(shown[0].Projected);
        Assert.All(shown.Skip(1), e => Assert.True(e.Projected));
    }

    [Fact]
    public async Task A_day_set_occurrence_moved_onto_its_next_day_shows_what_the_feed_lists_and_the_series_goes_on_to_post()
    {
        const long guildId = 12230;
        const string title = "Mon Wed Fri moved onto Wednesday";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, DaysOfWeek: MonWedFri), RepeatCount: 6));
        Assert.Equal(monday, live.StartsAtUtc);

        // Monday's occurrence moves onto Wednesday 18:00, the series' next day and time.
        var wednesday = monday.AddDays(2);
        await EditOccurrenceAsync(live.Id, new UpdateEventRequest(
            CreatorId, WhenText: WhenText(wednesday), Scope: EditScope.Occurrence));

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, monday);
        var feed = await FeedStartsAsync(anonymous, guildId, title, monday);
        var posted = await PostedByRunningAsync(live.SeriesId!.Value);

        // Six in all: the moved occurrence on Wednesday — shown once — then Fri, Mon, Wed, Fri
        // and the Monday two weeks on. Wednesday's own occurrence uses up none of the six.
        DateTimeOffset[] expected = [.. new[] { 2, 4, 7, 9, 11, 14 }.Select(days => monday.AddDays(days))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
        Assert.False(shown[0].Projected);
        Assert.All(shown.Skip(1), e => Assert.True(e.Projected));
    }

    [Fact]
    public async Task A_day_set_occurrence_moved_past_several_days_stops_on_its_until_date_as_the_feed_and_the_series_do()
    {
        const long guildId = 12240;
        const string title = "Mon Wed Fri moved a week on";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, DaysOfWeek: MonWedFri),
            RepeatUntilText: monday.AddDays(16).ToString("M/d/yyyy", CultureInfo.InvariantCulture)));
        Assert.Equal(monday, live.StartsAtUtc);

        // Monday's occurrence moves to the next Monday, 20:00 — past Wednesday's, Friday's and
        // that Monday's own 18:00.
        var nextMondayLate = monday.AddDays(7).AddHours(2);
        await EditOccurrenceAsync(live.Id, new UpdateEventRequest(
            CreatorId, WhenText: WhenText(nextMondayLate), Scope: EditScope.Occurrence));

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, monday);
        var feed = await FeedStartsAsync(anonymous, guildId, title, monday);
        var posted = await PostedByRunningAsync(live.SeriesId!.Value);

        // The moved occurrence, then Wednesday, Friday, Monday and the until date, a Wednesday.
        DateTimeOffset[] expected =
            [nextMondayLate, .. new[] { 9, 11, 14, 16 }.Select(days => monday.AddDays(days))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
    }

    [Fact]
    public async Task A_posted_occurrence_that_runs_long_shows_what_the_feed_lists_and_the_series_goes_on_to_post()
    {
        const long guildId = 12250;
        const string title = "Daily with a long first one";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day),
            RepeatUntilText: monday.AddDays(6).ToString("M/d/yyyy", CultureInfo.InvariantCulture)));
        Assert.Equal(monday, live.StartsAtUtc);

        // This occurrence alone lasts three days: Monday 18:00 to Thursday 18:00.
        await EditOccurrenceAsync(live.Id, new UpdateEventRequest(
            CreatorId, DurationMinutes: 3 * 24 * 60, Scope: EditScope.Occurrence));

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, monday);
        var feed = await FeedStartsAsync(anonymous, guildId, title, monday);
        var posted = await PostedByRunningAsync(live.SeriesId!.Value);

        // Monday's, then Friday, Saturday and Sunday, the until date. Tuesday's and Wednesday's
        // 18:00 pass while it runs, and Thursday's starts at the moment it ends.
        DateTimeOffset[] expected = [.. new[] { 0, 4, 5, 6 }.Select(days => monday.AddDays(days))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
    }

    [Fact]
    public async Task A_posted_occurrence_moved_into_the_next_month_leaves_no_upcoming_date_behind_it()
    {
        const long guildId = 12260;
        const string title = "Daily moved over the month's end";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var lastDay = LastEveningOfNextMonth();
        var first = lastDay.AddDays(-1);
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(first), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 5));
        Assert.Equal(first, live.StartsAtUtc);

        // The occurrence on the month's last day but one moves to the 2nd of the month after, 20:00.
        var moved = lastDay.AddDays(2).AddHours(2);
        await EditOccurrenceAsync(live.Id, new UpdateEventRequest(
            CreatorId, WhenText: WhenText(moved), Scope: EditScope.Occurrence));

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, first);
        var feed = await FeedStartsAsync(anonymous, guildId, title, first);
        var posted = await PostedByRunningAsync(live.SeriesId!.Value);

        // Five in all: the moved occurrence on the 2nd, then the 3rd to the 6th. The month it
        // left, the first of the views read, shows nothing of the series: the last day's own
        // 18:00 will not be posted.
        DateTimeOffset[] expected = [moved, .. Enumerable.Range(3, 4).Select(day => lastDay.AddDays(day))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
    }

    [Fact]
    public async Task A_count_seen_from_the_month_after_the_posted_occurrence_allows_for_the_date_it_passed()
    {
        const long guildId = 12270;
        const string title = "Daily counted over the month's end";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var lastDay = LastEveningOfNextMonth();
        var first = lastDay.AddDays(-3);
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(first), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 6));
        Assert.Equal(first, live.StartsAtUtc);

        // The occurrence moves a day on, to 20:00 — still inside the month.
        var moved = first.AddDays(1).AddHours(2);
        await EditOccurrenceAsync(live.Id, new UpdateEventRequest(
            CreatorId, WhenText: WhenText(moved), Scope: EditScope.Occurrence));

        using var anonymous = fixture.Factory.CreateClient();
        var shown = await ShownAsync(anonymous, slug, title, first);
        var feed = await FeedStartsAsync(anonymous, guildId, title, first);
        var posted = await PostedByRunningAsync(live.SeriesId!.Value);

        // Six in all: the moved occurrence, the month's last two days, then the 1st, 2nd and 3rd.
        // The month after is a view of its own, without the posted occurrence in it, and the
        // count still reaches the 3rd there.
        DateTimeOffset[] expected = [moved, .. Enumerable.Range(-1, 5).Select(day => lastDay.AddDays(day))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
    }

    [Fact]
    public async Task A_series_whose_own_length_reaches_its_next_date_shows_what_the_series_goes_on_to_post()
    {
        const long guildId = 12280;
        const string title = "Daily, a day and a half long";
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var monday = AMondayEvening();
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, title, WhenText(monday), ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day), RepeatCount: 4));
        Assert.Equal(monday, live.StartsAtUtc);

        // A day and a half long, set in the database: such a length is refused at creation now,
        // and this is a series made before that.
        await MakeLongAsync(live, 36 * 60);

        using var anonymous = fixture.Factory.CreateClient();
        var feed = await FeedStartsAsync(anonymous, guildId, title, monday);
        var shown = await ShownAsync(anonymous, slug, title, monday);
        var posted = await PostedByRunningAsync(live.SeriesId!.Value);

        // Every occurrence ends at 06:00 two days after it starts, so the series posts every
        // second date: Monday, Wednesday, Friday, Sunday.
        DateTimeOffset[] expected = [.. new[] { 0, 2, 4, 6 }.Select(days => monday.AddDays(days))];
        Assert.Equal(expected, feed);
        Assert.Equal(expected[1..], posted);
        Assert.Equal(expected, shown.Select(e => e.StartsAtUtc));
    }

    [Fact]
    public async Task Months_far_from_today_are_rejected()
    {
        const long guildId = 12160;
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;

        using var anonymous = fixture.Factory.CreateClient();
        var farFuture = DateTimeOffset.UtcNow.AddYears(3);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync(MonthUrl(slug, farFuture))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/public/calendars/{slug}?year=2026&month=13")).StatusCode);
        // Crafted years must be rejected outright, never wrap into range and 500 in the window math.
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/public/calendars/{slug}?year={int.MaxValue}&month=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/public/calendars/{slug}?year={int.MinValue}&month=12")).StatusCode);
    }

    // ---------- helpers ----------

    private static string MonthUrl(string slug, DateTimeOffset at) =>
        $"/public/calendars/{slug}?year={at.Year}&month={at.Month}";

    /// <summary>18:00 UTC on a Monday one to two weeks ahead, so a test's dates fall on the same
    /// weekdays whatever day it runs on.</summary>
    private static DateTimeOffset AMondayEvening()
    {
        var today = DateTimeOffset.UtcNow.Date;
        var monday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7 + 7);
        return new DateTimeOffset(monday.AddHours(18), TimeSpan.Zero);
    }

    /// <summary>18:00 UTC on the last day of next month, so that the days on either side of a
    /// month's end are all still to come whatever day a test runs on.</summary>
    private static DateTimeOffset LastEveningOfNextMonth()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, 1, 18, 0, 0, TimeSpan.Zero).AddMonths(2).AddDays(-1);
    }

    private static string WhenText(DateTimeOffset at) =>
        at.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>Every entry with the title in the month views from <paramref name="from"/> to
    /// <see cref="SweepWeeks"/> weeks on, in start order — a repeated entry stays repeated.</summary>
    private static async Task<List<PublicCalendarEventDto>> ShownAsync(
        HttpClient anonymous, string slug, string title, DateTimeOffset from)
    {
        var shown = new List<PublicCalendarEventDto>();
        var months = Enumerable.Range(0, SweepWeeks + 1)
            .Select(week => from.AddDays(7 * week))
            .DistinctBy(day => (day.Year, day.Month));
        foreach (var month in months)
        {
            var view = await ReadAsync<PublicCalendarDto>(await anonymous.GetAsync(MonthUrl(slug, month)));
            shown.AddRange(view.Events.Where(e => e.Title == title));
        }

        return [.. shown.OrderBy(e => e.StartsAtUtc)];
    }

    /// <summary>The starts a calendar client reads from the server's ICS feed for the title, from
    /// <paramref name="from"/> to <see cref="SweepWeeks"/> weeks on: the posted occurrence plus
    /// the repeat rule, expanded by Ical.Net.</summary>
    private async Task<List<DateTimeOffset>> FeedStartsAsync(
        HttpClient anonymous, long guildId, string title, DateTimeOffset from)
    {
        var token = await ReadAsync<FeedTokenDto>(await Client.PostAsync($"/guilds/{guildId}/feed-token", null));
        var feed = Ical.Net.Calendar.Load(await anonymous.GetStringAsync(token.Path))!;
        var end = from.AddDays(7 * SweepWeeks).UtcDateTime;
        return [.. feed.Events
            .Where(e => e.Summary == title)
            .SelectMany(e => e.GetOccurrences(new Ical.Net.DataTypes.CalDateTime(from.UtcDateTime))
                .TakeWhile(o => o.Period.StartTime.AsUtc < end))
            .Select(o => new DateTimeOffset(o.Period.StartTime.AsUtc, TimeSpan.Zero))
            .Order()];
    }

    /// <summary>What the series really posts after its live occurrence: skips the live occurrence
    /// and collects the replacement's start, until the series ends.</summary>
    private async Task<List<DateTimeOffset>> PostedBySkippingAsync(Guid liveId)
    {
        var posted = new List<DateTimeOffset>();
        Guid? id = liveId;
        for (var guard = 0; id is not null && guard < 30; guard++)
        {
            var skip = await ReadAsync<SkipOccurrenceResponse>(await Client.PostAsync($"/events/{id}/skip", null));
            id = skip.NextEvent?.Id;
            if (skip.NextEvent is { } next)
            {
                posted.Add(next.StartsAtUtc);
            }
        }

        Assert.Null(id); // the series ended inside the guard
        return posted;
    }

    /// <summary>What the series really posts after its posted occurrence when each occurrence
    /// runs its length: runs the scheduler at the moment the posted occurrence ends and collects
    /// the start of the occurrence it posts next, until the series ends.</summary>
    private async Task<List<DateTimeOffset>> PostedByRunningAsync(Guid seriesId)
    {
        var posted = new List<DateTimeOffset>();
        var series = await ReadAsync<SeriesDto>(await Client.GetAsync($"/series/{seriesId}"));
        for (var guard = 0; series.LiveEventId is { } liveId && guard < 30; guard++)
        {
            var live = await ReadAsync<EventDto>(await Client.GetAsync($"/events/{liveId}"));
            var end = Instant.FromDateTimeOffset(live.StartsAtUtc) + Duration.FromMinutes(live.DurationMinutes ?? 60);

            // One pass starts the occurrence; the next ends it and posts the one that follows.
            await SweepAsync(end);
            await SweepAsync(end);

            series = await ReadAsync<SeriesDto>(await Client.GetAsync($"/series/{seriesId}"));
            if (series.LiveEventId is { } nextId)
            {
                Assert.NotEqual(liveId, nextId);
                posted.Add((await ReadAsync<EventDto>(await Client.GetAsync($"/events/{nextId}"))).StartsAtUtc);
            }
        }

        Assert.True(series.Ended); // the series ended inside the guard
        return posted;
    }

    /// <summary>One pass of the scheduler, as of <paramref name="now"/>.</summary>
    private async Task SweepAsync(Instant now)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(now, CancellationToken.None);
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

    /// <summary>Changes one occurrence through the edit route.</summary>
    private async Task EditOccurrenceAsync(Guid eventId, UpdateEventRequest request) =>
        (await Client.PatchAsJsonAsync($"/events/{eventId}", request)).EnsureSuccessStatusCode();

    private async Task<PublicCalendarSettingsDto> PutAsync(long guildId, PublicCalendarRequest request) =>
        await ReadAsync<PublicCalendarSettingsDto>(
            await Client.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", request));

    private async Task<EventDto> CreateAsync(long guildId, CreateEventRequest request)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
