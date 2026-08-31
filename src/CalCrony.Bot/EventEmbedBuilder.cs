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

    /// <summary>Discord's cap on one embed field value.</summary>
    private const int FieldValueLimit = 1024;

    /// <summary>Discord's cap on an embed title.</summary>
    private const int TitleLimit = 256;

    /// <summary>Discord's cap on an embed description.</summary>
    private const int DescriptionLimit = 4096;

    /// <summary>Working cap for the whole embed — under Discord's 6000-char total with margin.</summary>
    private const int EmbedTotalBudget = 5800;

    /// <summary>The floor each member list keeps: room for the "+N more" marker alone.</summary>
    private const int MinListBudget = 12;

    /// <summary>Room reserved while filling a list so the omitted-count marker always fits.</summary>
    private const int OmittedMarkerReserve = 16;

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

        // Everything is sized against Discord's caps — 256 title, 4096 description, 1024 per
        // field value, 6000 for the whole embed — so no valid event can produce an embed Discord
        // rejects. Field names keep the FULL counts; the description (the one fixed part that
        // can be large: a 4096-char event description plus meta lines) absorbs the squeeze
        // first, then the member lists share what remains. Waitlisted RSVPs sit in their own
        // section, not in the option's seat count.
        var seated = ev.Options.ToDictionary(
            o => o.Id,
            o => ev.Rsvps.Where(r => r.OptionId == o.Id && !r.Waitlisted).Select(r => $"<@{r.UserId}>").ToList());
        var waitlist = ev.AttendingOption is { } attending
            ? ev.Rsvps.Where(r => r.OptionId == attending.Id && r.Waitlisted).Select(r => $"<@{r.UserId}>").ToList()
            : [];
        var names = ev.Options.ToDictionary(
            o => o.Id,
            o => $"{o.Emote} {o.Label} ({seated[o.Id].Count}{(o.Capacity is int cap ? $"/{cap}" : "")})");
        var waitlistName = $"⏳ Waitlist ({waitlist.Count})";
        var title = Truncate(ev.Title, TitleLimit);
        var footer = $"Event {ev.Id}";
        var namesLength = names.Values.Sum(n => n.Length) + (waitlist.Count > 0 ? waitlistName.Length : 0);
        var listCount = ev.Options.Count + (waitlist.Count > 0 ? 1 : 0);
        // Reserve MinListBudget per list up front, so whatever the description leaves is by
        // construction enough for every list's marker: fixedLength <= EmbedTotalBudget -
        // listCount * MinListBudget, hence remaining / listCount >= MinListBudget, hence the
        // final embed (fixed + listCount * listBudget) <= EmbedTotalBudget < 6000. No lower
        // clamp is needed — or wanted, since one could only ever push past the cap.
        var reservedForLists = listCount * MinListBudget;
        var descriptionText = Truncate(
            description.ToString(),
            Math.Min(DescriptionLimit, EmbedTotalBudget - title.Length - footer.Length - namesLength - reservedForLists));
        var fixedLength = title.Length + descriptionText.Length + footer.Length + namesLength;
        var remaining = EmbedTotalBudget - fixedLength;
        var listBudget = Math.Min(FieldValueLimit, remaining / Math.Max(1, listCount));

        var builder = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(EventColor)
            .WithDescription(descriptionText)
            .WithFooter(footer);

        if (!string.IsNullOrWhiteSpace(ev.ImageUrl))
        {
            builder.WithImageUrl(ev.ImageUrl);
        }

        foreach (var option in ev.Options)
        {
            builder.AddField(names[option.Id], BoundedMemberList(seated[option.Id], listBudget), inline: true);

            if (option.Id == ev.AttendingOption?.Id && waitlist.Count > 0)
            {
                builder.AddField(waitlistName, BoundedMemberList(waitlist, listBudget), inline: true);
            }
        }

        return builder.Build();
    }

    /// <summary>Cuts text to <paramref name="budget"/> chars with a trailing ellipsis, never
    /// splitting a surrogate pair.</summary>
    /// <param name="text">The text to bound.</param>
    /// <param name="budget">The character budget (values below 1 yield an empty string).</param>
    /// <returns>The bounded text.</returns>
    private static string Truncate(string text, int budget)
    {
        if (text.Length <= budget)
        {
            return text;
        }

        if (budget < 1)
        {
            return "";
        }

        var cut = budget - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }

    /// <summary>Renders a mention list within <paramref name="budget"/> chars: the entries that
    /// fit, then "+N more" for the rest. An empty list renders the em-dash placeholder.</summary>
    /// <param name="members">The rendered mentions, in display order.</param>
    /// <param name="budget">The character budget for this list.</param>
    /// <returns>The bounded field value.</returns>
    private static string BoundedMemberList(List<string> members, int budget)
    {
        if (members.Count == 0)
        {
            return "—";
        }

        var text = new StringBuilder();
        var shown = 0;
        foreach (var member in members)
        {
            var length = member.Length + (shown == 0 ? 0 : 1);
            var reserve = shown + 1 < members.Count ? OmittedMarkerReserve : 0;
            if (text.Length + length + reserve > budget)
            {
                break;
            }

            if (shown > 0)
            {
                text.Append('\n');
            }

            text.Append(member);
            shown++;
        }

        if (shown < members.Count)
        {
            if (shown > 0)
            {
                text.Append('\n');
            }

            text.Append($"+{members.Count - shown} more");
        }

        return text.ToString();
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
