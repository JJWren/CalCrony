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
