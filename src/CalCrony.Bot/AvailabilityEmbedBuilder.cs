using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

public static class AvailabilityEmbedBuilder
{
    private static readonly Color AvailabilityColor = new(0x57, 0xB9, 0xE2);

    public static Embed Build(string subject, DateTimeOffset startUtc, DateTimeOffset endUtc, IReadOnlyList<UserAvailabilityDto> results)
    {
        var startUnix = startUtc.ToUnixTimeSeconds();
        var endUnix = endUtc.ToUnixTimeSeconds();
        var lines = results.Select(FormatLine);

        return new EmbedBuilder()
            .WithTitle($"Availability for {subject}")
            .WithColor(AvailabilityColor)
            .WithDescription($"🗓️ <t:{startUnix}:F> – <t:{endUnix}:t>\n\n{string.Join("\n", lines)}")
            .Build();
    }

    private static string FormatLine(UserAvailabilityDto result) => result.Status switch
    {
        CalendarAvailabilityStatus.Free => $"✅ <@{result.UserId}> — free",
        CalendarAvailabilityStatus.Busy => $"🔴 <@{result.UserId}> — busy {FormatBusyBlocks(result.BusyBlocks)}",
        CalendarAvailabilityStatus.ReconnectRequired => $"🔄 <@{result.UserId}> — needs to reconnect (`/calendar connect`)",
        CalendarAvailabilityStatus.Error => $"⚠️ <@{result.UserId}> — couldn't check right now",
        _ => $"⚪ <@{result.UserId}> — not connected",
    };

    private static string FormatBusyBlocks(IReadOnlyList<BusyBlockDto> blocks) =>
        string.Join(", ", blocks.Select(b => $"<t:{b.StartUnix}:t>–<t:{b.EndUnix}:t>"));
}
