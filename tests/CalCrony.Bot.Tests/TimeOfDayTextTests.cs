using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class TimeOfDayTextTests
{
    [Theory]
    [InlineData(480, "8 AM")]
    [InlineData(1380, "11 PM")]
    [InlineData(1110, "6:30 PM")]
    [InlineData(545, "9:05 AM")]
    [InlineData(0, "12 AM")]
    [InlineData(30, "12:30 AM")]
    [InlineData(720, "12 PM")]
    [InlineData(1439, "11:59 PM")]
    [InlineData(1440, "midnight")]      // the end of a day, not the start of one
    public void Formats_minutes_from_midnight_on_the_12_hour_clock(int minutes, string expected)
    {
        Assert.Equal(expected, TimeOfDayText.Format(minutes));
    }

    [Fact]
    public void The_default_available_hours_read_as_8_AM_and_11_PM()
    {
        Assert.Equal("8 AM", TimeOfDayText.Format(AvailableHoursLimits.DefaultStartMinute));
        Assert.Equal("11 PM", TimeOfDayText.Format(AvailableHoursLimits.DefaultEndMinute));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void A_time_outside_the_day_is_refused(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeOfDayText.Format(minutes));
    }
}
