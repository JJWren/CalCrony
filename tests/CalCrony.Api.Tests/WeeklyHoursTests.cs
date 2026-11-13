using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>A member's week of available hours: one range per weekday, minutes from midnight.</summary>
public class WeeklyHoursTests
{
    [Fact]
    public void The_default_week_is_the_contract_default_on_all_seven_days()
    {
        // 8 AM–11 PM, stated once in Contracts so the bot's reply and the API cannot disagree.
        Assert.Equal(480, AvailableHoursLimits.DefaultStartMinute);
        Assert.Equal(1380, AvailableHoursLimits.DefaultEndMinute);

        foreach (var day in Enum.GetValues<IsoDayOfWeek>().Where(d => d != IsoDayOfWeek.None))
        {
            Assert.Equal(
                (AvailableHoursLimits.DefaultStartMinute, AvailableHoursLimits.DefaultEndMinute),
                WeeklyHours.Default.RangeFor(day));
        }
    }

    [Fact]
    public void A_weekday_without_a_range_is_unavailable()
    {
        var week = new WeeklyHours(new Dictionary<IsoDayOfWeek, (int, int)> { [IsoDayOfWeek.Saturday] = (600, 840) });

        Assert.Equal((600, 840), week.RangeFor(IsoDayOfWeek.Saturday));
        Assert.Null(week.RangeFor(IsoDayOfWeek.Sunday));
        Assert.Null(week.RangeFor(IsoDayOfWeek.None));
    }

    [Fact]
    public void A_range_may_fill_the_whole_day()
    {
        var week = new WeeklyHours(new Dictionary<IsoDayOfWeek, (int, int)> { [IsoDayOfWeek.Friday] = (0, 1440) });

        Assert.Equal((0, 1440), week.RangeFor(IsoDayOfWeek.Friday));
    }

    [Theory]
    [InlineData(-30, 60)]       // starts before the day does
    [InlineData(600, 1470)]     // ends after it
    [InlineData(600, 600)]      // empty
    [InlineData(700, 600)]      // ends before it starts — no overnight ranges
    public void A_range_must_lie_inside_one_day_and_start_before_it_ends(int startMinute, int endMinute)
    {
        Assert.Throws<ArgumentException>(() => new WeeklyHours(
            new Dictionary<IsoDayOfWeek, (int, int)> { [IsoDayOfWeek.Monday] = (startMinute, endMinute) }));
    }

    [Fact]
    public void A_range_needs_a_real_weekday()
    {
        Assert.Throws<ArgumentException>(() => new WeeklyHours(
            new Dictionary<IsoDayOfWeek, (int, int)> { [IsoDayOfWeek.None] = (600, 840) }));
    }

    [Fact]
    public void The_week_keeps_its_own_copy_of_the_ranges()
    {
        var source = new Dictionary<IsoDayOfWeek, (int, int)> { [IsoDayOfWeek.Monday] = (600, 840) };
        var week = new WeeklyHours(source);

        source[IsoDayOfWeek.Monday] = (0, 60);
        source[IsoDayOfWeek.Tuesday] = (0, 60);

        Assert.Equal((600, 840), week.RangeFor(IsoDayOfWeek.Monday));
        Assert.Null(week.RangeFor(IsoDayOfWeek.Tuesday));
    }

    [Fact]
    public void A_submitted_week_is_built_from_its_days()
    {
        var ok = WeeklyHours.TryFromDto(
            [new(DayOfWeek.Monday, 1080, 1380), new(DayOfWeek.Sunday, 0, 1440)], out var week, out var error);

        Assert.True(ok, error);
        Assert.Null(error);
        Assert.Equal((1080, 1380), week!.RangeFor(IsoDayOfWeek.Monday));

        // The BCL's Sunday is 0 and NodaTime's is 7; the week is the same day either way.
        Assert.Equal((0, 1440), week.RangeFor(IsoDayOfWeek.Sunday));
        Assert.Null(week.RangeFor(IsoDayOfWeek.Tuesday));
    }

