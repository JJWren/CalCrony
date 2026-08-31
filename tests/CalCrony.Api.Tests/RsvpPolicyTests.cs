using CalCrony.Api.Services;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>Pure RSVP v1 rules: cutoff-text parsing (relative vs absolute).</summary>
public class RsvpPolicyTests
{
    private static readonly NaturalDateTimeParser Parser = new(SystemClock.Instance);

    [Theory]
    [InlineData("2h before", 120)]
    [InlineData("90m", 90)]
    [InlineData("45 minutes before start", 45)]
    [InlineData("1 day before", 1440)]
    [InlineData("30 MIN", 30)]
    public void Relative_text_becomes_minutes_before_start(string text, int expectedMinutes)
    {
        Assert.True(RsvpPolicy.TryParseClose(
            text, DateTimeZone.Utc, Parser, out var minutes, out var closesAt, out var error));

        Assert.Null(error);
        Assert.Null(closesAt);
        Assert.Equal(expectedMinutes, minutes);
    }

    [Fact]
    public void Absolute_text_parses_as_a_future_instant()
    {
        Assert.True(RsvpPolicy.TryParseClose(
            "in 3 hours", DateTimeZone.Utc, Parser, out var minutes, out var closesAt, out var error));

        Assert.Null(error);
        Assert.Null(minutes);
        Assert.True(closesAt > SystemClock.Instance.GetCurrentInstant());
    }

    [Theory]
    [InlineData("flurble wumpus")] // unparseable either way
    [InlineData("99999999 minutes before")] // relative-shaped but out of range → absolute parse also fails
    public void Unreadable_cutoffs_fail_with_a_user_facing_error(string text)
    {
        Assert.False(RsvpPolicy.TryParseClose(
            text, DateTimeZone.Utc, Parser, out _, out _, out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void Relative_cutoffs_beyond_four_weeks_are_rejected()
    {
        Assert.False(RsvpPolicy.TryParseClose(
            "50000 minutes before", DateTimeZone.Utc, Parser, out _, out _, out var error));

        Assert.Contains("4 weeks", error);
    }
}
