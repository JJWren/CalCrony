using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class AvailabilityEmbedBuilderTests
{
    [Fact]
    public void Embed_renders_one_line_per_status()
    {
        var start = new DateTimeOffset(2026, 7, 20, 18, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var results = new List<UserAvailabilityDto>
        {
            new(1, CalendarAvailabilityStatus.Free, []),
            new(2, CalendarAvailabilityStatus.Busy, [new BusyBlockDto(start.AddMinutes(15), start.AddMinutes(45))]),
            new(3, CalendarAvailabilityStatus.NotConnected, []),
            new(4, CalendarAvailabilityStatus.ReconnectRequired, []),
            new(5, CalendarAvailabilityStatus.Error, []),
        };

        var embed = AvailabilityEmbedBuilder.Build("**Raid Night**", start, end, results);

        Assert.Equal("Availability for **Raid Night**", embed.Title);
        Assert.Contains($"<t:{start.ToUnixTimeSeconds()}:F>", embed.Description);
        Assert.Contains("✅ <@1> — free", embed.Description);
        Assert.Contains("🔴 <@2> — busy", embed.Description);
        Assert.Contains($"<t:{results[1].BusyBlocks[0].StartUnix}:t>", embed.Description);
        Assert.Contains("⚪ <@3> — not connected", embed.Description);
        Assert.Contains("🔄 <@4> — needs to reconnect", embed.Description);
        Assert.Contains("⚠️ <@5> — couldn't check right now", embed.Description);
    }

    [Fact]
    public void Multiple_busy_blocks_are_joined()
    {
        var start = DateTimeOffset.UtcNow;
        var result = new UserAvailabilityDto(1, CalendarAvailabilityStatus.Busy,
            [new BusyBlockDto(start, start.AddMinutes(30)), new BusyBlockDto(start.AddHours(2), start.AddHours(3))]);

        var embed = AvailabilityEmbedBuilder.Build("test", start, start.AddHours(4), [result]);

        Assert.Contains(", ", embed.Description);
    }
}
