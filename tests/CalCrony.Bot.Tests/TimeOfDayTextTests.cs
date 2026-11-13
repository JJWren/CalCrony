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
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeOfDayText.To24Hour(minutes));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(390, "06:30")]
    [InlineData(1110, "18:30")]
    [InlineData(1410, "23:30")]
    [InlineData(1440, "24:00")]     // the end of a day
    public void The_24_hour_form_has_two_digits_each(int minutes, string expected)
    {
        Assert.Equal(expected, TimeOfDayText.To24Hour(minutes));
    }

    [Theory]
    [InlineData("6pm", 1080)]
    [InlineData("6 pm", 1080)]
    [InlineData("6:30pm", 1110)]
    [InlineData("6:30 PM", 1110)]
    [InlineData("  6:30 pm  ", 1110)]      // outer spaces and case do not matter
    [InlineData("12 pm", 720)]
    [InlineData("12:30am", 30)]
    [InlineData("11:59pm", 1439)]
    [InlineData("1am", 60)]
    [InlineData("18:30", 1110)]
    [InlineData("18", 1080)]
    [InlineData("7", 420)]                 // a bare hour is the 24-hour clock
    [InlineData("07:05", 425)]
    [InlineData("23:59", 1439)]
    [InlineData("noon", 720)]
    [InlineData("NOON", 720)]
    public void A_typed_time_is_read_the_same_as_a_start_and_as_an_end(string text, int expected)
    {
        Assert.True(TimeOfDayText.TryParse(text, asEnd: false, out var asStart));
        Assert.True(TimeOfDayText.TryParse(text, asEnd: true, out var asEnd));

        Assert.Equal(expected, asStart);
        Assert.Equal(expected, asEnd);
    }

    [Theory]
    [InlineData("midnight")]
    [InlineData("Midnight")]
    [InlineData("12am")]
    [InlineData("12 AM")]
    [InlineData("0:00")]
    [InlineData("00:00")]
    [InlineData("0")]
    public void Midnight_starts_the_day_as_a_start_and_ends_it_as_an_end(string text)
    {
        Assert.True(TimeOfDayText.TryParse(text, asEnd: false, out var asStart));
        Assert.True(TimeOfDayText.TryParse(text, asEnd: true, out var asEnd));

        Assert.Equal(0, asStart);
        Assert.Equal(AvailableHoursLimits.MinutesPerDay, asEnd);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("24")]
    public void Twenty_four_hundred_is_an_end_only(string text)
    {
        Assert.True(TimeOfDayText.TryParse(text, asEnd: true, out var asEnd));
        Assert.Equal(AvailableHoursLimits.MinutesPerDay, asEnd);

        Assert.False(TimeOfDayText.TryParse(text, asEnd: false, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("25:00")]
    [InlineData("24:30")]
    [InlineData("6:60")]
    [InlineData("6:5")]
    [InlineData("6:305")]
    [InlineData("13pm")]
    [InlineData("0pm")]
    [InlineData("pm")]
    [InlineData("six")]
    [InlineData("6p")]
    [InlineData("6 :30pm")]
    [InlineData("1830")]
    [InlineData("-6")]
    [InlineData("6.30pm")]
    [InlineData("٦pm")]                    // digits other than 0–9
    [InlineData("tomorrow 6pm")]
    public void Text_that_is_not_a_time_of_day_is_not_read(string? text)
    {
        Assert.False(TimeOfDayText.TryParse(text, asEnd: false, out var asStart));
        Assert.False(TimeOfDayText.TryParse(text, asEnd: true, out var asEnd));

        Assert.Equal(0, asStart);
        Assert.Equal(0, asEnd);
    }

    [Fact]
    public void Text_over_the_length_limit_is_not_read_whatever_it_says()
    {
        var atLimit = "6pm".PadLeft(TimeOfDayText.MaxInputLength);
        var overLimit = "6pm".PadLeft(TimeOfDayText.MaxInputLength + 1);

        Assert.True(TimeOfDayText.TryParse(atLimit, asEnd: false, out var read));
        Assert.Equal(1080, read);
        Assert.False(TimeOfDayText.TryParse(overLimit, asEnd: false, out _));
    }

    [Fact]
    public void A_time_off_the_step_is_read_as_it_is()
    {
        // The step is the command's rule, so that it can say what is wrong.
        Assert.True(TimeOfDayText.TryParse("6:15pm", asEnd: false, out var minutes));
        Assert.Equal(1095, minutes);
    }

    [Fact]
    public void Every_24_hour_form_reads_back_to_its_time()
    {
        for (var minutes = 0; minutes <= AvailableHoursLimits.MinutesPerDay; minutes++)
        {
            // 00:00 as an end and 24:00 are the same instant; as a start only 00:00 exists.
            var asEnd = minutes == AvailableHoursLimits.MinutesPerDay;
            Assert.True(TimeOfDayText.TryParse(TimeOfDayText.To24Hour(minutes), asEnd, out var read), $"{minutes}");
            Assert.Equal(minutes, read);
        }
    }

    [Fact]
    public void Every_label_reads_back_to_its_time()
    {
        for (var minutes = 0; minutes <= AvailableHoursLimits.MinutesPerDay; minutes++)
        {
            var asEnd = minutes == AvailableHoursLimits.MinutesPerDay;
            Assert.True(TimeOfDayText.TryParse(TimeOfDayText.Format(minutes), asEnd, out var read), $"{minutes}");
            Assert.Equal(minutes, read);
        }
    }
}
