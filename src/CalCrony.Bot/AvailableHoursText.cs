using System.Diagnostics.CodeAnalysis;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord.Interactions;

namespace CalCrony.Bot;

/// <summary>The days a <c>/settings available-hours</c> command applies to.</summary>
public enum AvailableHoursDays
{
    /// <summary>All seven days.</summary>
    [ChoiceDisplay("every day")] EveryDay,

    /// <summary>Monday to Friday.</summary>
    [ChoiceDisplay("weekdays")] Weekdays,

    /// <summary>Saturday and Sunday.</summary>
    [ChoiceDisplay("weekends")] Weekends,

    /// <summary>Monday alone.</summary>
    [ChoiceDisplay("Monday")] Monday,

    /// <summary>Tuesday alone.</summary>
    [ChoiceDisplay("Tuesday")] Tuesday,

    /// <summary>Wednesday alone.</summary>
    [ChoiceDisplay("Wednesday")] Wednesday,

    /// <summary>Thursday alone.</summary>
    [ChoiceDisplay("Thursday")] Thursday,

    /// <summary>Friday alone.</summary>
    [ChoiceDisplay("Friday")] Friday,

    /// <summary>Saturday alone.</summary>
    [ChoiceDisplay("Saturday")] Saturday,

    /// <summary>Sunday alone.</summary>
    [ChoiceDisplay("Sunday")] Sunday,
}

/// <summary>A member's week of available hours in the bot: changing the days a command names,
/// putting the week into one line, and the replies of the hours commands. Pure, so every rule and
/// every sentence can be tested without Discord; the module only reads, writes and replies.</summary>
public static class AvailableHoursText
{
    /// <summary>What is said wherever hours are saved or shown: saving them lets other members
    /// list one's free slots by name. One wording, so the surfaces cannot drift.</summary>
    public const string SavedNotice =
        "Saved hours can be looked up by name: members of the servers you share with CalCrony can list your free slots " +
        "with `/availability slots`, which shows roughly the hours you keep and, if your calendar is linked, " +
        "when you're busy inside them. `/settings available-hours reset` takes that back.";

    /// <summary>The reply to <c>off</c> when nothing is saved: there is no saved day to switch
    /// off, and a command called "off" is not what should start a member's hours being listed.</summary>
    public const string NothingSavedToSwitchOff =
        "You haven't saved any hours yet, so there is nothing to switch off. Start with `/settings available-hours set`.";

    /// <summary>The reply to an <c>off</c> that would leave no day: a week with no day cannot be saved.</summary>
    public const string WouldLeaveNoDay =
        "That would leave no day available. To go back to the default hours, use `/settings available-hours reset`.";

    /// <summary>The reply when a typed time is off the step between selectable times.</summary>
    public static string OffTheStep { get; } =
        $"Times are in {AvailableHoursLimits.StepMinutes}-minute steps — 6 PM or 6:30 PM, for example.";

    /// <summary>The reply when the start is not before the end.</summary>
    public const string StartNotBeforeEnd =
        "The start has to be before the end. Hours can't run past midnight; for a late night, end at midnight.";

    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static readonly string[] ShortNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>The default hours as a range, from <see cref="AvailableHoursLimits"/>: "8 AM–11 PM".</summary>
    public static string DefaultRange { get; } =
        FormatRange(AvailableHoursLimits.DefaultStartMinute, AvailableHoursLimits.DefaultEndMinute);

    /// <summary>The weekdays a choice names, Monday first.</summary>
    /// <param name="choice">The choice.</param>
    /// <returns>Its weekdays.</returns>
    public static IReadOnlyList<DayOfWeek> DaysOf(AvailableHoursDays choice) => choice switch
    {
        AvailableHoursDays.EveryDay => MondayFirst,
        AvailableHoursDays.Weekdays => MondayFirst[..5],
        AvailableHoursDays.Weekends => MondayFirst[5..],
        AvailableHoursDays.Monday => [DayOfWeek.Monday],
        AvailableHoursDays.Tuesday => [DayOfWeek.Tuesday],
        AvailableHoursDays.Wednesday => [DayOfWeek.Wednesday],
        AvailableHoursDays.Thursday => [DayOfWeek.Thursday],
        AvailableHoursDays.Friday => [DayOfWeek.Friday],
        AvailableHoursDays.Saturday => [DayOfWeek.Saturday],
        AvailableHoursDays.Sunday => [DayOfWeek.Sunday],
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Not one of the choices."),
    };

