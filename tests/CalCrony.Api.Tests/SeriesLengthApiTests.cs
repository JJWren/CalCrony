using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>A repeating event whose occurrences would last as long as the gap between two of its
/// dates, or longer. The scheduler posts a series' next occurrence only once the one before has
/// ended, so such a length would hold back dates its schedule names. It is refused at creation, on
/// a change of the whole series' length and on a change of its schedule, with a sentence that
/// names the longest length the schedule allows. A series made before the refusal is left as it
/// is, and a change that touches neither its length nor its schedule is not refused.</summary>
public class SeriesLengthApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 14100;
    private const long ChannelId = 14101;
    private const long CreatorId = 14102;

    private HttpClient Client => fixture.Client;

    // Each schedule with the fewest minutes between two of its dates, as the calendar shows them,
    // and the longest length it allows, as the refusal writes it. Mon + Fri: Friday to Monday,
    // across the end of the week, is nearer than Monday to Friday; every second week, Monday to
    // Friday is nearer than the ten days from Friday to the Monday after next.
    [Theory]
    [InlineData(RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth, RecurrenceDays.None, 24 * 60, "23 h 59 min")]
    [InlineData(RecurrenceUnit.Day, 2, MonthlyMode.DayOfMonth, RecurrenceDays.None, 2 * 24 * 60, "1 day 23 h 59 min")]
    [InlineData(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, RecurrenceDays.None, 7 * 24 * 60, "6 days 23 h 59 min")]
    [InlineData(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, RecurrenceDays.None, 14 * 24 * 60, "13 days 23 h 59 min")]
    [InlineData(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, RecurrenceDays.Monday | RecurrenceDays.Tuesday, 24 * 60, "23 h 59 min")]
    [InlineData(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, RecurrenceDays.Monday | RecurrenceDays.Thursday, 3 * 24 * 60, "2 days 23 h 59 min")]
    [InlineData(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, RecurrenceDays.Monday | RecurrenceDays.Friday, 3 * 24 * 60, "2 days 23 h 59 min")]
    [InlineData(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, RecurrenceDays.Monday | RecurrenceDays.Sunday, 24 * 60, "23 h 59 min")]
    [InlineData(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, RecurrenceDays.Monday | RecurrenceDays.Friday, 4 * 24 * 60, "3 days 23 h 59 min")]
    [InlineData(RecurrenceUnit.Month, 1, MonthlyMode.DayOfMonth, RecurrenceDays.None, 28 * 24 * 60, "27 days 23 h 59 min")]
    [InlineData(RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday, RecurrenceDays.None, 28 * 24 * 60, "27 days 23 h 59 min")]
    public async Task A_length_that_reaches_the_gap_between_two_dates_is_refused_at_creation_and_a_minute_less_is_not(
        RecurrenceUnit unit, int interval, MonthlyMode mode, RecurrenceDays days, int gapMinutes, string longest)
    {
        var rule = new RecurrenceRuleDto(unit, interval, mode, days);
        var refused = await CreateAsync("Too long", gapMinutes, rule);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = await ErrorAsync(refused);
        Assert.Contains("would never be posted", error);
        Assert.Contains($"at most {longest}.", error);

        // Longer still: refused the same way, while it stays within the four weeks allowed.
        if (gapMinutes + 12 * 60 <= FieldLimits.MaxMinutes)
        {
            var longer = await CreateAsync("Longer still", gapMinutes + 12 * 60, rule);
            Assert.Equal(HttpStatusCode.BadRequest, longer.StatusCode);
            Assert.Contains($"at most {longest}.", await ErrorAsync(longer));
        }

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync("Just short", gapMinutes - 1, rule)).StatusCode);
    }

    [Fact]
    public async Task A_monthly_series_on_the_30th_may_last_four_weeks()
    {
        // No February has a 30th every year, so a monthly date on the 30th is never nearer than 29
        // days to the next: four weeks, the longest length there is, holds nothing back.
        var now = DateTimeOffset.UtcNow;
        var thirtieth = Enumerable.Range(1, 3)
            .Select(months => new DateTimeOffset(now.Year, now.Month, 1, 18, 0, 0, TimeSpan.Zero).AddMonths(months))
            .First(month => DateTime.DaysInMonth(month.Year, month.Month) >= 30)
            .AddDays(29);
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Monthly on the 30th", thirtieth.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
            ChannelId, DurationMinutes: FieldLimits.MaxMinutes, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Month)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_new_start_for_the_whole_series_is_checked_from_the_date_it_moves_to()
    {
        // Monthly on the 30th, four weeks long: allowed there (29 days to the next date at the
        // least), but not from the 15th (28 days). Moved there for the whole series, it is
        // refused, and so is the same move made together with that length.
        var (thirtieth, fifteenthAfter) = (MonthDay(0, 30), MonthDay(1, 15));
        var long30 = await CreatedAtAsync("Four weeks from the 30th", thirtieth, FieldLimits.MaxMinutes);
        var moved = await Client.PatchAsJsonAsync($"/events/{long30.Id}", new UpdateEventRequest(
            CreatorId, WhenText: When(fifteenthAfter), Scope: EditScope.Series));
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Contains("at most 27 days 23 h 59 min.", await ErrorAsync(moved));
        var unchanged = await GetSeriesAsync(long30.SeriesId!.Value);
        Assert.Equal(thirtieth.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), unchanged.AnchorDate);

        var short30 = await CreatedAtAsync("An hour on the 30th", thirtieth, 60);
        var movedAndLong = await Client.PatchAsJsonAsync($"/events/{short30.Id}", new UpdateEventRequest(
            CreatorId, WhenText: When(fifteenthAfter), DurationMinutes: FieldLimits.MaxMinutes, Scope: EditScope.Series));
        Assert.Equal(HttpStatusCode.BadRequest, movedAndLong.StatusCode);
        Assert.Equal(60, (await GetSeriesAsync(short30.SeriesId!.Value)).DurationMinutes);

        // The other way round is allowed: from the 15th to a 30th, with that length.
        var fifteenth = MonthDay(0, 15);
        var short15 = await CreatedAtAsync("An hour on the 15th", fifteenth, 60);
        var toThirtieth = MonthDay(1, 30);
        (await Client.PatchAsJsonAsync($"/events/{short15.Id}", new UpdateEventRequest(
            CreatorId, WhenText: When(toThirtieth), DurationMinutes: FieldLimits.MaxMinutes, Scope: EditScope.Series)))
            .EnsureSuccessStatusCode();
        var movedSeries = await GetSeriesAsync(short15.SeriesId!.Value);
        Assert.Equal(FieldLimits.MaxMinutes, movedSeries.DurationMinutes);
        Assert.Equal(toThirtieth.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), movedSeries.AnchorDate);
    }

    [Fact]
    public async Task No_length_reaches_the_gap_of_a_schedule_whose_dates_are_further_apart_than_four_weeks()
    {
        Assert.Equal(HttpStatusCode.Created,
            (await CreateAsync("Every other month", FieldLimits.MaxMinutes, new RecurrenceRuleDto(RecurrenceUnit.Month, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await CreateAsync("Yearly", FieldLimits.MaxMinutes, new RecurrenceRuleDto(RecurrenceUnit.Year))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await CreateAsync("Once, two days", 2 * 24 * 60, rule: null)).StatusCode);
    }

    [Fact]
    public async Task A_change_of_the_whole_series_length_that_reaches_the_gap_is_refused_and_one_occurrence_may_still_run_long()
    {
        var live = await CreatedAsync("An hour a day", 60, new RecurrenceRuleDto(RecurrenceUnit.Day));

        var refused = await Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
            CreatorId, DurationMinutes: 24 * 60, Scope: EditScope.Series));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("at most 23 h 59 min.", await ErrorAsync(refused));
        Assert.Equal(60, (await GetEventAsync(live.Id)).DurationMinutes);
        Assert.Equal(60, (await GetSeriesAsync(live.SeriesId!.Value)).DurationMinutes);

        // A minute less is accepted for the whole series.
        (await Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
            CreatorId, DurationMinutes: 23 * 60 + 59, Scope: EditScope.Series))).EnsureSuccessStatusCode();
        Assert.Equal(23 * 60 + 59, (await GetSeriesAsync(live.SeriesId!.Value)).DurationMinutes);

        // One occurrence alone may run past the next date: the calendar and the feed show the
        // dates it holds back.
        (await Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
            CreatorId, DurationMinutes: 3 * 24 * 60, Scope: EditScope.Occurrence))).EnsureSuccessStatusCode();
        Assert.Equal(3 * 24 * 60, (await GetEventAsync(live.Id)).DurationMinutes);
        Assert.Equal(23 * 60 + 59, (await GetSeriesAsync(live.SeriesId!.Value)).DurationMinutes);
    }

    [Fact]
    public async Task A_change_of_schedule_that_brings_two_dates_within_the_series_length_is_refused()
    {
        var live = await CreatedAsync("A day and a half, weekly", 36 * 60, new RecurrenceRuleDto(RecurrenceUnit.Week));
        var seriesId = live.SeriesId!.Value;

        var daily = await Client.PatchAsJsonAsync($"/series/{seriesId}", new UpdateSeriesRequest(Unit: RecurrenceUnit.Day));
        Assert.Equal(HttpStatusCode.BadRequest, daily.StatusCode);
        var error = await ErrorAsync(daily);
        Assert.Contains("would never be posted", error);
        Assert.Contains("lasts 1 day 12 h", error);
        Assert.Contains("at most 23 h 59 min.", error);
        Assert.EndsWith("Make the occurrences shorter first, or choose dates further apart.", error);

        var nextDay = await Client.PatchAsJsonAsync($"/series/{seriesId}", new UpdateSeriesRequest(
            DaysOfWeek: RecurrenceDays.Monday | RecurrenceDays.Tuesday));
        Assert.Equal(HttpStatusCode.BadRequest, nextDay.StatusCode);

        var unchanged = await GetSeriesAsync(seriesId);
        Assert.Equal(RecurrenceUnit.Week, unchanged.Unit);
        Assert.Equal(RecurrenceDays.None, unchanged.DaysOfWeek);

        // Every second day leaves room for a day and a half.
        (await Client.PatchAsJsonAsync($"/series/{seriesId}", new UpdateSeriesRequest(
            Unit: RecurrenceUnit.Day, Interval: 2))).EnsureSuccessStatusCode();
        var changed = await GetSeriesAsync(seriesId);
        Assert.Equal(RecurrenceUnit.Day, changed.Unit);
        Assert.Equal(2, changed.Interval);
    }

    [Fact]
    public async Task A_series_made_before_the_refusal_can_still_be_changed_in_all_but_its_length_and_schedule()
    {
        var live = await CreatedAsync("Made long before", 60, new RecurrenceRuleDto(RecurrenceUnit.Day));
        var seriesId = live.SeriesId!.Value;
        await MakeLongAsync(live, 24 * 60);

        // Its title, for the whole series, with the length it already has sent along, as the web's
        // form sends every field.
        (await Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
            CreatorId, Title: "Renamed", DurationMinutes: 24 * 60, Scope: EditScope.Series))).EnsureSuccessStatusCode();
        Assert.Equal("Renamed", (await GetSeriesAsync(seriesId)).Title);

        // Its end, with its schedule sent along unchanged.
        (await Client.PatchAsJsonAsync($"/series/{seriesId}", new UpdateSeriesRequest(
            Unit: RecurrenceUnit.Day, Interval: 1, End: SeriesEndChoice.Count, RepeatCount: 10))).EnsureSuccessStatusCode();
        Assert.Equal(10, (await GetSeriesAsync(seriesId)).MaxOccurrences);

        // Its start, for the whole series, an hour later with the length it has.
        var later = live.StartsAtUtc.AddHours(1);
        (await Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
            CreatorId, WhenText: When(later), Scope: EditScope.Series))).EnsureSuccessStatusCode();
        Assert.Equal(When(later), When((await GetEventAsync(live.Id)).StartsAtUtc));

        // A new schedule it still reaches, a new length that still reaches its own, and a new start
        // with such a length: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync($"/series/{seriesId}", new UpdateSeriesRequest(
            Unit: RecurrenceUnit.Week, DaysOfWeek: RecurrenceDays.Monday | RecurrenceDays.Tuesday))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
            CreatorId, DurationMinutes: 30 * 60, Scope: EditScope.Series))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
            CreatorId, WhenText: When(later.AddHours(1)), DurationMinutes: 30 * 60, Scope: EditScope.Series))).StatusCode);
        Assert.Equal(24 * 60, (await GetSeriesAsync(seriesId)).DurationMinutes);
        Assert.Equal(When(later), When((await GetEventAsync(live.Id)).StartsAtUtc));
    }

    [Fact]
    public async Task A_caller_who_may_not_change_the_series_is_answered_without_waiting_for_its_lock()
    {
        // The right to change the event is checked before any lock is taken: while another
        // request holds the series row, a member who did not make the event and a user of another
        // server are answered at once.
        var live = await CreatedAsync("Held by another change", 60, new RecurrenceRuleDto(RecurrenceUnit.Day));
        var (member, _) = await fixture.LoginAsync(14103, (GuildId, "G", false));
        var (outsider, _) = await fixture.LoginAsync(14104, (14199, "Elsewhere", true));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await using var holding = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync(
                $"""SELECT "Id" FROM "EventSeries" WHERE "Id" = {live.SeriesId!.Value} FOR UPDATE""");

            using var soon = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var byMember = await member.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
                0, Title: "Not theirs", Scope: EditScope.Series), soon.Token);
            Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
            var byOutsider = await outsider.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
                0, Title: "Not theirs", Scope: EditScope.Series), soon.Token);
            Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
        }

        Assert.Equal("Held by another change", (await GetSeriesAsync(live.SeriesId!.Value)).Title);
    }

    [Fact]
    public async Task Two_changes_sent_together_cannot_make_a_series_as_long_as_its_gap()
    {
        // A weekly series two hours long: a daily schedule is fine for it, and so is a length of
        // three days, but not both. Sent at the same moment, again and again, one of the two is
        // refused every time — the second to reach the series sees what the first has done.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var live = await CreatedAsync($"Both at once {attempt}", 120, new RecurrenceRuleDto(RecurrenceUnit.Week));
            var answers = await Task.WhenAll(
                Client.PatchAsJsonAsync($"/series/{live.SeriesId}", new UpdateSeriesRequest(Unit: RecurrenceUnit.Day)),
                Client.PatchAsJsonAsync($"/events/{live.Id}", new UpdateEventRequest(
                    CreatorId, DurationMinutes: 3 * 24 * 60, Scope: EditScope.Series)));

            Assert.Single(answers, answer => answer.IsSuccessStatusCode);
            var series = await GetSeriesAsync(live.SeriesId!.Value);
            Assert.False(
                series.Unit == RecurrenceUnit.Day && series.DurationMinutes >= 24 * 60,
                $"Attempt {attempt}: the series became daily and {series.DurationMinutes} minutes long.");
        }
    }

    [Fact]
    public async Task An_ended_series_as_long_as_its_gap_is_revived_only_with_dates_further_apart()
    {
        // A series made before the refusal, a day long and daily, then stopped. Reviving it as it
        // is would make it run again; that is checked like a new series.
        var live = await CreatedAsync("Made long, then stopped", 60, new RecurrenceRuleDto(RecurrenceUnit.Day));
        var seriesId = live.SeriesId!.Value;
        await MakeLongAsync(live, 24 * 60);
        (await Client.PostAsync($"/series/{seriesId}/stop", null)).EnsureSuccessStatusCode();

        var revived = await Client.PatchAsJsonAsync($"/series/{seriesId}", new UpdateSeriesRequest(
            End: SeriesEndChoice.Count, RepeatCount: 10));
        Assert.Equal(HttpStatusCode.BadRequest, revived.StatusCode);
        var error = await ErrorAsync(revived);
        Assert.Contains("lasts 1 day", error);
        Assert.EndsWith("Choose dates further apart to start it again.", error);
        Assert.True((await GetSeriesAsync(seriesId)).Ended);

        // Every second day leaves room for it.
        (await Client.PatchAsJsonAsync($"/series/{seriesId}", new UpdateSeriesRequest(Interval: 2))).EnsureSuccessStatusCode();
        Assert.False((await GetSeriesAsync(seriesId)).Ended);
    }

    [Fact]
    public async Task A_template_with_such_a_length_and_schedule_is_refused_when_it_is_used()
    {
        var source = await CreatedAsync("Template source", 60, rule: null);
        var saved = await Client.PostAsJsonAsync($"/guilds/{GuildId}/templates",
            new SaveTemplateRequest(CreatorId, "a day, daily", source.Id));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var template = (await saved.Content.ReadFromJsonAsync<EventTemplateDto>())!;

        // Saving the template is not checked.
        (await Client.PatchAsJsonAsync($"/templates/{template.Id}", new UpdateTemplateRequest(
            CreatorId, DurationMinutes: 24 * 60, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day)))).EnsureSuccessStatusCode();

        var fromTemplate = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 3 days", ChannelId, TemplateId: template.Id));
        Assert.Equal(HttpStatusCode.BadRequest, fromTemplate.StatusCode);
        Assert.Contains("at most 23 h 59 min.", await ErrorAsync(fromTemplate));

        // A length of its own, or no repeat, and it is accepted.
        Assert.Equal(HttpStatusCode.Created, (await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 3 days", ChannelId, DurationMinutes: 120, TemplateId: template.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 3 days", ChannelId, TemplateId: template.Id, NoRecurrence: true))).StatusCode);
    }

    private Task<HttpResponseMessage> CreateAsync(string title, int minutes, RecurrenceRuleDto? rule) =>
        Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 3 days", ChannelId, DurationMinutes: minutes, Recurrence: rule));

    private async Task<EventDto> CreatedAsync(string title, int minutes, RecurrenceRuleDto? rule)
    {
        var response = await CreateAsync(title, minutes, rule);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    /// <summary>18:00 UTC on a day of the month, in the first month from next month on that has
    /// 30 days or more, or <paramref name="monthsAfter"/> such months later.</summary>
    private static DateTimeOffset MonthDay(int monthsAfter, int day)
    {
        var now = DateTimeOffset.UtcNow;
        var month = new DateTimeOffset(now.Year, now.Month, 1, 18, 0, 0, TimeSpan.Zero).AddMonths(1);
        for (var found = -1; ; month = month.AddMonths(1))
        {
            if (DateTime.DaysInMonth(month.Year, month.Month) >= 30 && ++found == monthsAfter)
            {
                return month.AddDays(day - 1);
            }
        }
    }

    private static string When(DateTimeOffset at) =>
        at.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<EventDto> CreatedAtAsync(string title, DateTimeOffset at, int minutes)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, When(at), ChannelId, DurationMinutes: minutes, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Month)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> GetEventAsync(Guid id) =>
        (await Client.GetFromJsonAsync<EventDto>($"/events/{id}"))!;

    private async Task<SeriesDto> GetSeriesAsync(Guid id) =>
        (await Client.GetFromJsonAsync<SeriesDto>($"/series/{id}"))!;

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;

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
}
