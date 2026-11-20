using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>What the ICS feed asks of a series whose own length can run into its next date. A
/// series that lasts its gap (<see cref="SeriesProjection.LastsItsGap"/>) is listed entry by entry.
/// Any other keeps its repeat rule; one whose length, with the largest step forward of its zone's
/// clocks in the time looked at added, reaches the fewest days between two of its dates
/// (<see cref="SeriesProjection.CanHoldDatesBack"/>) has the dates it holds back
/// (<see cref="SeriesProjection.HeldBackDates"/>) written into it as exceptions. Fixed dates, no
/// database.</summary>
public class SeriesHoldBackTests
{
    // A year from 1 January 2027: New York's clocks go forward on 14 March and back on 7 November.
    private static readonly Instant From = Instant.FromUtc(2027, 1, 1, 0, 0);
    private static readonly Instant AYearOn = From + Duration.FromDays(366);

    [Theory]
    // UTC's clocks do not change: a day is 24 hours, a week seven days.
    [InlineData("UTC", RecurrenceUnit.Day, RecurrenceDays.None, 23 * 60 + 59, false)]
    [InlineData("UTC", RecurrenceUnit.Day, RecurrenceDays.None, 24 * 60, true)]
    [InlineData("UTC", RecurrenceUnit.Week, RecurrenceDays.None, 7 * 24 * 60 - 1, false)]
    [InlineData("UTC", RecurrenceUnit.Week, RecurrenceDays.None, 7 * 24 * 60, true)]
    [InlineData("UTC", RecurrenceUnit.Week, RecurrenceDays.Monday | RecurrenceDays.Thursday, 3 * 24 * 60 - 1, false)]
    [InlineData("UTC", RecurrenceUnit.Week, RecurrenceDays.Monday | RecurrenceDays.Thursday, 3 * 24 * 60, true)]
    [InlineData("UTC", RecurrenceUnit.Month, RecurrenceDays.None, 28 * 24 * 60 - 1, false)]
    [InlineData("UTC", RecurrenceUnit.Month, RecurrenceDays.None, 28 * 24 * 60, true)]
    [InlineData("UTC", RecurrenceUnit.Year, RecurrenceDays.None, 28 * 24 * 60, false)]
    // New York's clocks go forward an hour in the year: the night they do is 23 hours long.
    [InlineData("America/New_York", RecurrenceUnit.Day, RecurrenceDays.None, 22 * 60 + 59, false)]
    [InlineData("America/New_York", RecurrenceUnit.Day, RecurrenceDays.None, 23 * 60, true)]
    [InlineData("America/New_York", RecurrenceUnit.Week, RecurrenceDays.None, 7 * 24 * 60 - 61, false)]
    [InlineData("America/New_York", RecurrenceUnit.Week, RecurrenceDays.None, 7 * 24 * 60 - 60, true)]
    // A zone id the zone database does not have is read as UTC, as everywhere else.
    [InlineData("Nowhere/Unknown", RecurrenceUnit.Day, RecurrenceDays.None, 23 * 60 + 59, false)]
    [InlineData("Nowhere/Unknown", RecurrenceUnit.Day, RecurrenceDays.None, 24 * 60, true)]
    public void A_series_can_hold_dates_back_when_its_length_and_its_zones_step_forward_reach_its_shortest_gap(
        string zone, RecurrenceUnit unit, RecurrenceDays days, int minutes, bool expected) =>
        Assert.Equal(expected, SeriesProjection.CanHoldDatesBack(Series(zone, unit, days, minutes), From, AYearOn));

    [Fact]
    public void A_series_with_no_length_set_runs_an_hour_and_holds_no_daily_date_back() =>
        Assert.False(SeriesProjection.CanHoldDatesBack(Series("UTC", RecurrenceUnit.Day, RecurrenceDays.None, null), From, AYearOn));

    [Fact]
    public void Only_a_step_forward_within_the_time_looked_at_counts()
    {
        // 23 hours a day in New York, looked at from June to August, when the clocks stay where
        // they are, and over the night they go back in November: no night is shorter than a day.
        var series = Series("America/New_York", RecurrenceUnit.Day, RecurrenceDays.None, 23 * 60);
        Assert.False(SeriesProjection.CanHoldDatesBack(series, Instant.FromUtc(2027, 6, 1, 0, 0), Instant.FromUtc(2027, 8, 31, 0, 0)));
        Assert.False(SeriesProjection.CanHoldDatesBack(series, Instant.FromUtc(2027, 10, 1, 0, 0), Instant.FromUtc(2027, 12, 31, 0, 0)));

        // And over the night they go forward in March, it can.
        Assert.True(SeriesProjection.CanHoldDatesBack(series, Instant.FromUtc(2027, 3, 1, 0, 0), Instant.FromUtc(2027, 3, 31, 0, 0)));
    }

