using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

/// <summary>Renders free/busy results as a Discord embed.</summary>
public static class AvailabilityEmbedBuilder
{
    private static readonly Color AvailabilityColor = new(0x57, 0xB9, 0xE2);

    /// <summary>Builds the availability embed: window header plus one status line per member.</summary>
    /// <param name="subject">Display name for the checked group.</param>
    /// <param name="startUtc">Window start (UTC).</param>
    /// <param name="endUtc">Window end (UTC).</param>
    /// <param name="results">The per-user availability results.</param>
    /// <returns>The built embed.</returns>
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

    /// <summary>One member's line: status emoji, mention, and busy blocks when applicable.</summary>
    /// <param name="result">The per-user availability result.</param>
    /// <returns>The formatted status line.</returns>
    private static string FormatLine(UserAvailabilityDto result) => result.Status switch
    {
        CalendarAvailabilityStatus.Free => $"✅ <@{result.UserId}> — free",
        CalendarAvailabilityStatus.Busy => $"🔴 <@{result.UserId}> — busy {FormatBusyBlocks(result.BusyBlocks)}",
        CalendarAvailabilityStatus.ReconnectRequired => $"🔄 <@{result.UserId}> — needs to reconnect (`/calendar connect`)",
        CalendarAvailabilityStatus.Error => $"⚠️ <@{result.UserId}> — couldn't check right now",
        _ => $"⚪ <@{result.UserId}> — not connected",
    };

    /// <summary>Compact busy-block list as Discord timestamps.</summary>
    /// <param name="blocks">The busy intervals to format.</param>
    /// <returns>The formatted block list.</returns>
    private static string FormatBusyBlocks(IReadOnlyList<BusyBlockDto> blocks) =>
        string.Join(", ", blocks.Select(b => $"<t:{b.StartUnix}:t>–<t:{b.EndUnix}:t>"));
}