    /// <summary>Reads the two typed times of <c>set</c> into a range, or gives the reply that says
    /// what is wrong with them: a time that cannot be read, one off the step, or a start that is
    /// not before its end.</summary>
    /// <param name="from">The typed start.</param>
    /// <param name="to">The typed end.</param>
    /// <param name="range">The range in minutes from midnight, when both times are usable.</param>
    /// <param name="refusal">The reply to give instead, when they are not.</param>
    /// <returns>Whether the range can be saved.</returns>
    public static bool TryReadRange(
        string? from, string? to, out (int StartMinute, int EndMinute) range, [NotNullWhen(false)] out string? refusal)
    {
        range = default;
        if (!TimeOfDayText.TryParse(from, asEnd: false, out var start))
        {
            refusal = CouldNotRead(TimeOfDayAutocompleteHandler.StartOptionName);
            return false;
        }

        if (!TimeOfDayText.TryParse(to, asEnd: true, out var end))
        {
            refusal = CouldNotRead(TimeOfDayAutocompleteHandler.EndOptionName);
            return false;
        }

        if (start % AvailableHoursLimits.StepMinutes != 0 || end % AvailableHoursLimits.StepMinutes != 0)
        {
            refusal = OffTheStep;
            return false;
        }

        if (start >= end)
        {
            refusal = StartNotBeforeEnd;
            return false;
        }

        range = (start, end);
        refusal = null;
        return true;
    }

    /// <summary>The week <c>set</c> saves: the member's saved week with the named days given the
    /// range. With nothing saved it starts from an empty week, so days the member does not name
    /// are not available — the week the API sends along with "not set" is the default one, there
    /// to be shown, and is not what a first save builds on.</summary>
    /// <param name="current">The member's hours as the API returned them.</param>
    /// <param name="choice">The days the command names.</param>
    /// <param name="range">The range to give them.</param>
    /// <returns>The week to save, Monday first.</returns>
    public static IReadOnlyList<AvailableHoursDayDto> Set(
        AvailableHoursDto current, AvailableHoursDays choice, (int StartMinute, int EndMinute) range) =>
        Apply(current.IsSet ? current.Days : [], choice, range);

    /// <summary>The week <c>off</c> saves, or the reply that refuses it. With nothing saved there
    /// is no saved day to switch off — and saving what is left of the default week would start
    /// the member's hours being listed by name, which a command called "off" must not do. A week
    /// with no day left cannot be saved either.</summary>
    /// <param name="current">The member's hours as the API returned them.</param>
    /// <param name="choice">The days the command names.</param>
    /// <param name="week">The week to save, Monday first, when the command goes ahead.</param>
    /// <param name="refusal">The reply to give instead, when it does not.</param>
    /// <returns>Whether there is a week to save.</returns>
    public static bool TryOff(
        AvailableHoursDto current,
        AvailableHoursDays choice,
        [NotNullWhen(true)] out IReadOnlyList<AvailableHoursDayDto>? week,
        [NotNullWhen(false)] out string? refusal)
    {
        week = null;
        if (!current.IsSet)
        {
            refusal = NothingSavedToSwitchOff;
            return false;
        }

        var left = Apply(current.Days, choice, null);
        if (left.Count == 0)
        {
            refusal = WouldLeaveNoDay;
            return false;
        }

        week = left;
        refusal = null;
        return true;
    }

    /// <summary>The week after a command: the named days get the range, or lose theirs when the
    /// range is null; every other day stays as it was.</summary>
    /// <param name="week">The week before — the saved one, or none for a first save.</param>
    /// <param name="choice">The days the command names.</param>
    /// <param name="range">The range to give them, or null to mark them as not available.</param>
    /// <returns>The new week, Monday first; empty when no day is left.</returns>
    public static IReadOnlyList<AvailableHoursDayDto> Apply(
        IReadOnlyList<AvailableHoursDayDto> week, AvailableHoursDays choice, (int StartMinute, int EndMinute)? range)
    {
        var named = DaysOf(choice);
        var days = week.Where(day => !named.Contains(day.Day)).ToList();
        if (range is { } set)
        {
            days.AddRange(named.Select(day => new AvailableHoursDayDto(day, set.StartMinute, set.EndMinute)));
        }

        return [.. days.OrderBy(day => Array.IndexOf(MondayFirst, day.Day))];
    }

    /// <summary>A week on one line. Days that share a range form a group, and groups come in the
    /// order of their first day, Monday first; three or more days in a row are written as a span.
    /// Days with no range come last, marked off. "Mon–Fri 6 PM–11 PM · Sat, Sun off".</summary>
    /// <param name="week">The week's available days.</param>
    /// <returns>The line.</returns>
    public static string Describe(IReadOnlyList<AvailableHoursDayDto> week)
    {
        var ranges = week.ToDictionary(day => day.Day, day => (day.StartMinute, day.EndMinute));
        if (ranges.Count == MondayFirst.Length && ranges.Values.Distinct().Count() == 1)
        {
            var (start, end) = ranges[DayOfWeek.Monday];
            return $"Every day {FormatRange(start, end)}";
        }

        var groups = MondayFirst
            .Select((day, index) => (Index: index, Day: day))
            .Where(entry => ranges.ContainsKey(entry.Day))
            .GroupBy(entry => ranges[entry.Day], entry => entry.Index)
            .Select(group => $"{FormatDays([.. group])} {FormatRange(group.Key.StartMinute, group.Key.EndMinute)}")
            .ToList();

        var off = Enumerable.Range(0, MondayFirst.Length).Where(index => !ranges.ContainsKey(MondayFirst[index])).ToList();
        if (off.Count > 0)
        {
            groups.Add($"{FormatDays(off)} off");
        }

        return string.Join(" · ", groups);
    }