    [Fact]
    public void What_the_choice_says_matches_what_the_series_posts_over_a_year()
    {
        // Daily and Mon + Wed series of every length in steps of a quarter of an hour, from three
        // hours short of their gap to a day and a half past it, in UTC and in New York: wherever
        // the helper's dates leave out a date the schedule names, the choice says a date can be
        // held back. Below the gap less the clock's step, the choice says none can be.
        foreach (var zone in new[] { "UTC", "America/New_York" })
        {
            foreach (var (unit, days, gap) in new[]
                     {
                         (RecurrenceUnit.Day, RecurrenceDays.None, 24 * 60),
                         (RecurrenceUnit.Week, RecurrenceDays.Monday | RecurrenceDays.Wednesday, 2 * 24 * 60),
                     })
            {
                var step = zone == "UTC" ? 0 : 60;
                for (var minutes = gap - 3 * 60; minutes <= gap + 36 * 60; minutes += 15)
                {
                    var series = Series(zone, unit, days, minutes);
                    var posted = SeriesProjection.Upcoming(series, From, 1000).TakeWhile(x => x < AYearOn).ToList();
                    var named = Every(series, From, AYearOn);
                    var can = SeriesProjection.CanHoldDatesBack(series, From, AYearOn);
                    Assert.True(can || named.SequenceEqual(posted), $"{zone}, {unit}, {minutes} min: a date is held back");
                    Assert.Equal(minutes >= gap - step, can);

                    // Every date the schedule names is either posted or held back, never both.
                    var held = SeriesProjection.HeldBackDates(series, From, AYearOn, 1000).Dates;
                    Assert.Empty(held.Intersect(posted));
                    Assert.Equal(named, posted.Concat(held).Order());
                }
            }
        }
    }

    [Theory]
    [InlineData("UTC", RecurrenceUnit.Day, RecurrenceDays.None, 24 * 60, true)]
    [InlineData("UTC", RecurrenceUnit.Day, RecurrenceDays.None, 24 * 60 - 1, false)]
    [InlineData("America/New_York", RecurrenceUnit.Day, RecurrenceDays.None, 24 * 60 - 1, false)]
    [InlineData("UTC", RecurrenceUnit.Week, RecurrenceDays.None, 7 * 24 * 60, true)]
    [InlineData("UTC", RecurrenceUnit.Week, RecurrenceDays.Monday | RecurrenceDays.Thursday, 3 * 24 * 60, true)]
    [InlineData("UTC", RecurrenceUnit.Week, RecurrenceDays.Monday | RecurrenceDays.Thursday, 3 * 24 * 60 - 1, false)]
    [InlineData("UTC", RecurrenceUnit.Month, RecurrenceDays.None, 28 * 24 * 60, true)]
    public void A_series_lasts_its_gap_when_its_length_reaches_the_gap_as_the_calendar_shows_it(
        string zone, RecurrenceUnit unit, RecurrenceDays days, int minutes, bool expected) =>
        Assert.Equal(expected, SeriesProjection.LastsItsGap(Series(zone, unit, days, minutes)));

    [Fact]
    public void Held_back_dates_of_a_series_a_minute_short_of_a_day_are_the_dates_after_a_night_the_clocks_go_forward()
    {
        // Daily at 09:00 New York time from Thursday 11 March 2027, 5 times, 23 h 59 min: the clocks
        // go forward in the night to Sunday 14, so Saturday's occurrence ends at 09:59 on Sunday and
        // Sunday's date is held back. The fifth and last date posted is Tuesday 16.
        var zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        var series = Series("America/New_York", RecurrenceUnit.Day, RecurrenceDays.None, 23 * 60 + 59, new LocalDate(2027, 3, 11));
        series.MaxOccurrences = 5;
        var freeFrom = EventTimes.End(At(zone, 2027, 3, 11), series.DurationMinutes);

        var counted = SeriesProjection.HeldBackDates(series, freeFrom, AYearOn, 1000);
        Assert.Equal([At(zone, 2027, 3, 14)], counted.Dates);
        Assert.Equal(At(zone, 2027, 3, 16), counted.LastPosted);

        // Without a count: the same date, and no other in the year; no last date.
        series.MaxOccurrences = null;
        var endless = SeriesProjection.HeldBackDates(series, freeFrom, AYearOn, 1000);
        Assert.Equal([At(zone, 2027, 3, 14)], endless.Dates);
        Assert.Null(endless.LastPosted);
    }

