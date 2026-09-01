using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

public class RecurrenceCalculatorTests
{
    private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];

    [Fact]
    public void Daily_and_weekly_intervals_step_from_the_anchor()
    {
        var anchor = new LocalDate(2026, 7, 3); // a Friday

        Assert.Equal(new LocalDate(2026, 7, 6),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Day, 3, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 7, 3)));
        Assert.Equal(new LocalDate(2026, 7, 17),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 7, 3)));

        // Weekly always lands on the anchor weekday, even from a mid-cycle "after".
        var next = RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 7, 9));
        Assert.Equal(IsoDayOfWeek.Friday, next.DayOfWeek);
        Assert.Equal(new LocalDate(2026, 7, 17), next);
    }

    [Fact]
    public void Before_anchor_returns_the_anchor_itself()
    {
        var anchor = new LocalDate(2026, 8, 15);
        Assert.Equal(anchor,
            RecurrenceCalculator.NextDate(RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 8, 1)));
    }

    [Fact]
    public void Monthly_day_31_clamps_without_drifting()
    {
        var anchor = new LocalDate(2026, 1, 31);

        var feb = RecurrenceCalculator.NextDate(RecurrenceUnit.Month, 1, MonthlyMode.DayOfMonth, anchor, anchor);
        Assert.Equal(new LocalDate(2026, 2, 28), feb);

        // Anchor-based math: March returns to the 31st instead of sticking at 28.
        var mar = RecurrenceCalculator.NextDate(RecurrenceUnit.Month, 1, MonthlyMode.DayOfMonth, anchor, feb);
        Assert.Equal(new LocalDate(2026, 3, 31), mar);
    }

    [Fact]
    public void Monthly_nth_weekday_lands_on_the_same_position()
    {
        var anchor = new LocalDate(2026, 7, 17); // 3rd Friday of July 2026

        var next = RecurrenceCalculator.NextDate(RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday, anchor, anchor);
        Assert.Equal(new LocalDate(2026, 8, 21), next); // 3rd Friday of August
        Assert.Equal(IsoDayOfWeek.Friday, next.DayOfWeek);
    }

    [Fact]
    public void Monthly_fifth_weekday_clamps_to_last_when_absent()
    {
        var anchor = new LocalDate(2026, 1, 30); // 5th Friday of January 2026

        // February 2026 has only four Fridays — the rule falls back to the last one.
        var next = RecurrenceCalculator.NextDate(RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday, anchor, anchor);
        Assert.Equal(new LocalDate(2026, 2, 27), next);

        // The rule stays "5th-flavored": May 2026 has five Fridays again and lands on the 5th.
        var may = RecurrenceCalculator.NextDate(
            RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday, anchor, new LocalDate(2026, 4, 30));
        Assert.Equal(new LocalDate(2026, 5, 29), may);
    }

    [Fact]
    public void Yearly_steps_from_the_anchor()
    {
        var anchor = new LocalDate(2026, 8, 31);

        Assert.Equal(new LocalDate(2027, 8, 31),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Year, 1, MonthlyMode.DayOfMonth, anchor, anchor));

        // Every-2-years stays on the anchor grid even from a mid-cycle "after".
        Assert.Equal(new LocalDate(2030, 8, 31),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Year, 2, MonthlyMode.DayOfMonth, anchor, new LocalDate(2028, 9, 1)));
    }

    [Fact]
    public void Yearly_feb_29_clamps_without_drifting()
    {
        var anchor = new LocalDate(2028, 2, 29); // a leap day

        // Non-leap years clamp to Feb 28 (the short-month convention applied to February).
        var y2029 = RecurrenceCalculator.NextDate(RecurrenceUnit.Year, 1, MonthlyMode.DayOfMonth, anchor, anchor);
        Assert.Equal(new LocalDate(2029, 2, 28), y2029);

        var y2030 = RecurrenceCalculator.NextDate(RecurrenceUnit.Year, 1, MonthlyMode.DayOfMonth, anchor, y2029);
        Assert.Equal(new LocalDate(2030, 2, 28), y2030);

        // Anchor-based math: the next leap year returns to Feb 29 instead of sticking at 28.
        var y2032 = RecurrenceCalculator.NextDate(
            RecurrenceUnit.Year, 1, MonthlyMode.DayOfMonth, anchor, new LocalDate(2031, 2, 28));
        Assert.Equal(new LocalDate(2032, 2, 29), y2032);
    }

    [Fact]
    public void Yearly_next_occurrence_is_timezone_aware()
    {
        // Aug 31 18:00 in New York — the next occurrence resolves in the series zone (EDT, UTC-4).
        var anchor = new LocalDate(2026, 8, 31);
        var next = RecurrenceCalculator.NextOccurrence(
            RecurrenceUnit.Year, 1, MonthlyMode.DayOfMonth, anchor, new LocalTime(18, 0),
            NewYork, anchor, null, Instant.FromUtc(2026, 9, 15, 0, 0));

        Assert.NotNull(next);
        Assert.Equal(new LocalDate(2027, 8, 31), next.Value.Date);
        Assert.Equal(Instant.FromUtc(2027, 8, 31, 22, 0), next.Value.Instant);
    }

    [Fact]
    public void Dst_gap_start_time_shifts_leniently()
    {
        // 2026-03-08 02:30 doesn't exist in America/New_York (spring forward) — lenient
        // resolution shifts it forward by the gap instead of throwing.
        var anchor = new LocalDate(2026, 3, 7);
        var next = RecurrenceCalculator.NextOccurrence(
            RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth, anchor, new LocalTime(2, 30),
            NewYork, anchor, null, Instant.FromUtc(2026, 3, 8, 0, 0));

        Assert.NotNull(next);
        Assert.Equal(new LocalDate(2026, 3, 8), next.Value.Date);
        Assert.Equal(Instant.FromUtc(2026, 3, 8, 7, 30), next.Value.Instant); // 03:30 EDT
    }

    [Fact]
    public void Catch_up_skips_missed_slots_to_first_future()
    {
        // Weekly series whose cursor is a month stale (bot was down): one hop to the future.
        var anchor = new LocalDate(2026, 6, 1);
        var now = Instant.FromUtc(2026, 7, 10, 12, 0);
        var next = RecurrenceCalculator.NextOccurrence(
            RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, new LocalTime(18, 0),
            DateTimeZone.Utc, anchor, null, now);

        Assert.NotNull(next);
        Assert.Equal(new LocalDate(2026, 7, 13), next.Value.Date);
        Assert.True(next.Value.Instant > now);
    }

    [Fact]
    public void Until_date_exhaustion_returns_null()
    {
        var anchor = new LocalDate(2026, 7, 1);
        var next = RecurrenceCalculator.NextOccurrence(
            RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, new LocalTime(18, 0),
            DateTimeZone.Utc, anchor, new LocalDate(2026, 7, 7), Instant.FromUtc(2026, 7, 2, 0, 0));

        Assert.Null(next); // next slot (Jul 8) is past the inclusive until date
    }

    [Theory]
    [InlineData(RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth, "Repeats daily")]
    [InlineData(RecurrenceUnit.Day, 3, MonthlyMode.DayOfMonth, "Repeats every 3 days")]
    [InlineData(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, "Repeats weekly on Friday")]
    [InlineData(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, "Repeats every 2 weeks on Friday")]
    [InlineData(RecurrenceUnit.Month, 1, MonthlyMode.DayOfMonth, "Repeats monthly on day 17")]
    [InlineData(RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday, "Repeats monthly on the 3rd Friday")]
    [InlineData(RecurrenceUnit.Year, 1, MonthlyMode.DayOfMonth, "Repeats yearly on Jul 17")]
    [InlineData(RecurrenceUnit.Year, 2, MonthlyMode.DayOfMonth, "Repeats every 2 years on Jul 17")]
    public void Describe_covers_the_rule_matrix(RecurrenceUnit unit, int interval, MonthlyMode mode, string expected)
    {
        Assert.Equal(expected, RecurrenceCalculator.Describe(Series(unit, interval, mode)));
    }

    [Fact]
    public void Describe_appends_count_and_until_suffixes()
    {
        var counted = Series(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth);
        counted.MaxOccurrences = 10;
        counted.OccurrenceCount = 3;
        Assert.Equal("Repeats weekly on Friday · 3 of 10", RecurrenceCalculator.Describe(counted));

        var dated = Series(RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth);
        dated.UntilDate = new LocalDate(2026, 8, 30);
        Assert.Equal("Repeats daily · until Aug 30, 2026", RecurrenceCalculator.Describe(dated));

        var lastWeekday = Series(RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday);
        lastWeekday.AnchorDate = new LocalDate(2026, 1, 30); // 5th Friday
        Assert.Equal("Repeats monthly on the last Friday", RecurrenceCalculator.Describe(lastWeekday));
    }

    private static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];

    private const RecurrenceDays TueThu = RecurrenceDays.Tuesday | RecurrenceDays.Thursday;

    [Fact]
    public void Day_set_rolls_to_the_next_selected_weekday_within_and_across_weeks()
    {
        var anchor = new LocalDate(2026, 9, 1); // a Tuesday

        Assert.Equal(new LocalDate(2026, 9, 3),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, anchor, TueThu));
        Assert.Equal(new LocalDate(2026, 9, 8),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 9, 3), TueThu));

        // A mid-week "after" still lands on the next selected day, never the anchor weekday.
        Assert.Equal(new LocalDate(2026, 9, 10),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 9, 9), TueThu));
    }

    [Fact]
    public void Day_set_with_interval_counts_weeks_monday_first_from_the_anchors_week()
    {
        var anchor = new LocalDate(2026, 9, 1); // Tuesday of the week starting Mon Aug 31

        // Both days of the anchor week fire, the next week is off, then both again.
        var thu = RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, anchor, TueThu);
        Assert.Equal(new LocalDate(2026, 9, 3), thu);
        Assert.Equal(new LocalDate(2026, 9, 15),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, thu, TueThu));
        Assert.Equal(new LocalDate(2026, 9, 17),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 9, 15), TueThu));

        // From inside an off-week (Wed Sep 9) the next slot is the following on-week's Tuesday.
        Assert.Equal(new LocalDate(2026, 9, 15),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 9, 9), TueThu));

        // Sunday is the END of a Monday-first week: Tue+Sun at interval 2 fires Sun Sep 6 in the
        // anchor week, then skips to Tue Sep 15 — not Sun Sep 13, which sits in the off-week.
        var tueSun = RecurrenceDays.Tuesday | RecurrenceDays.Sunday;
        Assert.Equal(new LocalDate(2026, 9, 6),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, anchor, tueSun));
        Assert.Equal(new LocalDate(2026, 9, 15),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 2, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 9, 6), tueSun));
    }

    [Fact]
    public void Anchor_outside_the_day_set_is_still_the_first_occurrence()
    {
        var anchor = new LocalDate(2026, 9, 2); // a Wednesday
        var monFri = RecurrenceDays.Monday | RecurrenceDays.Friday;

        // DTSTART semantics: before the anchor, the anchor; after it, the set takes over.
        Assert.Equal(anchor,
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 8, 1), monFri));
        Assert.Equal(new LocalDate(2026, 9, 4),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, anchor, monFri));
        Assert.Equal(new LocalDate(2026, 9, 7),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, new LocalDate(2026, 9, 4), monFri));
    }

    [Fact]
    public void Weekdays_preset_skips_the_weekend_and_non_week_units_ignore_the_set()
    {
        var friday = new LocalDate(2026, 9, 4);
        Assert.Equal(new LocalDate(2026, 9, 7),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, friday, friday, RecurrenceDays.Weekdays));

        // A stray day set on a daily rule changes nothing (the API rejects it anyway).
        Assert.Equal(new LocalDate(2026, 9, 5),
            RecurrenceCalculator.NextDate(RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth, friday, friday, TueThu));
    }

    [Fact]
    public void Day_set_occurrences_keep_the_wall_clock_time_across_dst_transitions()
    {
        var weekend = RecurrenceDays.Saturday | RecurrenceDays.Sunday;
        var sevenPm = new LocalTime(19, 0);

        // Spring forward: Sat Mar 7 2026 19:00 CST → Sun Mar 8 19:00 CDT is 23 hours later.
        var springAnchor = new LocalDate(2026, 3, 7);
        var beforeSpring = (springAnchor + sevenPm).InZoneStrictly(Chicago).ToInstant();
        var spring = RecurrenceCalculator.NextOccurrence(
            RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, springAnchor, sevenPm, Chicago,
            springAnchor, null, beforeSpring, weekend)!.Value;
        Assert.Equal(new LocalDate(2026, 3, 8), spring.Date);
        Assert.Equal(Duration.FromHours(23), spring.Instant - beforeSpring);
        Assert.Equal(sevenPm, spring.Instant.InZone(Chicago).TimeOfDay);

        // Fall back: Sat Oct 31 2026 19:00 CDT → Sun Nov 1 19:00 CST is 25 hours later.
        var fallAnchor = new LocalDate(2026, 10, 31);
        var beforeFall = (fallAnchor + sevenPm).InZoneStrictly(Chicago).ToInstant();
        var fall = RecurrenceCalculator.NextOccurrence(
            RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, fallAnchor, sevenPm, Chicago,
            fallAnchor, null, beforeFall, weekend)!.Value;
        Assert.Equal(new LocalDate(2026, 11, 1), fall.Date);
        Assert.Equal(Duration.FromHours(25), fall.Instant - beforeFall);
        Assert.Equal(sevenPm, fall.Instant.InZone(Chicago).TimeOfDay);
    }

    [Fact]
    public void Day_set_honors_the_until_date()
    {
        var anchor = new LocalDate(2026, 9, 1);
        var next = RecurrenceCalculator.NextOccurrence(
            RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, anchor, new LocalTime(18, 0),
            DateTimeZone.Utc, new LocalDate(2026, 9, 3), new LocalDate(2026, 9, 7), Instant.FromUtc(2026, 9, 3, 19, 0), TueThu);

        Assert.Null(next); // the next slot (Tue Sep 8) is past the inclusive until date
    }

    [Theory]
    [InlineData(1, RecurrenceDays.Tuesday | RecurrenceDays.Thursday, "Repeats weekly on Tue, Thu")]
    [InlineData(2, RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday, "Repeats every 2 weeks on Mon, Wed, Fri")]
    [InlineData(1, RecurrenceDays.Weekdays, "Repeats every weekday")]
    [InlineData(2, RecurrenceDays.Weekdays, "Repeats every 2 weeks on weekdays")]
    [InlineData(1, RecurrenceDays.Saturday | RecurrenceDays.Sunday, "Repeats weekly on Sat, Sun")]
    [InlineData(1, RecurrenceDays.None, "Repeats weekly on Friday")]
    public void Describe_renders_day_sets(int interval, RecurrenceDays days, string expected)
    {
        var series = Series(RecurrenceUnit.Week, interval, MonthlyMode.DayOfMonth);
        series.DaysOfWeek = days;
        Assert.Equal(expected, RecurrenceCalculator.Describe(series));
    }

    private static EventSeries Series(RecurrenceUnit unit, int interval, MonthlyMode mode) => new()
    {
        Id = Guid.NewGuid(),
        Unit = unit,
        Interval = interval,
        MonthlyMode = mode,
        AnchorDate = new LocalDate(2026, 7, 17), // a 3rd Friday
        StartTime = new LocalTime(18, 0),
        Title = "Sample",
    };
}
