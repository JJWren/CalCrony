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
    public void First_entry_is_attending_unless_a_star_marks_another()
    {
        Assert.True(RsvpOptionSyntax.TryParse("❌ Out, 🍕 In *", out var specs, out _));

        Assert.False(specs[0].IsAttending);
        Assert.True(specs[1].IsAttending);
        Assert.Equal("In", specs[1].Label); // the marker never leaks into the label
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