    [Fact]
    public void Held_back_dates_are_the_schedule_less_the_dates_the_helper_lists_from_its_first()
    {
        // A day long, 4 times, in UTC, its first occurrence posted on Monday 4 January: from the
        // first date the helper lists, Wednesday, every second date is held back, and the helper
        // lists the others. Tuesday, held back by the posted occurrence, lies before that first
        // date, where the feed's repeat rule does not reach. 23 hours a day: none is held back.
        var day = Series("UTC", RecurrenceUnit.Day, RecurrenceDays.None, 24 * 60, new LocalDate(2027, 1, 4));
        day.MaxOccurrences = 4;
        var freeFrom = EventTimes.End(At(DateTimeZone.Utc, 2027, 1, 4), day.DurationMinutes);
        var held = SeriesProjection.HeldBackDates(day, freeFrom, AYearOn, 1000);
        Assert.Equal([At(DateTimeZone.Utc, 2027, 1, 7), At(DateTimeZone.Utc, 2027, 1, 9)], held.Dates);
        Assert.Equal(At(DateTimeZone.Utc, 2027, 1, 10), held.LastPosted);
        Assert.Equal(
            [At(DateTimeZone.Utc, 2027, 1, 6), At(DateTimeZone.Utc, 2027, 1, 8), At(DateTimeZone.Utc, 2027, 1, 10)],
            SeriesProjection.Upcoming(day, freeFrom, 1000));

        var short23 = Series("UTC", RecurrenceUnit.Day, RecurrenceDays.None, 23 * 60, new LocalDate(2027, 1, 4));
        Assert.Empty(SeriesProjection.HeldBackDates(short23, From, AYearOn, 1000).Dates);
    }

    [Fact]
    public void Held_back_dates_stop_at_the_end_of_the_time_looked_at()
    {
        // The night the clocks go forward in New York, 14 March 2027, lies after the time looked at.
        var series = Series("America/New_York", RecurrenceUnit.Day, RecurrenceDays.None, 23 * 60 + 59, new LocalDate(2027, 3, 1));
        var held = SeriesProjection.HeldBackDates(series, From, Instant.FromUtc(2027, 3, 13, 0, 0), 1000);
        Assert.Empty(held.Dates);
        Assert.Null(held.LastPosted);
    }

    private static Instant At(DateTimeZone zone, int year, int month, int day) =>
        new LocalDateTime(year, month, day, 9, 0).InZoneLeniently(zone).ToInstant();

    /// <summary>Every date the schedule names after the cursor, from <paramref name="from"/>.</summary>
    private static List<Instant> Every(EventSeries series, Instant from, Instant until)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(series.TimeZone) ?? DateTimeZone.Utc;
        var dates = new List<Instant>();
        var cursor = series.CurrentOccurrenceDate;
        var floor = from;
        while (RecurrenceCalculator.NextOccurrence(
                   series.Unit, series.Interval, series.MonthlyMode, series.AnchorDate, series.StartTime,
                   zone, cursor, series.UntilDate, floor, series.DaysOfWeek) is { } next
               && next.Instant < until)
        {
            dates.Add(next.Instant);
            cursor = next.Date;
            floor = next.Instant;
        }

        return dates;
    }

    private static EventSeries Series(string zone, RecurrenceUnit unit, RecurrenceDays days, int? minutes, LocalDate? anchor = null) => new()
    {
        Id = Guid.NewGuid(),
        Unit = unit,
        Interval = 1,
        DaysOfWeek = days,
        AnchorDate = anchor ?? new LocalDate(2026, 12, 28), // a Monday
        StartTime = new LocalTime(9, 0),
        TimeZone = zone,
        CurrentOccurrenceDate = anchor ?? new LocalDate(2026, 12, 28),
        OccurrenceCount = 1,
        DurationMinutes = minutes,
        Title = "Sample",
    };
}
