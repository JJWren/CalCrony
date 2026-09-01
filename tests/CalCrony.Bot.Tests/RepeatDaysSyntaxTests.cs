using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class RepeatDaysSyntaxTests
{
    [Theory]
    [InlineData("tue,thu", RecurrenceDays.Tuesday | RecurrenceDays.Thursday)]
    [InlineData("Tuesday and Thursday", RecurrenceDays.Tuesday | RecurrenceDays.Thursday)]
    [InlineData("mon/wed/fri", RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday)]
    [InlineData("Mon + Weds + Fri.", RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday)]
    [InlineData("thur, tues", RecurrenceDays.Tuesday | RecurrenceDays.Thursday)]
    [InlineData("sa su", RecurrenceDays.Saturday | RecurrenceDays.Sunday)]
    [InlineData("weekdays", RecurrenceDays.Weekdays)]
    [InlineData("Weekday", RecurrenceDays.Weekdays)]
    [InlineData("weekends", RecurrenceDays.Saturday | RecurrenceDays.Sunday)]
    [InlineData("weekdays, sat", RecurrenceDays.Weekdays | RecurrenceDays.Saturday)]
    [InlineData("tue, tue", RecurrenceDays.Tuesday)] // duplicates collapse
    public void Parses_names_abbreviations_separators_and_presets(string input, RecurrenceDays expected)
    {
        Assert.True(RepeatDaysSyntax.TryParse(input, out var days, out var error));

        Assert.Null(error);
        Assert.Equal(expected, days);
    }

    [Theory]
    [InlineData("t", "could be more than one day")] // Tuesday or Thursday
    [InlineData("s", "could be more than one day")] // Saturday or Sunday
    [InlineData("m", "isn't a weekday")] // one letter is never enough
    [InlineData("funday", "isn't a weekday")]
    [InlineData("tue, thx", "isn't a weekday")]
    [InlineData("   ", "at least one day")]
    [InlineData("none", "isn't a weekday")] // only edit commands accept "none"
    public void Rejects_bad_input_with_a_pointer_at_the_problem(string input, string expectedFragment)
    {
        Assert.False(RepeatDaysSyntax.TryParse(input, out _, out var error));

        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void None_clears_the_set_only_when_allowed()
    {
        Assert.True(RepeatDaysSyntax.TryParse("none", out var days, out var error, allowNone: true));
        Assert.Null(error);
        Assert.Equal(RecurrenceDays.None, days);

        // "none" mixed with days is a contradiction, not a clear.
        Assert.False(RepeatDaysSyntax.TryParse("none, tue", out _, out var mixed, allowNone: true));
        Assert.Contains("isn't a weekday", mixed);
    }
}
