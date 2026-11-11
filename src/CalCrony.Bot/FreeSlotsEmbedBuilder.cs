using System.Globalization;
using System.Text;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

/// <summary>Renders a free-slots answer as a Discord embed: the range and what the slots rest on,
/// one line per day, and the members who could not be counted with the reason for each.</summary>
public static class FreeSlotsEmbedBuilder
{
    /// <summary>Slots listed per day before the rest collapse into "+N more". Fourteen days at this
    /// many slots stay inside Discord's description limit.</summary>
    public const int MaxSlotsPerDay = 6;

    private const long SecondsPerDay = 24 * 60 * 60;

    /// <summary>The reasons a member is left out, in the order they are listed. The emoji are the
    /// ones the availability grid uses for the same situations.</summary>
    private static readonly (FreeSlotMemberState State, string Lead)[] NotCountedReasons =
    [
        (FreeSlotMemberState.NotCounted, "⚪ No calendar linked"),
        (FreeSlotMemberState.ReconnectRequired, "🔄 Needs to reconnect with `/calendar connect`"),
        (FreeSlotMemberState.Error, "⚠️ Couldn't check right now"),
    ];

    /// <summary>Builds the free-slots embed.</summary>
    /// <param name="subject">The target's plain name (mention markup does not render in titles).</param>
    /// <param name="minimumMinutes">The shortest slot that was asked for, in minutes.</param>
    /// <param name="response">The API's answer.</param>
    /// <returns>The built embed.</returns>
    public static Embed Build(string subject, int minimumMinutes, FreeSlotsResponse response)
    {
        var lines = new List<string>
        {
            $"🗓️ {FormatRange(response.FirstDay, response.LastDay)} · {minimumMinutes} min or longer",
            BasisSentence(response),
        };

        if (response.CountedMembers > 0)
        {
            lines.Add("");
            lines.AddRange(response.Days.Select(FormatDay));
        }

        var builder = new EmbedBuilder()
            .WithTitle($"Free slots for {subject}")
            .WithColor(AvailabilityEmbedBuilder.AvailabilityColor)
            .WithDescription(string.Join("\n", lines))
            .WithFooter($"Days are in {response.TimeZone} · times show in your local time");

        if (NotCountedField(response.Members) is { } notCounted)
        {
            builder.AddField("Not counted", notCounted);
        }

        return builder.Build();
    }

    /// <summary>The range as literal dates with the year — a bare date already behind us rolls to
    /// next year, and the year is what makes that visible.</summary>
    /// <param name="first">The first day listed.</param>
    /// <param name="last">The last day listed.</param>
    /// <returns>One day, or first – last, with both years when the range crosses New Year.</returns>
    private static string FormatRange(DateOnly first, DateOnly last)
    {
        if (first == last)
        {
            return $"{FormatDate(first)}, {first.Year}";
        }

        return first.Year == last.Year
            ? $"{FormatDate(first)} – {FormatDate(last)}, {last.Year}"
            : $"{FormatDate(first)}, {first.Year} – {FormatDate(last)}, {last.Year}";
    }

    /// <summary>A day as literal text ("Thu Nov 12"). Days are the caller's calendar days, cut by
    /// the API in the zone the footer names, so they cannot be Discord timestamps.</summary>
    /// <param name="date">The day.</param>
    /// <returns>The day's label.</returns>
    private static string FormatDate(DateOnly date) => date.ToString("ddd MMM d", CultureInfo.InvariantCulture);

    /// <summary>One sentence on what the slots rest on. Every counted member is on the default
    /// hours, so the sentence states them once for everyone.</summary>
    /// <param name="response">The API's answer.</param>
    /// <returns>The sentence.</returns>
    private static string BasisSentence(FreeSlotsResponse response)
    {
        var from = TimeOfDayText.Format(AvailableHoursLimits.DefaultStartMinute);
        var to = TimeOfDayText.Format(AvailableHoursLimits.DefaultEndMinute);

        return (response.Members.Count, response.CountedMembers) switch
        {
            (_, 0) =>
                "Nobody could be counted, so there are no slots to list. " +
                "A member is counted once they link a calendar with `/calendar connect`.",
            (1, _) => $"Free on their linked calendar, between {from} and {to} in their timezone.",
            (_, 1) =>
                $"When the one counted member is free on their linked calendar, between {from} and {to} in their timezone.",
            var (_, counted) =>
                $"When all {counted} counted members are free on their linked calendars, " +
                $"each between {from} and {to} in their own timezone.",
        };
    }

    /// <summary>One day's line: the day, then its slots in order, or "nothing free".</summary>
    /// <param name="day">The day and its slots.</param>
    /// <returns>The formatted line.</returns>
    private static string FormatDay(FreeSlotDayDto day)
    {
        var label = $"**{FormatDate(day.Date)}**";
        if (day.Slots.Count == 0)
        {
            return $"{label} · nothing free";
        }

        var shown = string.Join(" · ", day.Slots.Take(MaxSlotsPerDay).Select(FormatSlot));
        return day.Slots.Count > MaxSlotsPerDay
            ? $"{label} · {shown} · +{day.Slots.Count - MaxSlotsPerDay} more"
            : $"{label} · {shown}";
    }

    /// <summary>A slot as two Discord timestamps, which each reader sees in their own local time.
    /// The time-only style is enough while a slot is shorter than a day; a longer one would read
    /// "8:00 AM–8:00 AM", so its end carries the date.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The formatted slot.</returns>
    private static string FormatSlot(FreeSlotDto slot)
    {
        var endStyle = slot.EndUnix - slot.StartUnix >= SecondsPerDay ? 'f' : 't';
        return $"<t:{slot.StartUnix}:t>–<t:{slot.EndUnix}:{endStyle}>";
    }

    /// <summary>The "Not counted" field: one line per reason that has members. The field's
    /// character limit is shared equally between the lines present, so a long list under one
    /// reason cannot push another reason out.</summary>
    /// <param name="members">Every member asked about.</param>
    /// <returns>The field's text, or null when everyone was counted.</returns>
    private static string? NotCountedField(IReadOnlyList<FreeSlotMemberDto> members)
    {
        var groups = NotCountedReasons
            .Select(reason => (
                reason.Lead,
                UserIds: members.Where(member => member.State == reason.State).Select(member => member.UserId).ToList()))
            .Where(group => group.UserIds.Count > 0)
            .ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        var share = (EmbedFieldBuilder.MaxFieldValueLength - (groups.Count - 1)) / groups.Count;
        return string.Join("\n", groups.Select(group => FormatGroup(group.Lead, group.UserIds, share)));
    }

    /// <summary>One reason's line: the reason, then the members as mentions — as many as fit in
    /// the line's share of the field, then "+N more".</summary>
    /// <param name="lead">The reason.</param>
    /// <param name="userIds">The members it applies to.</param>
    /// <param name="budget">The most characters the line may use.</param>
    /// <returns>The formatted line.</returns>
    private static string FormatGroup(string lead, List<long> userIds, int budget)
    {
        var line = new StringBuilder(lead).Append(" — ");
        for (var i = 0; i < userIds.Count; i++)
        {
            var mention = $"{(i == 0 ? "" : ", ")}<@{userIds[i]}>";

            // A mention is added only if, with it, there is still room to say how many are left.
            var left = userIds.Count - i - 1;
            var room = left == 0 ? 0 : $" +{left} more".Length;
            if (line.Length + mention.Length + room > budget)
            {
                return line.Append(i == 0 ? "" : " ").Append($"+{userIds.Count - i} more").ToString();
            }

            line.Append(mention);
        }

        return line.ToString();
    }
}
