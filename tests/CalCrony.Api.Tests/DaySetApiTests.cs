using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;
using NodaTime.Extensions;

namespace CalCrony.Api.Tests;

/// <summary>End-to-end coverage of weekly day sets: create/edit/skip round trips, count
/// semantics (every day-instance counts), template capture, and the ICS feed's BYDAY.</summary>
public class DaySetApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9990;
    private const long ChannelId = 9991;
    private const long CreatorId = 9992;

    private const RecurrenceDays TueThu = RecurrenceDays.Tuesday | RecurrenceDays.Thursday;

    private static readonly RecurrenceRuleDto WeeklyTueThu = new(RecurrenceUnit.Week, DaysOfWeek: TueThu);

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Create_stores_the_day_set_and_summarizes_it()
    {
        var ev = await CreateSeriesEventAsync("Raid nights", WeeklyTueThu);
        Assert.Equal("Repeats weekly on Tue, Thu", ev.RecurrenceSummary);

        var series = await GetSeriesAsync(ev.SeriesId!.Value);
        Assert.Equal(TueThu, series.DaysOfWeek);
        Assert.Equal(RecurrenceUnit.Week, series.Unit);

        var weekdays = await CreateSeriesEventAsync("Standup", new RecurrenceRuleDto(RecurrenceUnit.Week, DaysOfWeek: RecurrenceDays.Weekdays));
        Assert.Equal("Repeats every weekday", weekdays.RecurrenceSummary);

        var biweekly = await CreateSeriesEventAsync("Class", new RecurrenceRuleDto(
            RecurrenceUnit.Week, 2, DaysOfWeek: RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday));
        Assert.Equal("Repeats every 2 weeks on Mon, Wed, Fri", biweekly.RecurrenceSummary);
    }

    [Fact]
    public async Task Create_rejects_day_sets_on_non_weekly_rules_and_unknown_flags()
    {
        var daily = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Bad", "in 2 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day, DaysOfWeek: TueThu)));
        Assert.Equal(HttpStatusCode.BadRequest, daily.StatusCode);
        Assert.Contains("only apply to weekly", await ErrorAsync(daily));

        var bogus = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Bad", "in 2 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, DaysOfWeek: (RecurrenceDays)256)));
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        Assert.Contains("set of weekdays", await ErrorAsync(bogus));
    }

    [Fact]
    public async Task Skip_rolls_one_day_instance_at_a_time_keeping_the_wall_clock_time()
    {
        var ev = await CreateSeriesEventAsync("Skip days", WeeklyTueThu);
        var zone = DateTimeZoneProviders.Tzdb[ev.TimeZone];
        var first = ev.StartsAtUtc.ToInstant().InZone(zone);

        var skip = await SkipAsync(ev.Id);
        var next = skip.NextEvent!.StartsAtUtc.ToInstant().InZone(zone);

        // The replacement is the next selected weekday after the first date — the same slot the
        // pure calculator predicts — at the same local time.
        var expected = RecurrenceCalculator.NextDate(
            RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, first.Date, first.Date, TueThu);
        Assert.Equal(expected, next.Date);
        Assert.Contains(next.DayOfWeek, new[] { IsoDayOfWeek.Tuesday, IsoDayOfWeek.Thursday });
        Assert.Equal(first.TimeOfDay, next.TimeOfDay);
        Assert.Equal(2, skip.Series.OccurrenceCount);

        // And again: one more day-instance, never a whole week.
        var again = await SkipAsync(skip.NextEvent.Id);
        var third = again.NextEvent!.StartsAtUtc.ToInstant().InZone(zone);
        Assert.Equal(
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, first.Date, next.Date, TueThu),
            third.Date);
        Assert.True(Period.Between(next.Date, third.Date, PeriodUnits.Days).Days < 7);
    }

    [Fact]
    public async Task Count_limit_counts_day_instances()
    {
        var ev = await CreateSeriesEventAsync("Three times", WeeklyTueThu, repeatCount: 3);
        Assert.Contains("1 of 3", ev.RecurrenceSummary);

        var second = await SkipAsync(ev.Id);
        Assert.NotNull(second.NextEvent);
        Assert.Equal(2, second.Series.OccurrenceCount);

        var third = await SkipAsync(second.NextEvent!.Id);
        Assert.NotNull(third.NextEvent);
        Assert.Equal(3, third.Series.OccurrenceCount);

        // The third day-instance was the last one: skipping it ends the series.
        var done = await SkipAsync(third.NextEvent!.Id);
        Assert.Null(done.NextEvent);
        Assert.True(done.Series.Ended);
    }

    [Fact]
    public async Task Patch_changes_the_day_set_and_dropping_weekly_drops_the_set()
    {
        var ev = await CreateSeriesEventAsync("Edit days", WeeklyTueThu);
        var id = ev.SeriesId!.Value;

        var monWedFri = RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday;
        var edited = await ReadSeriesAsync(await PatchAsync(id, new UpdateSeriesRequest(DaysOfWeek: monWedFri)));
        Assert.Equal(monWedFri, edited.DaysOfWeek);
        Assert.Equal("Repeats weekly on Mon, Wed, Fri", edited.Summary);

        // The next spawn follows the new set.
        var skip = await SkipAsync(ev.Id);
        var zone = DateTimeZoneProviders.Tzdb[ev.TimeZone];
        var nextDay = skip.NextEvent!.StartsAtUtc.ToInstant().InZone(zone).DayOfWeek;
        Assert.Contains(nextDay, new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Friday });

        // Interval edits keep the set; clearing it explicitly goes back to the anchor weekday.
        var every2 = await ReadSeriesAsync(await PatchAsync(id, new UpdateSeriesRequest(Interval: 2)));
        Assert.Equal(monWedFri, every2.DaysOfWeek);
        Assert.Equal("Repeats every 2 weeks on Mon, Wed, Fri", every2.Summary);

        var cleared = await ReadSeriesAsync(await PatchAsync(id, new UpdateSeriesRequest(DaysOfWeek: RecurrenceDays.None)));
        Assert.Equal(RecurrenceDays.None, cleared.DaysOfWeek);
        Assert.Matches("^Repeats every 2 weeks on (Mon|Tues|Wednes|Thurs|Fri|Satur|Sun)day$", cleared.Summary);

        // Moving to a non-weekly unit drops a stored set without being told to…
        await PatchAsync(id, new UpdateSeriesRequest(DaysOfWeek: monWedFri));
        var daily = await ReadSeriesAsync(await PatchAsync(id, new UpdateSeriesRequest(Unit: RecurrenceUnit.Day, Interval: 1)));
        Assert.Equal(RecurrenceDays.None, daily.DaysOfWeek);
        Assert.Equal("Repeats daily", daily.Summary);

        // …while asking for days on a non-weekly unit is a friendly 400.
        var bad = await PatchAsync(id, new UpdateSeriesRequest(DaysOfWeek: RecurrenceDays.Tuesday));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("only apply to weekly", await ErrorAsync(bad));
    }

    [Fact]
    public async Task Template_captures_and_reapplies_the_day_set()
    {
        var ev = await CreateSeriesEventAsync("Template source", WeeklyTueThu);

        var save = await Client.PostAsJsonAsync($"/guilds/{GuildId}/templates",
            new SaveTemplateRequest(CreatorId, "tue-thu", ev.Id));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var template = (await save.Content.ReadFromJsonAsync<EventTemplateDto>())!;
        Assert.Equal(TueThu, template.Recurrence!.DaysOfWeek);

        var fromTemplate = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "From template", "in 3 hours", ChannelId, TemplateId: template.Id));
        Assert.Equal(HttpStatusCode.Created, fromTemplate.StatusCode);
        var created = (await fromTemplate.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal("Repeats weekly on Tue, Thu", created.RecurrenceSummary);

        // Editing the template's rule with days on a daily unit is rejected like everywhere else.
        var bad = await Client.PatchAsJsonAsync($"/templates/{template.Id}", new UpdateTemplateRequest(
            CreatorId, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day, DaysOfWeek: TueThu)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Feed_exports_the_day_set_as_byday_with_interval_and_follows_rule_edits()
    {
        // Day sets no other test in this guild uses, so the RRULE lines are unambiguous.
        var wedSat = RecurrenceDays.Wednesday | RecurrenceDays.Saturday;
        var ev = await CreateSeriesEventAsync("Feed days", new RecurrenceRuleDto(RecurrenceUnit.Week, 3, DaysOfWeek: wedSat));
        var zone = DateTimeZoneProviders.Tzdb[ev.TimeZone];
        var anchor = ev.StartsAtUtc.ToInstant().InZone(zone).Date;
        var uid = $"{ev.SeriesId}@calcrony";

        var ics = await FetchFeedAsync();
        var rrule = RruleFor(ics, "BYDAY=WE,SA");
        Assert.Contains("FREQ=WEEKLY", rrule);
        Assert.Contains("INTERVAL=3", rrule);
        Assert.Equal(
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 3, MonthlyMode.DayOfMonth, anchor, anchor, wedSat),
            IcsText.DtStartDate(ics, uid, zone));

        // A day-set edit re-projects from the engine's next slot under the NEW set.
        var monFri = RecurrenceDays.Monday | RecurrenceDays.Friday;
        (await PatchAsync(ev.SeriesId!.Value, new UpdateSeriesRequest(DaysOfWeek: monFri))).EnsureSuccessStatusCode();
        ics = await FetchFeedAsync();
        Assert.Contains("INTERVAL=3", RruleFor(ics, "BYDAY=MO,FR"));
        Assert.Equal(
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 3, MonthlyMode.DayOfMonth, anchor, anchor, monFri),
            IcsText.DtStartDate(ics, uid, zone));

        // A whole-series time edit re-anchors the grid; the feed follows the new anchor's week.
        var move = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, WhenText: "in 10 days", Scope: EditScope.Series));
        move.EnsureSuccessStatusCode();
        var moved = (await move.Content.ReadFromJsonAsync<EventDto>())!;
        var newAnchor = moved.StartsAtUtc.ToInstant().InZone(zone).Date;
        Assert.NotEqual(anchor, newAnchor);
        ics = await FetchFeedAsync();
        Assert.Equal(newAnchor, IcsText.DtStartDate(ics, $"{ev.Id}@calcrony", zone));
        Assert.Equal(
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 3, MonthlyMode.DayOfMonth, newAnchor, newAnchor, monFri),
            IcsText.DtStartDate(ics, uid, zone));
    }

    private static string RruleFor(string ics, string bydayFragment) =>
        ics.Split('\n').Select(l => l.TrimEnd('\r'))
            .First(l => l.StartsWith("RRULE:", StringComparison.Ordinal) && l.Contains(bydayFragment));

    private async Task<string> FetchFeedAsync()
    {
        var token = await Client.PostAsync($"/guilds/{GuildId}/feed-token", null);
        token.EnsureSuccessStatusCode();
        var feed = (await token.Content.ReadFromJsonAsync<FeedTokenDto>())!;
        using var anonymous = fixture.Factory.CreateClient();
        return await anonymous.GetStringAsync(feed.Path);
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

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
}
