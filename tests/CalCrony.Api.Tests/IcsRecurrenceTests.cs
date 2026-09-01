using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Ical.Net;
using NodaTime;

namespace CalCrony.Api.Tests;

public class IcsRecurrenceTests
{
    [Fact]
    public void Daily_and_weekly_map_frequency_interval_and_anchor_weekday()
    {
        var daily = IcsRecurrence.BuildPattern(Series(RecurrenceUnit.Day, 3), anchorIsCounted: true);
        Assert.Equal(FrequencyType.Daily, daily.Frequency);
        Assert.Equal(3, daily.Interval);
        Assert.Empty(daily.ByDay);

        var weekly = IcsRecurrence.BuildPattern(Series(RecurrenceUnit.Week, 2), anchorIsCounted: true);
        Assert.Equal(FrequencyType.Weekly, weekly.Frequency);
        Assert.Equal(2, weekly.Interval);
        var day = Assert.Single(weekly.ByDay);
        Assert.Equal(DayOfWeek.Friday, day.DayOfWeek); // anchor 2026-07-17 is a Friday
        Assert.Null(day.Offset);
    }

    [Fact]
    public void Monthly_day_of_month_stays_plain_through_28()
    {
        var series = Series(RecurrenceUnit.Month, 1);
        series.AnchorDate = new LocalDate(2026, 7, 15);

        var pattern = IcsRecurrence.BuildPattern(series, anchorIsCounted: true);

        Assert.Equal(FrequencyType.Monthly, pattern.Frequency);
        Assert.Equal([15], pattern.ByMonthDay);
        Assert.Empty(pattern.BySetPosition);
    }

    [Fact]
    public void Monthly_day_31_uses_the_clamp_idiom()
    {
        // RFC's plain BYMONTHDAY=31 SKIPS short months; our engine clamps. The 28..31 +
        // BYSETPOS=-1 idiom picks min(31, month length) — clamp semantics exactly.
        var series = Series(RecurrenceUnit.Month, 1);
        series.AnchorDate = new LocalDate(2026, 1, 31);

        var pattern = IcsRecurrence.BuildPattern(series, anchorIsCounted: true);

        Assert.Equal([28, 29, 30, 31], pattern.ByMonthDay);
        Assert.Equal([-1], pattern.BySetPosition);
    }

    [Fact]
    public void Monthly_nth_weekday_maps_ordinal_and_fifth_maps_to_last()
    {
        var third = Series(RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday);
        third.AnchorDate = new LocalDate(2026, 7, 17); // 3rd Friday
        var thirdDay = Assert.Single(IcsRecurrence.BuildPattern(third, true).ByDay);
        Assert.Equal(DayOfWeek.Friday, thirdDay.DayOfWeek);
        Assert.Equal(3, thirdDay.Offset);

        var fifth = Series(RecurrenceUnit.Month, 1, MonthlyMode.NthWeekday);
        fifth.AnchorDate = new LocalDate(2026, 1, 30); // 5th Friday — a 5th, when present, IS the last
        var fifthDay = Assert.Single(IcsRecurrence.BuildPattern(fifth, true).ByDay);
        Assert.Equal(-1, fifthDay.Offset);
    }

    [Fact]
    public void Yearly_maps_frequency_interval_and_anchor_month_day()
    {
        var series = Series(RecurrenceUnit.Year, 2);
        series.AnchorDate = new LocalDate(2026, 8, 31);

        var pattern = IcsRecurrence.BuildPattern(series, anchorIsCounted: true);

        Assert.Equal(FrequencyType.Yearly, pattern.Frequency);
        Assert.Equal(2, pattern.Interval);
        Assert.Equal([8], pattern.ByMonth);
        Assert.Equal([31], pattern.ByMonthDay);
        Assert.Empty(pattern.BySetPosition);
    }

    [Fact]
    public void Yearly_feb_29_uses_the_clamp_idiom()
    {
        // RFC's plain BYMONTHDAY=29 SKIPS non-leap years; our engine clamps to Feb 28. The
        // 28,29 + BYSETPOS=-1 idiom picks min(29, February's length) — clamp semantics exactly.
        var series = Series(RecurrenceUnit.Year, 1);
        series.AnchorDate = new LocalDate(2028, 2, 29);

        var pattern = IcsRecurrence.BuildPattern(series, anchorIsCounted: true);

        Assert.Equal(FrequencyType.Yearly, pattern.Frequency);
        Assert.Equal([2], pattern.ByMonth);
        Assert.Equal([28, 29], pattern.ByMonthDay);
        Assert.Equal([-1], pattern.BySetPosition);
    }

