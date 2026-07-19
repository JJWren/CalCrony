using System.Text;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

/// <summary>Renders events as Discord embeds with RSVP buttons.</summary>
public static class EventEmbedBuilder
{
    private static readonly Color EventColor = new(0x57, 0xB9, 0xE2);

    /// <summary>Builds the event embed: time, recurrence, duration, location, description, and per-option RSVP fields.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The built embed.</returns>
    public static Embed Build(EventDto ev)
    {
        var description = new StringBuilder();
        description.AppendLine($"🗓️ <t:{ev.StartsAtUnix}:F> (<t:{ev.StartsAtUnix}:R>)");
        if (!string.IsNullOrWhiteSpace(ev.RecurrenceSummary))
        {
            description.AppendLine($"🔁 {ev.RecurrenceSummary}");
        }

        if (ev.DurationMinutes is int minutes)
        {
            description.AppendLine($"⏱️ {FormatDuration(minutes)}");
        }

        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            description.AppendLine($"📍 {ev.Location}");
        }

        if (ev.AttendeeRoleId is long roleId)
        {
            description.AppendLine($"🏷️ Going grants <@&{roleId}>");
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            description.AppendLine().AppendLine(ev.Description);
        }

        var builder = new EmbedBuilder()
            .WithTitle(ev.Title)
            .WithColor(EventColor)
            .WithDescription(description.ToString())
            .WithFooter($"Event {ev.Id}");

        if (!string.IsNullOrWhiteSpace(ev.ImageUrl))
        {
            builder.WithImageUrl(ev.ImageUrl);
        }

        foreach (var option in ev.Options)
        {
            var members = ev.Rsvps.Where(r => r.OptionId == option.Id).Select(r => $"<@{r.UserId}>").ToList();
            var capacity = option.Capacity is int cap ? $"/{cap}" : "";
            builder.AddField(
                $"{option.Emote} {option.Label} ({members.Count}{capacity})",
                members.Count == 0 ? "—" : string.Join("\n", members),
                inline: true);
        }

        return builder.Build();
    }

    /// <summary>One RSVP button per option.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The RSVP button row.</returns>
    public static MessageComponent BuildComponents(EventDto ev)
    {
        var row = new ActionRowBuilder();
        foreach (var option in ev.Options)
        {
            row.WithButton(
                option.Label,
                customId: $"rsvp:{ev.Id}:{option.Id}",
                style: ButtonStyle.Secondary,
                emote: new Emoji(option.Emote));
        }

        return new ComponentBuilder().AddRow(row).Build();
    }

    /// <summary>Human-readable duration ("90 min", "2 hr").</summary>
    /// <param name="minutes">The duration in minutes.</param>
    /// <returns>The human-readable duration.</returns>
    private static string FormatDuration(int minutes) =>
        minutes switch
        {
            < 60 => $"{minutes} min",
            _ when minutes % 60 == 0 => $"{minutes / 60} hr",
            _ => $"{minutes / 60} hr {minutes % 60} min",
        };
}
