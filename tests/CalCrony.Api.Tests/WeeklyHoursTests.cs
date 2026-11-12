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
}
