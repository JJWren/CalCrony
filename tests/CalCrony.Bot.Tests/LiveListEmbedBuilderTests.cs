using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class LiveListEmbedBuilderTests
{
    private static EventDto SampleEvent(string title = "Raid Night", int goingCount = 2)
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        var maybe = new RsvpOptionDto(Guid.NewGuid(), "🤔", "Maybe", 2, null);
        var rsvps = Enumerable.Range(0, goingCount)
            .Select(i => new RsvpDto(100 + i, going.Id))
            .Append(new RsvpDto(999, maybe.Id))
            .ToList();
        return new EventDto(
            Guid.NewGuid(), 1, 2, title, null,
            DateTimeOffset.UtcNow.AddHours(3), "UTC", 60,
            42, null, null, null, EventStatus.Scheduled,
            [going, maybe], rsvps);
    }

    [Fact]
    public void Embed_lists_events_with_time_channel_and_going_count()
    {
        var first = SampleEvent("Raid Night", goingCount: 2);
        var second = SampleEvent("Movie Night", goingCount: 0);

        var embed = LiveListEmbedBuilder.Build([first, second]);

        Assert.Equal("📅 Upcoming events", embed.Title);
        Assert.Contains("**Raid Night**", embed.Description);
        Assert.Contains($"<t:{first.StartsAtUnix}:F>", embed.Description);
        Assert.Contains("<#42>", embed.Description);
        Assert.Contains("2 going", embed.Description);
        Assert.Contains("**Movie Night**", embed.Description);
        Assert.Contains("0 going", embed.Description);
        Assert.Equal("Updates automatically", embed.Footer!.Value.Text);
    }

    [Fact]
    public void Empty_list_shows_the_create_nudge()
    {
        var embed = LiveListEmbedBuilder.Build([]);

        Assert.Contains("No upcoming events", embed.Description);
        Assert.Contains("/create", embed.Description);
    }

    [Fact]
    public void Format_line_counts_only_going_rsvps()
    {
        var ev = SampleEvent(goingCount: 3);

        var line = LiveListEmbedBuilder.FormatLine(ev);

        Assert.Contains("3 going", line);
    }

    [Fact]
    public void Overflowing_content_drops_trailing_events_to_stay_under_discords_cap()
    {
        // 25 max-length titles overflow Discord's 4096-char description budget.
        var events = Enumerable.Range(0, 25)
            .Select(i => SampleEvent(title: $"Event {i:00} " + new string('x', 180)))
            .ToList();

        var description = LiveListEmbedBuilder.Build(events).Description;

        Assert.True(description.Length <= 4096);
        Assert.Contains("**Event 00", description);
        Assert.DoesNotContain("**Event 24", description);
    }
}