    public static TheoryData<AvailableHoursDayDto[]?, string> RefusedWeeks() => new()
    {
        { null, "Pick at least one day, or reset to go back to the default hours." },
        { [], "Pick at least one day, or reset to go back to the default hours." },
        { [.. Enumerable.Repeat(new AvailableHoursDayDto(DayOfWeek.Monday, 600, 840), 8)], "A week has seven days." },
        { [new((DayOfWeek)9, 600, 840)], "9 is not a day of the week." },
        { [null!], "null is not a day of the week." },
        { [new(DayOfWeek.Monday, 600, 840), new(DayOfWeek.Monday, 900, 960)], "Monday is listed twice." },
        { [new(DayOfWeek.Monday, -30, 840)], "Monday: a time is 0 to 1440 minutes after midnight." },
        { [new(DayOfWeek.Monday, 600, 1470)], "Monday: a time is 0 to 1440 minutes after midnight." },
        { [new(DayOfWeek.Friday, 495, 840)], "Friday: times are in 30-minute steps." },
        { [new(DayOfWeek.Friday, 600, 845)], "Friday: times are in 30-minute steps." },
        { [new(DayOfWeek.Saturday, 600, 600)], "Saturday: the start has to be before the end." },
        { [new(DayOfWeek.Saturday, 1320, 120)], "Saturday: the start has to be before the end." },   // no overnight ranges
    };

