using System.Text;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

/// <summary>Renders the live-list embed: a persistent upcoming-events summary the bot keeps
/// current. Also owns the one-event line format /list shares.</summary>
public static class LiveListEmbedBuilder
{
    private static readonly Color ListColor = new(0x57, 0xB9, 0xE2);

    /// <summary>Discord caps embed descriptions at 4096 characters; events that don't fit are
    /// dropped from the end (soonest-first ordering keeps the most relevant ones).</summary>
    private const int DescriptionLimit = 4096;

    private const string EmptyMessage = "No upcoming events. Create one with `/create`!";

    /// <summary>Builds the live-list embed from the guild's upcoming events (soonest first).</summary>
    /// <param name="events">The upcoming events to render.</param>
    /// <returns>The built embed.</returns>
    public static Embed Build(IReadOnlyList<EventDto> events) =>
        new EmbedBuilder()
            .WithTitle("📅 Upcoming events")
            .WithColor(ListColor)
            .WithDescription(BuildDescription(events))
            .WithFooter("Updates automatically")
            .Build();

    /// <summary>One event as a list line: title, absolute + relative time, channel, attending
    /// count (seats only — the waitlist isn't coming yet).</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The formatted line.</returns>
    public static string FormatLine(EventDto ev)
    {
        var goingCount = ev.AttendingOption is { } going ? ev.SeatedCount(going.Id) : 0;
        return $"**{ev.Title}** — <t:{ev.StartsAtUnix}:F> (<t:{ev.StartsAtUnix}:R>) in <#{ev.ChannelId}> · {goingCount} going";
    }

    /// <summary>Joins as many event lines as fit under the description cap; empty gets a friendly nudge.</summary>
    /// <param name="events">The upcoming events to render.</param>
    /// <returns>The description text.</returns>
    private static string BuildDescription(IReadOnlyList<EventDto> events)
    {
        if (events.Count == 0)
        {
            return EmptyMessage;
        }

        var description = new StringBuilder();
        foreach (var line in events.Select(FormatLine))
        {
            var needed = line.Length + (description.Length == 0 ? 0 : 1);
            if (description.Length + needed > DescriptionLimit)
            {
                break;
            }

            if (description.Length > 0)
            {
                description.Append('\n');
            }

            description.Append(line);
        }

        return description.ToString();
    }
}
