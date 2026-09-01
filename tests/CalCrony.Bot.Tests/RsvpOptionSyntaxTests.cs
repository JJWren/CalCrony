using CalCrony.Bot;

namespace CalCrony.Bot.Tests;

public class RsvpOptionSyntaxTests
{
    [Fact]
    public void Parses_emoji_label_and_capacity_per_entry()
    {
        Assert.True(RsvpOptionSyntax.TryParse("⚔️ Raider x10, 🛡️ Standby, ❌ Can't make it", out var specs, out var error));

        Assert.Null(error);
        Assert.Collection(
            specs,
            s => Assert.Equal(("⚔️", "Raider", (int?)10, true), (s.Emote, s.Label, s.Capacity, s.IsAttending)),
            s => Assert.Equal(("🛡️", "Standby", (int?)null, false), (s.Emote, s.Label, s.Capacity, s.IsAttending)),
            s => Assert.Equal(("❌", "Can't make it", (int?)null, false), (s.Emote, s.Label, s.Capacity, s.IsAttending)));
    }

    [Fact]
    public void A_role_mention_gives_that_option_its_own_attendee_role()
    {
        // What a typed @Tank / @Healer / @DPS looks like inside a string command option.
        Assert.True(RsvpOptionSyntax.TryParse(
            "🛡️ Tank x2 <@&11>, 💚 Healer x2 <@&22>, ⚔️ DPS x6 <@&33>", out var specs, out var error));

        Assert.Null(error);
        Assert.Collection(
            specs,
            s => Assert.Equal(("Tank", (int?)2, (long?)11), (s.Label, s.Capacity, s.AttendeeRoleId)),
            s => Assert.Equal(("Healer", (int?)2, (long?)22), (s.Label, s.Capacity, s.AttendeeRoleId)),
            s => Assert.Equal(("DPS", (int?)6, (long?)33), (s.Label, s.Capacity, s.AttendeeRoleId)));
    }

    [Theory]
    [InlineData("🛡️ Tank x2 <@&11>")]   // role after the capacity
    [InlineData("🛡️ Tank <@&11> x2")]   // role before it
    [InlineData("🛡️ <@&11> Tank x2")]   // role in the middle
    public void The_role_mention_is_read_wherever_it_sits_and_never_becomes_label_text(string input)
    {
        Assert.True(RsvpOptionSyntax.TryParse(input, out var specs, out var error));

        Assert.Null(error);
        Assert.Equal(("🛡️", "Tank", (int?)2, (long?)11), (specs[0].Emote, specs[0].Label, specs[0].Capacity, specs[0].AttendeeRoleId));
    }

    [Fact]
    public void Options_without_a_mention_carry_no_role_and_the_star_still_marks_attending()
    {
        Assert.True(RsvpOptionSyntax.TryParse("🛡️ Tank <@&11>, 💚 Healer *", out var specs, out _));

        Assert.Equal(11, specs[0].AttendeeRoleId);
        Assert.False(specs[0].IsAttending);
        Assert.Null(specs[1].AttendeeRoleId);
        Assert.True(specs[1].IsAttending);
        Assert.Equal("Healer", specs[1].Label); // neither marker leaks into the label
    }

    [Fact]
    public void Two_role_mentions_on_one_option_are_rejected()
    {
        Assert.False(RsvpOptionSyntax.TryParse("🛡️ Tank <@&11> <@&22>", out _, out var error));

        Assert.Contains("at most one role", error);
    }

    [Fact]
    public void First_entry_is_attending_unless_a_star_marks_another()
    {
        Assert.True(RsvpOptionSyntax.TryParse("❌ Out, 🍕 In *", out var specs, out _));

        Assert.False(specs[0].IsAttending);
        Assert.True(specs[1].IsAttending);
        Assert.Equal("In", specs[1].Label); // the marker never leaks into the label
    }

    [Theory]
    [InlineData("1️⃣ Choice", "1️⃣", "Choice")] // keycap — ASCII-led, still an emoji
    [InlineData("‼️ Urgent", "‼️", "Urgent")] // BMP punctuation-category emoji
    [InlineData("👨‍👩‍👧 Family", "👨‍👩‍👧", "Family")] // ZWJ sequence
    public void Every_emoji_the_api_accepts_becomes_the_button_emoji(string input, string emote, string label)
    {
        Assert.True(RsvpOptionSyntax.TryParse(input, out var specs, out _));

        Assert.Equal(emote, specs[0].Emote);
        Assert.Equal(label, specs[0].Label);
    }

    [Theory]
    [InlineData("✅ Going x10000", 10000)] // more than four digits is still a capacity
    [InlineData("✅ Going x2147483647", int.MaxValue)]
    public void Large_capacities_parse_instead_of_becoming_label_text(string input, int expected)
    {
        Assert.True(RsvpOptionSyntax.TryParse(input, out var specs, out _));

        Assert.Equal("Going", specs[0].Label);
        Assert.Equal(expected, specs[0].Capacity);
    }

    [Theory]
    [InlineData("✅ Going x99999999999")] // overflows Int32
    [InlineData("✅ Going x0")]
    public void Unusable_capacities_are_errors_not_labels(string input)
    {
        Assert.False(RsvpOptionSyntax.TryParse(input, out _, out var error));

        Assert.Contains("Capacity", error);
    }

    [Fact]
    public void Missing_emoji_gets_a_default_and_accented_labels_stay_labels()
    {
        Assert.True(RsvpOptionSyntax.TryParse("Café night, ✅ Going", out var specs, out _));

        Assert.Equal("🔹", specs[0].Emote);
        Assert.Equal("Café night", specs[0].Label);
        Assert.Equal("✅", specs[1].Emote);
    }

    [Theory]
    [InlineData("⚔️ x10", "label")] // capacity but no label text
    [InlineData("<:raid:123456789> Raider", "Custom server emojis")]
    [InlineData("🍕 A *, 🥗 B *", "one option")]
    [InlineData("a,b,c,d,e,f,g,h,i,j,k", "between 1 and 10")]
    public void Rejects_bad_syntax_with_a_pointer_at_the_problem(string input, string expectedFragment)
    {
        Assert.False(RsvpOptionSyntax.TryParse(input, out _, out var error));
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }
}
