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
            lines.AddRange(FormatDays(response.Days));
            if (NothingFreeHint(minimumMinutes, response.Days) is { } hint)
            {
                lines.Add("");
                lines.Add(hint);
            }
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
    /// hours, so the sentence states them once for everyone — and says whose timezone they are
    /// read in when a member has set none, since that moves every time in the list.</summary>
    /// <param name="response">The API's answer.</param>
    /// <returns>The sentence.</returns>
    private static string BasisSentence(FreeSlotsResponse response)
    {
        var from = TimeOfDayText.Format(AvailableHoursLimits.DefaultStartMinute);
        var to = TimeOfDayText.Format(AvailableHoursLimits.DefaultEndMinute);

        return (response.Members.Count, response.CountedMembers) switch
        {
            (_, 0) => $"Nobody could be counted, so there are no slots to list. {WhyNobodyWasCounted(response.Members)}",
            (1, _) =>
                $"Free on their linked calendar, between {from} and {to} in their timezone " +
                "(the server's if they haven't set one).",
            (_, 1) =>
                $"When the one counted member is free on their linked calendar, between {from} and {to} " +
                "in their timezone (the server's if they haven't set one).",
            var (_, counted) =>
                $"When all {counted} counted members are free on their linked calendars, " +
                $"each between {from} and {to} in their own timezone (the server's for anyone who hasn't set one).",
        };
    }

    /// <summary>What to say after "nobody could be counted". Linking a calendar is the remedy only
    /// when no calendar is the one reason; a member whose linked calendar needs reconnecting, or
    /// could not be read just now, is not told to link one.</summary>
    /// <param name="members">Every member asked about; none of them counted.</param>
    /// <returns>The sentence.</returns>
    private static string WhyNobodyWasCounted(IReadOnlyList<FreeSlotMemberDto> members) =>
        members.All(member => member.State == FreeSlotMemberState.NotCounted)
            ? "A member is counted once they link a calendar with `/calendar connect`."
            : "The reasons are listed below.";

    /// <summary>One line per day, in order. A slot is listed under the day it starts on, so a day
    /// with no slot of its own can still begin inside one; its line then says how long it stays
    /// free and which day that slot is listed under.</summary>
    /// <param name="days">The requested days.</param>
    /// <returns>The formatted lines.</returns>
    private static IEnumerable<string> FormatDays(IReadOnlyList<FreeSlotDayDto> days)
    {
        FreeSlotDayDto? lastWithSlots = null;
        foreach (var day in days)
        {
            yield return FormatDay(day, lastWithSlots);
            if (day.Slots.Count > 0)
            {
                lastWithSlots = day;
            }
        }
    }

    /// <summary>One day's line: the day, then its slots in order. A day without slots says
    /// "nothing free" — unless it begins inside a slot from an earlier day, when it says until
    /// when it is free.</summary>
    /// <param name="day">The day and its slots.</param>
    /// <param name="lastWithSlots">The nearest earlier day that lists a slot, if any.</param>
    /// <returns>The formatted line.</returns>
    private static string FormatDay(FreeSlotDayDto day, FreeSlotDayDto? lastWithSlots)
    {
        var label = $"**{FormatDate(day.Date)}**";
        if (day.Slots.Count == 0)
        {
            return day.OpenUntilUtc is { } openUntil
                ? $"{label} · {FormatOpenUntil(openUntil, lastWithSlots)}"
                : $"{label} · nothing free";
        }

        var shown = string.Join(" · ", day.Slots.Take(MaxSlotsPerDay).Select(FormatSlot));
        return day.Slots.Count > MaxSlotsPerDay
            ? $"{label} · {shown} · +{day.Slots.Count - MaxSlotsPerDay} more"
            : $"{label} · {shown}";
    }

    /// <summary>What a day says when it lists no slot but begins inside one: free until that slot
    /// ends, and the day the slot started on. Slots never overlap, so the slot still running when
    /// the day begins is the last one of the nearest earlier day that lists any.</summary>
    /// <param name="openUntil">When the slot the day begins inside ends.</param>
    /// <param name="startedOn">The day that slot is listed under, if the answer names one.</param>
    /// <returns>The formatted text.</returns>
    private static string FormatOpenUntil(DateTimeOffset openUntil, FreeSlotDayDto? startedOn)
    {
        if (startedOn is null)
        {
            return $"free until <t:{openUntil.ToUnixTimeSeconds()}:t>";
        }

        var started = startedOn.Date.ToString("ddd", CultureInfo.InvariantCulture);
        return $"free until <t:{openUntil.ToUnixTimeSeconds()}:{EndStyle(startedOn.Slots[^1])}> (started {started})";
    }

    /// <summary>A slot as two Discord timestamps, which each reader sees in their own local time.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The formatted slot.</returns>
    private static string FormatSlot(FreeSlotDto slot) => $"<t:{slot.StartUnix}:t>–<t:{slot.EndUnix}:{EndStyle(slot)}>";

    /// <summary>The timestamp style for a slot's end. The time-only style is enough while a slot
    /// is shorter than a day; a longer one would read "8:00 AM–8:00 AM", so its end carries the
    /// date.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>Discord's style letter: time only, or date and time.</returns>
    private static char EndStyle(FreeSlotDto slot) => slot.EndUnix - slot.StartUnix >= SecondsPerDay ? 'f' : 't';

    /// <summary>A closing line for an answer in which no day has anything free, when a shorter
    /// minimum could still find something — a minimum longer than anyone's hours can never match,
    /// and a run of "nothing free" does not say so.</summary>
    /// <param name="minimumMinutes">The shortest slot that was asked for, in minutes.</param>
    /// <param name="days">The requested days.</param>
    /// <returns>The line, or null when something is free or the minimum is already the smallest.</returns>
    private static string? NothingFreeHint(int minimumMinutes, IReadOnlyList<FreeSlotDayDto> days) =>
        minimumMinutes > FreeSlotLimits.MinMinimumMinutes && days.Count > 0 && days.All(day => day.Slots.Count == 0)
            ? $"Nothing {minimumMinutes} min or longer is free in this range — a shorter `duration` may find something."
            : null;

    /// <summary>The "Not counted" field: one line per reason that has members. When the field
    /// holds every line in full, everyone is named. When it does not, the lines share it: a line
    /// keeps its full length while that is within an equal share of the room left, and the longer
    /// ones split the rest — so a short list is never cut for room it does not use, and a long
    /// one cannot push another reason out.</summary>
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

        var full = groups.Select(group => FormatGroup(group.Lead, group.UserIds, int.MaxValue)).ToList();
        var room = EmbedFieldBuilder.MaxFieldValueLength - (groups.Count - 1);     // less the line breaks
        if (full.Sum(line => line.Length) <= room)
        {
            return string.Join("\n", full);
        }

        // Shortest line first: each takes what it needs, up to an equal share of what is left.
        var budgets = new int[groups.Count];
        var waiting = groups.Count;
        foreach (var index in Enumerable.Range(0, groups.Count).OrderBy(i => full[i].Length))
        {
            budgets[index] = Math.Min(full[index].Length, room / waiting);
            room -= budgets[index];
            waiting--;
        }

        return string.Join("\n", groups.Select((group, index) =>
            full[index].Length <= budgets[index] ? full[index] : FormatGroup(group.Lead, group.UserIds, budgets[index])));
    }

    /// <summary>One reason's line: the reason, then the members as mentions — as many as fit in
    /// the line's budget, then "+N more".</summary>
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
            var reserved = left == 0 ? 0 : $" +{left} more".Length;
            if (line.Length + mention.Length + reserved > budget)
            {
                return line.Append(i == 0 ? "" : " ").Append($"+{userIds.Count - i} more").ToString();
            }

            line.Append(mention);
        }

        return line.ToString();
    }
}
