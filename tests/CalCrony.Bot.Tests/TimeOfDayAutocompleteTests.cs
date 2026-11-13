using CalCrony.Bot;
using CalCrony.Bot.Api;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>The time suggestions of <c>/settings available-hours set</c>: what an empty box
/// offers, how typing narrows it, and that every suggestion sends a value the command reads back
/// to the time its label shows.</summary>
public class TimeOfDayAutocompleteTests
{
    [Theory]
    [InlineData("to", true)]
    [InlineData("from", false)]
    [InlineData("days", false)]
    [InlineData("TO", false)]       // Discord's option names are lower-case, and so is the constant
    [InlineData(null, false)]
    public void Only_the_end_option_is_treated_as_an_end(string? optionName, bool expected)
    {
        Assert.Equal(expected, TimeOfDayAutocompleteHandler.IsEnd(optionName));
    }

    [Fact]
    public void An_empty_start_offers_the_whole_hours_of_the_day()
    {
        var suggestions = TimeOfDayAutocompleteHandler.BuildSuggestions("", asEnd: false);

        Assert.Equal(24, suggestions.Count);
        Assert.Equal("12 AM", suggestions[0].Name);
        Assert.Equal("00:00", suggestions[0].Value);
        Assert.Equal("11 PM", suggestions[^1].Name);
        Assert.Equal("23:00", suggestions[^1].Value);
    }

    [Fact]
    public void An_empty_end_offers_the_whole_hours_up_to_midnight()
    {
        var suggestions = TimeOfDayAutocompleteHandler.BuildSuggestions(null, asEnd: true);

        // An end cannot be the first instant of the day, and can be the last.
        Assert.Equal(24, suggestions.Count);
        Assert.Equal("1 AM", suggestions[0].Name);
        Assert.Equal("midnight", suggestions[^1].Name);
        Assert.Equal("24:00", suggestions[^1].Value);
    }

    [Theory]
    [InlineData("6", "6 AM|6:30 AM|6 PM|6:30 PM")]
    [InlineData("18", "6 PM|6:30 PM")]
    [InlineData("6:3", "6:30 AM|6:30 PM")]
    [InlineData("6p", "6 PM")]
    [InlineData("6 P", "6 PM")]                 // spaces and case aside
    [InlineData("06", "6 AM|6:30 AM")]
    [InlineData("noon", "12 PM")]
    [InlineData("xyz", "")]
    public void Typing_narrows_the_times_to_those_it_can_still_become(string input, string expected)
    {
        var suggestions = TimeOfDayAutocompleteHandler.BuildSuggestions(input, asEnd: false);

        Assert.Equal(expected, string.Join("|", suggestions.Select(s => s.Name)));
    }

    [Fact]
    public void Midnight_is_the_start_of_the_day_for_a_start_and_the_end_of_it_for_an_end()
    {
        var asStart = TimeOfDayAutocompleteHandler.BuildSuggestions("mid", asEnd: false);
        var asEnd = TimeOfDayAutocompleteHandler.BuildSuggestions("mid", asEnd: true);

        Assert.Equal(["12 AM"], asStart.Select(s => s.Name));
        Assert.Equal(["00:00"], asStart.Select(s => s.Value));
        Assert.Equal(["midnight"], asEnd.Select(s => s.Name));
        Assert.Equal(["24:00"], asEnd.Select(s => s.Value));
    }

    [Fact]
    public void A_time_the_text_already_names_comes_first()
    {
        // "12" is noon on the 24-hour clock; the other times it could still become follow in order.
        var suggestions = TimeOfDayAutocompleteHandler.BuildSuggestions("12", asEnd: false);

        Assert.Equal(["12 PM", "12 AM", "12:30 AM", "12:30 PM"], suggestions.Select(s => s.Name));
    }

    [Fact]
    public void A_time_off_the_step_is_not_offered()
    {
        Assert.Empty(TimeOfDayAutocompleteHandler.BuildSuggestions("6:15pm", asEnd: false));
    }

    [Fact]
    public void Text_longer_than_a_time_can_be_is_offered_nothing()
    {
        var atLimit = "6".PadRight(TimeOfDayText.MaxInputLength);
        var overLimit = "6".PadRight(TimeOfDayText.MaxInputLength + 1);

        Assert.NotEmpty(TimeOfDayAutocompleteHandler.BuildSuggestions(atLimit, asEnd: false));
        Assert.Empty(TimeOfDayAutocompleteHandler.BuildSuggestions(overLimit, asEnd: false));
    }

    [Fact]
    public void There_are_never_more_suggestions_than_discord_shows()
    {
        // "1" could still become 1, 10, 11 or 12 o'clock, morning or evening, and 13:00 to 19:30.
        var suggestions = TimeOfDayAutocompleteHandler.BuildSuggestions("1", asEnd: false);

        Assert.Equal(TimeOfDayAutocompleteHandler.MaxSuggestions, suggestions.Count);
        Assert.Equal(25, TimeOfDayAutocompleteHandler.MaxSuggestions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_suggestion_sends_a_value_that_reads_back_to_the_time_its_label_shows(bool asEnd)
    {
        var inputs = new[] { "", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "2:3", "mid", "noon", "p" };

        foreach (var suggestion in inputs.SelectMany(input => TimeOfDayAutocompleteHandler.BuildSuggestions(input, asEnd)))
        {
            Assert.True(TimeOfDayText.TryParse((string)suggestion.Value, asEnd, out var minutes), $"{suggestion.Value}");
            Assert.Equal(suggestion.Name, TimeOfDayText.Format(minutes));

            // On the step, and inside what the option can hold.
            Assert.Equal(0, minutes % AvailableHoursLimits.StepMinutes);
            Assert.InRange(minutes, asEnd ? AvailableHoursLimits.StepMinutes : 0, asEnd ? AvailableHoursLimits.MinutesPerDay : AvailableHoursLimits.MinutesPerDay - AvailableHoursLimits.StepMinutes);
            Assert.InRange(((string)suggestion.Value).Length, 1, TimeOfDayText.MaxInputLength);
        }
    }
}