    [Theory]
    [MemberData(nameof(RefusedWeeks))]
    public void A_submitted_week_that_cannot_be_saved_is_refused_with_the_reason(AvailableHoursDayDto[]? days, string expected)
    {
        Assert.False(WeeklyHours.TryFromDto(days, out var week, out var error));

        Assert.Null(week);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void The_first_failing_check_gives_the_reason()
    {
        // Within one entry the checks run in a fixed order: twice, outside the day, off the step,
        // out of order.
        Assert.False(WeeklyHours.TryFromDto(
            [new(DayOfWeek.Monday, 600, 840), new(DayOfWeek.Monday, -15, 2000)], out _, out var twice));
        Assert.Equal("Monday is listed twice.", twice);

        Assert.False(WeeklyHours.TryFromDto([new(DayOfWeek.Monday, -15, 840)], out _, out var outside));
        Assert.Equal("Monday: a time is 0 to 1440 minutes after midnight.", outside);

        Assert.False(WeeklyHours.TryFromDto([new(DayOfWeek.Monday, 615, 600)], out _, out var offStep));
        Assert.Equal("Monday: times are in 30-minute steps.", offStep);

        // Entries are read in the order sent, and the count is checked before any of them.
        Assert.False(WeeklyHours.TryFromDto(
            [new(DayOfWeek.Tuesday, 600, 600), new((DayOfWeek)9, 0, 30)], out _, out var firstEntry));
        Assert.Equal("Tuesday: the start has to be before the end.", firstEntry);

        Assert.False(WeeklyHours.TryFromDto(
            [.. Enumerable.Repeat(new AvailableHoursDayDto((DayOfWeek)9, -1, -1), 8)], out _, out var tooMany));
        Assert.Equal("A week has seven days.", tooMany);
    }

    [Fact]
    public void A_week_is_stored_as_its_days_in_order_under_fixed_names()
    {
        Assert.True(WeeklyHours.TryFromDto(
            [new(DayOfWeek.Sunday, 600, 1440), new(DayOfWeek.Monday, 1080, 1380)], out var week, out _));

        // Monday is 1 and Sunday 7, whatever order the days were sent in.
        Assert.Equal("""[{"day":1,"start":1080,"end":1380},{"day":7,"start":600,"end":1440}]""", week!.Serialize());
    }

    [Fact]
    public void A_stored_week_reads_back_as_it_was_saved()
    {
        Assert.True(WeeklyHours.TryFromDto(
            [new(DayOfWeek.Wednesday, 0, 30), new(DayOfWeek.Saturday, 600, 1440)], out var saved, out _));

        var read = WeeklyHours.Parse(saved!.Serialize());

        Assert.NotNull(read);
        foreach (var day in Enum.GetValues<IsoDayOfWeek>())
        {
            Assert.Equal(saved.RangeFor(day), read.RangeFor(day));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"monday\"")]
    [InlineData("[]")]                                                        // no day at all
    [InlineData("[null]")]
    [InlineData("""[{"day":0,"start":480,"end":1380}]""")]                    // no such weekday
    [InlineData("""[{"day":8,"start":480,"end":1380}]""")]
    [InlineData("""[{"day":1,"start":480,"end":1380},{"day":1,"start":0,"end":60}]""")]     // a weekday twice
    [InlineData("""[{"day":1,"start":600,"end":600}]""")]
    [InlineData("""[{"day":1,"start":900,"end":600}]""")]
    [InlineData("""[{"day":1,"start":-30,"end":600}]""")]
    [InlineData("""[{"day":1,"start":600,"end":1470}]""")]
    [InlineData("""[{"day":1}]""")]                                           // no times
    [InlineData("""[{"Day":1,"Start":480,"End":1380}]""")]                    // not the stored names
    [InlineData("""[{"day":1.5,"start":480,"end":1380}]""")]
    [InlineData("""[{"day":1,"start":480,"end":1380}""")]                     // cut short
    public void A_stored_value_that_is_not_a_week_reads_as_nothing_saved(string? stored)
    {
        Assert.Null(WeeklyHours.Parse(stored));
    }

    [Fact]
    public void More_than_seven_stored_days_read_as_nothing_saved()
    {
        var eight = "[" + string.Join(",", Enumerable.Range(1, 8).Select(d => $$"""{"day":{{d}},"start":480,"end":1380}""")) + "]";

        Assert.Null(WeeklyHours.Parse(eight));
    }

    [Fact]
    public void A_stored_time_off_the_step_is_still_read()
    {
        // The step is a rule for what a member may enter. A week saved under another step has to
        // stay readable.
        var week = WeeklyHours.Parse("""[{"day":2,"start":495,"end":1375}]""");

        Assert.Equal((495, 1375), week!.RangeFor(IsoDayOfWeek.Tuesday));
    }

    [Fact]
    public void On_the_wire_a_week_lists_its_available_days_monday_first()
    {
        Assert.True(WeeklyHours.TryFromDto(
            [new(DayOfWeek.Sunday, 600, 1440), new(DayOfWeek.Friday, 1080, 1380), new(DayOfWeek.Monday, 480, 720)],
            out var week,
            out _));

        Assert.Equal(
            [
                new AvailableHoursDayDto(DayOfWeek.Monday, 480, 720),
                new AvailableHoursDayDto(DayOfWeek.Friday, 1080, 1380),
                new AvailableHoursDayDto(DayOfWeek.Sunday, 600, 1440),
            ],
            week!.ToDto());
    }

    [Fact]
    public void The_default_week_on_the_wire_is_all_seven_days()
    {
        var days = WeeklyHours.Default.ToDto();

        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            days.Select(d => d.Day));
        Assert.All(days, d => Assert.Equal(
            (AvailableHoursLimits.DefaultStartMinute, AvailableHoursLimits.DefaultEndMinute), (d.StartMinute, d.EndMinute)));
    }

    [Fact]
    public void The_longest_week_fits_the_column()
    {
        Assert.True(WeeklyHours.TryFromDto(
            [.. Enum.GetValues<DayOfWeek>().Select(day => new AvailableHoursDayDto(day, 1410, 1440))], out var week, out _));

        Assert.Equal(239, week!.Serialize().Length);
        Assert.True(week.Serialize().Length <= WeeklyHours.MaxStoredLength);
    }
}
