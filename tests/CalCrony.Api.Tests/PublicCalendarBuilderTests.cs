using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The month view's projection of a weekly series with a day set, on fixed dates: every
/// selected weekday is shown — the same occurrences the series goes on to post and the ICS feed's
/// BYDAY rule expands to — and the until date and the occurrence count end it where they end the
/// series.</summary>
public class PublicCalendarBuilderTests
{
    private const RecurrenceDays MonWedFri =
        RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday;

    private const RecurrenceDays TueThu = RecurrenceDays.Tuesday | RecurrenceDays.Thursday;

    private static readonly IReadOnlyDictionary<long, string> NoChannels = new Dictionary<long, string>();

    /// <summary>Noon on Saturday 2026-10-31: the whole of November 2026 lies ahead.</summary>
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 31, 12, 0);

    private static readonly LocalDate Monday = new(2026, 11, 2);

    [Fact]
    public void A_day_set_shows_every_selected_weekday_of_the_month()
    {
        var series = Series(MonWedFri, Monday);

        var month = November(series);

        // The first occurrence is the posted row; every later Monday, Wednesday and Friday is
        // projected — not the first occurrence's weekday alone.
        Assert.Equal([2], Days(month, projected: false));
        Assert.Equal([4, 6, 9, 11, 13, 16, 18, 20, 23, 25, 27, 30], Days(month, projected: true));
        Assert.All(month.Events, e => Assert.Equal(new TimeSpan(18, 0, 0), e.StartsAtLocal.TimeOfDay));
    }

    [Fact]
    public void A_day_set_with_an_until_date_stops_on_that_date()
    {
        var series = Series(MonWedFri, Monday);

        series.UntilDate = new LocalDate(2026, 11, 18); // a Wednesday, inclusive
        Assert.Equal([4, 6, 9, 11, 13, 16, 18], Days(November(series), projected: true));

        series.UntilDate = new LocalDate(2026, 11, 17); // a Tuesday: Monday the 16th is the last
        Assert.Equal([4, 6, 9, 11, 13, 16], Days(November(series), projected: true));
    }

    [Fact]
    public void A_day_set_with_a_count_shows_that_many_day_instances_and_no_more()
    {
        var series = Series(MonWedFri, Monday);
        series.MaxOccurrences = 6; // the posted first occurrence is 1 of 6

        var month = November(series);

        // Five more day-instances — the series ends on Friday the 13th, so nothing is shown in
        // the weeks after it.
        Assert.Equal([2], Days(month, projected: false));
        Assert.Equal([4, 6, 9, 11, 13], Days(month, projected: true));
    }

    [Fact]
    public void A_count_is_used_up_by_the_day_instances_before_the_month_shown()
    {
        var series = Series(MonWedFri, Monday);
        series.MaxOccurrences = 20;

        var december = Build(series, 2026, 12);

        // Thirteen of the twenty fall in November (the 2nd to the 30th); December has the last seven.
        Assert.Equal([2, 4, 7, 9, 11, 14, 16], Days(december, projected: true));
    }

    [Fact]
    public void A_first_occurrence_outside_the_day_set_is_not_repeated_on_its_weekday()
    {
        // Created on a Sunday to repeat on Tuesdays and Thursdays: only the first occurrence is a
        // Sunday.
        var series = Series(TueThu, new LocalDate(2026, 11, 1));

        var month = November(series);

        Assert.Equal([1], Days(month, projected: false));
        Assert.Equal([3, 5, 10, 12, 17, 19, 24, 26], Days(month, projected: true));
    }

    [Fact]
    public void A_day_set_every_second_week_shows_the_on_weeks_only()
    {
        var series = Series(TueThu, new LocalDate(2026, 11, 3), interval: 2);

        // On-weeks start on 2, 16 and 30 November and on 14 and 28 December.
        Assert.Equal([5, 17, 19], Days(November(series), projected: true));
        Assert.Equal([1, 3, 15, 17, 29, 31], Days(Build(series, 2026, 12), projected: true));
    }

    [Fact]
    public void On_weeks_run_monday_to_sunday_from_the_week_of_the_first_occurrence()
    {
        // Tue + Sun from Tuesday the 3rd: an on-week's Sunday is the one that ends it — the 8th
        // and the 22nd, not the 15th.
        var tueSun = Series(RecurrenceDays.Tuesday | RecurrenceDays.Sunday, new LocalDate(2026, 11, 3), interval: 2);
        Assert.Equal([8, 17, 22], Days(November(tueSun), projected: true));

        // Tue + Thu from Thursday the 5th: Tuesday the 10th is in an off-week, though it is less
        // than two weeks after the first occurrence.
        var fromThursday = Series(TueThu, new LocalDate(2026, 11, 5), interval: 2);
        Assert.Equal([17, 19], Days(November(fromThursday), projected: true));
    }

    [Fact]
    public void A_series_under_way_goes_on_from_its_posted_occurrence()
    {
        // Monday's occurrence has ended and Wednesday's is posted: the cursor is on Wednesday.
        var wednesday = new LocalDate(2026, 11, 4);
        var series = Series(MonWedFri, Monday);
        series.CurrentOccurrenceDate = wednesday;
        series.OccurrenceCount = 2;
        Event[] posted = [Occurrence(series, Monday, EventStatus.Ended), Occurrence(series, wednesday, EventStatus.Scheduled)];

        var month = PublicCalendarBuilder.Build(
            null, DateTimeZone.Utc, 2026, 11, posted, [series], NoChannels, Instant.FromUtc(2026, 11, 3, 12, 0));

        Assert.Equal([2, 4], Days(month, projected: false));
        Assert.Equal([6, 9, 11, 13, 16, 18, 20, 23, 25, 27, 30], Days(month, projected: true));
    }

    [Fact]
    public void A_posted_occurrence_moved_onto_the_next_date_is_shown_once()
    {
        // Monday's occurrence was moved to Wednesday 18:00 by an edit of that occurrence alone, so
        // the series' cursor is still on Monday and Wednesday is its next date as well.
        var series = Series(MonWedFri, Monday);
        var moved = Occurrence(series, new LocalDate(2026, 11, 4), EventStatus.Scheduled);

        var month = PublicCalendarBuilder.Build(null, DateTimeZone.Utc, 2026, 11, [moved], [series], NoChannels, Now);

        Assert.Equal([4], Days(month, projected: false));
        Assert.Equal([6, 9, 11, 13, 16, 18, 20, 23, 25, 27, 30], Days(month, projected: true));
    }

    [Fact]
    public void A_seven_day_set_reaches_the_furthest_month_the_calendar_serves()
    {
        // The densest day set steps like a daily series, and the step limit must still reach the
        // last day of the furthest month. A demanding case: seen from the first instant of a
        // month, with today's occurrence still to come, and a month of 31 days 24 months on —
        // 762 dates to step. (A clock change between the server's zone and the series' can add
        // one.)
        var series = Series(RecurrenceDays.All, new LocalDate(2026, 11, 30));
        var now = Instant.FromUtc(2026, 12, 1, 0, 0);
        Assert.True(PublicCalendarBuilder.IsMonthInRange(2028, 12, now.InUtc().Date));
        Assert.False(PublicCalendarBuilder.IsMonthInRange(2029, 1, now.InUtc().Date));

        var month = PublicCalendarBuilder.Build(null, DateTimeZone.Utc, 2028, 12, [], [series], NoChannels, now);

        Assert.Equal(Enumerable.Range(1, 31), Days(month, projected: true));
    }

    [Fact]
    public void A_day_set_keeps_its_local_time_across_a_clock_change()
    {
        // US clocks go back on Sunday 2026-11-01: 8pm stays 8pm, and the UTC start moves an hour.
        var series = Series(RecurrenceDays.Weekdays, new LocalDate(2026, 10, 26), zone: "America/Chicago");
        series.StartTime = new LocalTime(20, 0);
        var zone = DateTimeZoneProviders.Tzdb["America/Chicago"];
        var now = Instant.FromUtc(2026, 10, 25, 12, 0);

        var october = PublicCalendarBuilder.Build(null, zone, 2026, 10, [Live(series)], [series], NoChannels, now);
        var november = PublicCalendarBuilder.Build(null, zone, 2026, 11, [], [series], NoChannels, now);

        Assert.Equal([27, 28, 29, 30], Days(october, projected: true));
        Assert.Equal(
            [2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 16, 17, 18, 19, 20, 23, 24, 25, 26, 27, 30],
            Days(november, projected: true));
        Assert.All(
            october.Events.Concat(november.Events),
            e => Assert.Equal(new TimeSpan(20, 0, 0), e.StartsAtLocal.TimeOfDay));
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 1, 0, 0, TimeSpan.Zero), october.Events[^1].StartsAtUtc);  // CDT
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 2, 0, 0, TimeSpan.Zero), november.Events[0].StartsAtUtc);   // CST
    }

    [Fact]
    public void A_weekly_series_without_a_day_set_still_shows_its_one_weekday()
    {
        var series = Series(RecurrenceDays.None, Monday);

        Assert.Equal([9, 16, 23, 30], Days(November(series), projected: true));
    }

    // ---------- helpers ----------

    private static PublicCalendarDto November(EventSeries series) => Build(series, 2026, 11);

    /// <summary>The month view of one series whose first occurrence is posted and still ahead.</summary>
    private static PublicCalendarDto Build(EventSeries series, int year, int month)
    {
        var zone = DateTimeZoneProviders.Tzdb[series.TimeZone];
        var (start, end) = PublicCalendarBuilder.MonthWindow(zone, year, month);
        var live = Live(series);
        Event[] inMonth = live.StartsAt >= start && live.StartsAt < end ? [live] : [];
        return PublicCalendarBuilder.Build("Guild", zone, year, month, inMonth, [series], NoChannels, Now);
    }

    /// <summary>Days of the month of the view's entries, in start order.</summary>
    private static int[] Days(PublicCalendarDto month, bool projected) =>
        [.. month.Events.Where(e => e.Projected == projected).Select(e => e.StartsAtLocal.Day)];

    /// <summary>The posted occurrence on the series' slot cursor.</summary>
    private static Event Live(EventSeries series) =>
        Occurrence(series, series.CurrentOccurrenceDate, EventStatus.Scheduled);

    /// <summary>An occurrence row of the series on a date, at the series' local time.</summary>
    private static Event Occurrence(EventSeries series, LocalDate date, EventStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Title = series.Title,
        SeriesId = series.Id,
        StartsAt = (date + series.StartTime).InZoneLeniently(DateTimeZoneProviders.Tzdb[series.TimeZone]).ToInstant(),
        TimeZone = series.TimeZone,
        Status = status,
    };

    private static EventSeries Series(
        RecurrenceDays days, LocalDate anchor, int interval = 1, string zone = "UTC") => new()
    {
        Id = Guid.NewGuid(),
        Unit = RecurrenceUnit.Week,
        Interval = interval,
        DaysOfWeek = days,
        AnchorDate = anchor,
        StartTime = new LocalTime(18, 0),
        TimeZone = zone,
        CurrentOccurrenceDate = anchor,
        OccurrenceCount = 1,
        Title = "Class",
    };
}
