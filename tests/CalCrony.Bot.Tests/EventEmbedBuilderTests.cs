using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class EventEmbedBuilderTests
{
    private static EventDto SampleEvent()
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        var notGoing = new RsvpOptionDto(Guid.NewGuid(), "❌", "Not going", 1, null);
        var maybe = new RsvpOptionDto(Guid.NewGuid(), "🤔", "Maybe", 2, 5);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Raid Night", "Bring snacks",
            DateTimeOffset.UtcNow.AddHours(3), "America/Chicago", 90,
            3, null, "Voice chat", null, EventStatus.Scheduled,
            [going, notGoing, maybe],
            [new RsvpDto(42, going.Id), new RsvpDto(43, going.Id)]);
    }

    [Fact]
    public void Embed_contains_title_time_and_option_fields()
    {
        var ev = SampleEvent();

        var embed = EventEmbedBuilder.Build(ev);

        Assert.Equal("Raid Night", embed.Title);
        Assert.Contains($"<t:{ev.StartsAtUnix}:F>", embed.Description);
        Assert.Equal(3, embed.Fields.Length);
        Assert.Contains("(2)", embed.Fields[0].Name);
        Assert.Contains("<@42>", embed.Fields[0].Value);
        Assert.Contains("(0/5)", embed.Fields[2].Name);
        Assert.Equal("—", embed.Fields[2].Value);
    }

    [Fact]
    public void Components_have_one_button_per_option()
    {
        var ev = SampleEvent();

        var components = EventEmbedBuilder.BuildComponents(ev);

        var row = Assert.Single(components.Components.OfType<Discord.ActionRowComponent>());
        Assert.Equal(3, row.Components.Count);
    }
}
