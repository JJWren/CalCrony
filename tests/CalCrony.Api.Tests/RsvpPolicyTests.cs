using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>Pure RSVP v1 rules: cutoff-text parsing (relative vs absolute), emote validation,
/// and the template-spec serialization bound.</summary>
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

    // ---------- Emote validation ----------

    [Theory]
    [InlineData("✅")] // BMP symbol
    [InlineData("🤔")] // outside the BMP
    [InlineData("⚔️")] // BMP symbol + variation selector
    [InlineData("1️⃣")] // keycap sequence
    [InlineData("🇺🇸")] // regional-indicator flag
    [InlineData("👨‍👩‍👧")] // ZWJ family
    [InlineData("🏳️‍🌈")] // ZWJ flag
    [InlineData("‼️")] // BMP straggler outside the symbol categories
    public void Real_emojis_pass_emote_validation(string emote) =>
        Assert.True(EmoteText.IsLikelyEmoji(emote));

    [Theory]
    [InlineData("abc")] // plain text — the Discord-breaking case
    [InlineData("a")]
    [InlineData("123")] // digits without the keycap combiner
    [InlineData(":smile:")] // shortcode, not an emoji
    [InlineData("✅✅")] // two emojis — buttons take exactly one
    [InlineData("✅ ok")]
    public void Text_that_is_not_one_emoji_fails_emote_validation(string emote) =>
        Assert.False(EmoteText.IsLikelyEmoji(emote));

    [Fact]
    public void Non_emoji_emotes_are_rejected_when_building_options()
    {
        var built = RsvpPolicy.TryBuildOptions([new RsvpOptionSpec("abc", "Going")], null, out var error);

        Assert.Null(built);
        Assert.Contains("emoji", error);
    }

    [Fact]
    public void Custom_server_emotes_are_rejected_with_the_bot_wording()
    {
        var built = RsvpPolicy.TryBuildOptions([new RsvpOptionSpec("<:pepe:1234>", "Going")], null, out var error);

        Assert.Null(built);
        Assert.Contains("Custom server emojis", error);
    }

    [Fact]
    public void Control_characters_in_labels_are_rejected()
    {
        var built = RsvpPolicy.TryBuildOptions([new RsvpOptionSpec("✅", "Go\u0001ing")], null, out var error);

        Assert.Null(built);
        Assert.Contains("control characters", error);
    }

    // ---------- Template spec serialization ----------

    [Fact]
    public void Spec_storage_json_keeps_bmp_unicode_raw_and_round_trips()
    {
        // With the default encoder every ü would become a six-char escape; the storage encoder
        // keeps BMP text raw so ordinary non-ASCII labels stay compact.
        var options = Enumerable.Range(0, 10).Select(i => new RsvpOption
        {
            Id = Guid.NewGuid(),
            Emote = "🧙",
            Label = new string('ü', 63) + (char)('0' + i),
            SortOrder = i,
            Capacity = 9999,
            IsAttending = i == 0,
        }).ToList();

        var json = RsvpPolicy.SerializeSpecs(options);

        Assert.Contains("ü", json);
        Assert.Equal(10, RsvpPolicy.OptionsFromTemplate(json).Count); // round-trips
    }

    [Fact]
    public void Spec_storage_json_fits_the_column_even_at_the_escaping_worst_case()
    {
        // Astral-plane chars always escape six-to-one per UTF-16 unit under System.Text.Json's
        // encoders, so the worst case is 10 options whose 64-unit emotes AND labels are all
        // emoji. The column must hold that even though option validation would reject it today.
        var emoji64Units = string.Concat(Enumerable.Repeat("🧙", 32));
        var options = Enumerable.Range(0, 10).Select(i => new RsvpOption
        {
            Id = Guid.NewGuid(),
            Emote = emoji64Units,
            Label = emoji64Units,
            SortOrder = i,
            Capacity = 9999,
            IsAttending = i == 0,
        }).ToList();

        var json = RsvpPolicy.SerializeSpecs(options);

        Assert.InRange(json.Length, 1, 10240);
    }
}