    [Fact]
    public void Until_converts_the_inclusive_local_date_to_utc_end_of_day()
    {
        var series = Series(RecurrenceUnit.Week, 1);
        series.TimeZone = "America/Chicago";
        series.UntilDate = new LocalDate(2026, 8, 30); // CDT, UTC-5

        var pattern = IcsRecurrence.BuildPattern(series, anchorIsCounted: true);

        Assert.NotNull(pattern.Until);
        Assert.Equal(new DateTime(2026, 8, 31, 4, 59, 59, DateTimeKind.Utc), pattern.Until!.Value);
        Assert.Null(pattern.Count);
    }

    [Fact]
    public void Count_is_the_remaining_occurrences_for_both_anchor_modes()
    {
        var series = Series(RecurrenceUnit.Week, 1);
        series.MaxOccurrences = 10;
        series.OccurrenceCount = 3;

        // Live occurrence is already counted, so it plus 7 more = 8 instances from DTSTART.
        Assert.Equal(8, IcsRecurrence.BuildPattern(series, anchorIsCounted: true).Count);

        // A computed next slot isn't counted yet: 7 instances remain from DTSTART.
        Assert.Equal(7, IcsRecurrence.BuildPattern(series, anchorIsCounted: false).Count);
    }

    [Fact]
    public void Unbounded_series_leave_until_and_count_unset()
    {
        var pattern = IcsRecurrence.BuildPattern(Series(RecurrenceUnit.Day, 1), anchorIsCounted: true);
        Assert.Null(pattern.Until);
        Assert.Null(pattern.Count);
    }

    [Fact]
    public void Weekly_day_set_maps_to_byday_list_with_interval_and_monday_week_start()
    {
        var series = Series(RecurrenceUnit.Week, 2);
        series.DaysOfWeek = RecurrenceDays.Tuesday | RecurrenceDays.Thursday;

        var pattern = IcsRecurrence.BuildPattern(series, anchorIsCounted: true);

        Assert.Equal(FrequencyType.Weekly, pattern.Frequency);
        Assert.Equal(2, pattern.Interval);
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Thursday], pattern.ByDay.Select(d => d.DayOfWeek));
        Assert.All(pattern.ByDay, d => Assert.Null(d.Offset));
        // The calculator counts interval weeks Monday-first; WKST=MO keeps clients in step.
        Assert.Equal(DayOfWeek.Monday, pattern.FirstDayOfWeek);
    }

    [Fact]
    public void Weekdays_preset_lists_monday_through_friday_in_order()
    {
        var series = Series(RecurrenceUnit.Week, 1);
        series.DaysOfWeek = RecurrenceDays.Weekdays;

        var pattern = IcsRecurrence.BuildPattern(series, anchorIsCounted: true);

        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            pattern.ByDay.Select(d => d.DayOfWeek));
    }

    [Fact]
    public void Weekly_day_set_serializes_as_byday_rrule()
    {
        var series = Series(RecurrenceUnit.Week, 2);
        series.DaysOfWeek = RecurrenceDays.Tuesday | RecurrenceDays.Thursday;

        var rrule = new Ical.Net.Serialization.DataTypes.RecurrencePatternSerializer()
            .SerializeToString(IcsRecurrence.BuildPattern(series, anchorIsCounted: true))!;

        Assert.Contains("FREQ=WEEKLY", rrule);
        Assert.Contains("INTERVAL=2", rrule);
        Assert.Contains("BYDAY=TU,TH", rrule);
    }

    private static EventSeries Series(
        RecurrenceUnit unit, int interval, MonthlyMode mode = MonthlyMode.DayOfMonth) => new()
    {
        Id = Guid.NewGuid(),
        Unit = unit,
        Interval = interval,
        MonthlyMode = mode,
        AnchorDate = new LocalDate(2026, 7, 17), // a Friday
        StartTime = new LocalTime(18, 0),
        TimeZone = "UTC",
        OccurrenceCount = 1,
        Title = "Sample",
    };
}
