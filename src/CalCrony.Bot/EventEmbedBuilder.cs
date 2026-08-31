using System.Text;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

/// <summary>Renders events as Discord embeds with RSVP buttons.</summary>
public static class EventEmbedBuilder
{
    private static readonly Color EventColor = new(0x57, 0xB9, 0xE2);

    /// <summary>Discord caps buttons at five per action row.</summary>
    private const int ButtonsPerRow = 5;

    /// <summary>Builds the event embed: time, recurrence, duration, location, RSVP cutoff,
    /// description, per-option RSVP fields, and the attending option's waitlist.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">The render instant (defaults to the wall clock) — decides whether the
    /// RSVP cutoff shows as upcoming or closed.</param>
    /// <returns>The built embed.</returns>
    public static Embed Build(EventDto ev, DateTimeOffset? now = null)
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
            description.AppendLine($"🏷️ {ev.AttendingOption?.Label ?? "Going"} grants <@&{roleId}>");
        }

        if (ev.RsvpCloseUnix is long closeUnix)
        {
            // The line reads correctly live either way (<t:R> keeps counting), but a re-render
            // after the cutoff says it plainly.
            description.AppendLine(ev.RsvpsClosed(now ?? DateTimeOffset.UtcNow)
                ? "🔒 RSVPs are closed"
                : $"🔒 RSVPs close <t:{closeUnix}:F> (<t:{closeUnix}:R>)");
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
            // Waitlisted RSVPs sit in their own section, not in the option's seat count.
            var members = ev.Rsvps
                .Where(r => r.OptionId == option.Id && !r.Waitlisted)
                .Select(r => $"<@{r.UserId}>")
                .ToList();
            var capacity = option.Capacity is int cap ? $"/{cap}" : "";
            builder.AddField(
                $"{option.Emote} {option.Label} ({members.Count}{capacity})",
                members.Count == 0 ? "—" : string.Join("\n", members),
                inline: true);

            if (option.Id == ev.AttendingOption?.Id)
            {
                var waitlist = ev.Rsvps
                    .Where(r => r.OptionId == option.Id && r.Waitlisted)
                    .Select(r => $"<@{r.UserId}>")
                    .ToList();
                if (waitlist.Count > 0)
                {
                    builder.AddField($"⏳ Waitlist ({waitlist.Count})", string.Join("\n", waitlist), inline: true);
                }
            }
        }

        return builder.Build();
    }

    /// <summary>One RSVP button per option, five per row, all disabled once RSVPs close.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">The render instant (defaults to the wall clock).</param>
    /// <returns>The RSVP button rows.</returns>
    public static MessageComponent BuildComponents(EventDto ev, DateTimeOffset? now = null)
    {
        var closed = ev.RsvpsClosed(now ?? DateTimeOffset.UtcNow);
        var builder = new ComponentBuilder();
        var row = new ActionRowBuilder();
        foreach (var option in ev.Options)
        {
            if (row.Components.Count == ButtonsPerRow)
            {
                builder.AddRow(row);
                row = new ActionRowBuilder();
            }

            row.WithButton(
                option.Label,
                customId: $"rsvp:{ev.Id}:{option.Id}",
                style: ButtonStyle.Secondary,
                emote: new Emoji(option.Emote),
                disabled: closed);
        }

        return builder.AddRow(row).Build();
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