    /// <summary>The reply after hours are saved: the whole week, the zone it is read in, and what
    /// saving hours allows.</summary>
    /// <param name="week">The saved week.</param>
    /// <param name="userTimeZone">The member's own timezone, when they have set one.</param>
    /// <param name="guildTimeZone">The timezone of the server the command ran in; only read when
    /// the member has none of their own.</param>
    /// <returns>The reply.</returns>
    public static string SavedReply(IReadOnlyList<AvailableHoursDayDto> week, string? userTimeZone, string? guildTimeZone) =>
        $"🕒 Your available hours: **{Describe(week)}**\n{ZoneLine(userTimeZone, guildTimeZone)}\n{SavedNotice}";

    /// <summary>Which zone the saved hours are read in. Without a personal timezone they follow
    /// each server's own, so the same hours can mean different instants in two servers — the
    /// line says so and names the remedy.</summary>
    /// <param name="userTimeZone">The member's own timezone, when they have set one.</param>
    /// <param name="guildTimeZone">The timezone of the server the command ran in; only read when
    /// the member has none of their own. A server with none set runs on UTC.</param>
    /// <returns>The line.</returns>
    public static string ZoneLine(string? userTimeZone, string? guildTimeZone) =>
        string.IsNullOrWhiteSpace(userTimeZone)
            ? $"You haven't set a timezone, so these hours are read in each server's own — **{(string.IsNullOrWhiteSpace(guildTimeZone) ? "UTC" : guildTimeZone)}** here. " +
              "`/settings timezone` makes them mean the same everywhere."
            : $"Read in your timezone, **{userTimeZone}**.";

    /// <summary>The reply after hours are reset: the default applies, and other members can no
    /// longer list this member's free slots by name. A role they hold still reaches them, as it
    /// does on the availability grid.</summary>
    /// <returns>The reply.</returns>
    public static string ResetReply() =>
        $"🕒 Your available hours are back to the default, **{DefaultRange} every day**. " +
        "Other members can no longer list your free slots by name; a role you hold still counts you while your calendar is linked.";

    /// <summary>The line <c>/settings view</c> shows for the caller's hours.</summary>
    /// <param name="hours">The caller's hours as the API returned them.</param>
    /// <returns>The line.</returns>
    public static string ViewLine(AvailableHoursDto hours) =>
        hours.IsSet
            ? $"**Your available hours:** {Describe(hours.Days)} " +
              "(saved — members of servers you share with CalCrony can list your free slots by name)"
            : $"**Your available hours:** not saved — {DefaultRange} every day is assumed";

    /// <summary>The reply when a typed time cannot be read. It names the option and does not
    /// quote the text back.</summary>
    /// <param name="option">The option the time was typed into.</param>
    /// <returns>The reply.</returns>
    private static string CouldNotRead(string option) =>
        $"I couldn't read the `{option}` time. Try 6 PM, 6:30pm or 18:30.";

    /// <summary>A range as two times with an en dash: "6 PM–11 PM", "10 AM–midnight".</summary>
    /// <param name="startMinute">Start, in minutes from midnight.</param>
    /// <param name="endMinute">End, in minutes from midnight.</param>
    /// <returns>The formatted range.</returns>
    private static string FormatRange(int startMinute, int endMinute) =>
        $"{TimeOfDayText.Format(startMinute)}–{TimeOfDayText.Format(endMinute)}";

    /// <summary>Weekdays by their short names: three or more in a row as a span ("Mon–Fri"),
    /// anything else joined by commas ("Sat, Sun", "Mon, Wed, Fri").</summary>
    /// <param name="indexes">The weekdays as positions in the Monday-first week, in order.</param>
    /// <returns>The formatted days.</returns>
    private static string FormatDays(IReadOnlyList<int> indexes)
    {
        var parts = new List<string>();
        for (var i = 0; i < indexes.Count;)
        {
            var last = i;
            while (last + 1 < indexes.Count && indexes[last + 1] == indexes[last] + 1)
            {
                last++;
            }

            if (last - i >= 2)
            {
                parts.Add($"{ShortNames[indexes[i]]}–{ShortNames[indexes[last]]}");
            }
            else
            {
                for (var j = i; j <= last; j++)
                {
                    parts.Add(ShortNames[indexes[j]]);
                }
            }

            i = last + 1;
        }

        return string.Join(", ", parts);
    }
}
