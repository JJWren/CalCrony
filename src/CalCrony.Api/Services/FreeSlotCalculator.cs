using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>What a free-slots lookup asks for.</summary>
/// <param name="Now">The current instant: nothing before it — rounded up to a quarter hour — is offered.</param>
/// <param name="CallerZone">The zone whose calendar days are listed.</param>
/// <param name="FirstDay">The first day listed, in <paramref name="CallerZone"/>.</param>
/// <param name="Days">How many days are listed (at least one).</param>
/// <param name="Minimum">The shortest slot worth listing.</param>
public sealed record FreeSlotQuery(Instant Now, DateTimeZone CallerZone, LocalDate FirstDay, int Days, Duration Minimum);

/// <summary>One counted member's side of the calculation.</summary>
/// <param name="Zone">The zone the member's hours are read in.</param>
/// <param name="Hours">The member's available hours.</param>
/// <param name="Busy">Busy intervals from the member's linked calendar, in any order; they may
/// overlap, touch, or reach outside the window. Empty when the member has no calendar.</param>
public sealed record MemberAvailability(
    DateTimeZone Zone, WeeklyHours Hours, IReadOnlyList<(Instant Start, Instant End)> Busy);

/// <summary>One requested day and the slots that start on it.</summary>
/// <param name="Date">The calendar day in the caller's zone.</param>
/// <param name="Slots">The slots starting on that day, in order; empty when none does.</param>
/// <param name="OpenUntil">When the day begins inside a slot that started on an earlier day, the
/// instant that slot ends; null when the day begins outside any slot.</param>
public sealed record FreeSlotDay(
    LocalDate Date, IReadOnlyList<(Instant Start, Instant End)> Slots, Instant? OpenUntil);

/// <summary>Pure free-slot math: no clock, no database. A slot is a stretch in which every member
/// is inside their own available hours and has no busy interval, at least the minimum long. Days
/// are the caller's; hours are each member's own wall clock, so members in different zones — and
/// days that are 23 or 25 hours long — need no special cases here.</summary>
public static class FreeSlotCalculator
{
    private static readonly long TicksPerQuarterHour = 15 * NodaConstants.TicksPerMinute;

    /// <summary>The window a lookup covers: from the later of the first day's midnight and "now"
    /// rounded up to the next quarter hour, to the midnight one day after the last requested day.
    /// The extra day is what lets a slot that starts late on the last day show its real end. This
    /// is both what the calendar provider is asked for and what <see cref="Compute"/> works over.</summary>
    /// <param name="query">The lookup.</param>
    /// <returns>The window's start and end.</returns>
    public static (Instant Start, Instant End) QueryWindow(FreeSlotQuery query)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Days, 1, nameof(query));

        var start = Instant.Max(StartOfDay(query.FirstDay, query.CallerZone), RoundUpToQuarterHour(query.Now));
        var end = StartOfDay(query.FirstDay.PlusDays(query.Days + 1), query.CallerZone);
        return (start, end);
    }

    /// <summary>The slots of each requested day. A slot belongs to the caller's day it starts on,
    /// and one that runs past midnight is not cut there: the day it runs into says how long it
    /// stays open instead (<see cref="FreeSlotDay.OpenUntil"/>), so a day with no slot of its own
    /// is not mistaken for one with nothing free. Every requested day is returned, in order, with
    /// or without slots.</summary>
    /// <param name="query">The lookup.</param>
    /// <param name="members">The counted members. With none, every day comes back empty.</param>
    /// <returns>One entry per requested day.</returns>
    public static IReadOnlyList<FreeSlotDay> Compute(FreeSlotQuery query, IReadOnlyList<MemberAvailability> members)
    {
        var (windowStart, windowEnd) = QueryWindow(query);
        var lastStart = StartOfDay(query.FirstDay.PlusDays(query.Days), query.CallerZone);

        List<(Instant Start, Instant End)> free = [];
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var available = Subtract(
                HoursIntervals(member, windowStart, windowEnd),
                Normalize(member.Busy, windowStart, windowEnd));
            free = i == 0 ? available : Intersect(free, available);
            if (free.Count == 0)
            {
                break;
            }
        }

        var listed = free
            .Where(slot => slot.Start < lastStart && slot.End - slot.Start >= query.Minimum)
            .ToList();
        var slotsByDay = listed.ToLookup(slot => slot.Start.InZone(query.CallerZone).Date);

        return [.. Enumerable.Range(0, query.Days)
            .Select(offset => query.FirstDay.PlusDays(offset))
            .Select(day => new FreeSlotDay(
                day, [.. slotsByDay[day]], OpenUntil(listed, StartOfDay(day, query.CallerZone))))];
    }

    /// <summary>When the listed slot a day begins inside ends, if there is one: a slot that
    /// started on an earlier day and is still running at the day's first instant. A slot that
    /// starts exactly then is the day's own, and one that ends exactly then is over.</summary>
    /// <param name="listed">The listed slots, sorted and disjoint.</param>
    /// <param name="dayStart">The day's first instant.</param>
    /// <returns>The instant that slot ends, or null when the day begins outside any slot.</returns>
    private static Instant? OpenUntil(List<(Instant Start, Instant End)> listed, Instant dayStart)
    {
        foreach (var (start, end) in listed)
        {
            if (start >= dayStart)
            {
                break;
            }

            if (end > dayStart)
            {
                return end;
            }
        }

        return null;
    }

    /// <summary>The first instant of a calendar day in a zone.</summary>
    /// <param name="day">The calendar day.</param>
    /// <param name="zone">The zone.</param>
    /// <returns>The day's first instant.</returns>
    private static Instant StartOfDay(LocalDate day, DateTimeZone zone) => day.AtStartOfDayInZone(zone).ToInstant();

    /// <summary>Rounds an instant up to the next quarter hour; an exact quarter hour stays. Every
    /// zone in use is offset from UTC by a multiple of fifteen minutes, so the result is a quarter
    /// hour on the local clock too.</summary>
    /// <param name="instant">The instant to round.</param>
    /// <returns>The first quarter hour at or after the instant.</returns>
    private static Instant RoundUpToQuarterHour(Instant instant)
    {
        var ticks = instant.ToUnixTimeTicks();
        var remainder = ((ticks % TicksPerQuarterHour) + TicksPerQuarterHour) % TicksPerQuarterHour;
        return remainder == 0 ? instant : Instant.FromUnixTimeTicks(ticks - remainder + TicksPerQuarterHour);
    }

    /// <summary>A member's available hours inside the window, as sorted, disjoint intervals. Each
    /// local date in the member's zone contributes its weekday's range; ranges that touch (hours to
    /// midnight, then hours from midnight) join into one stretch.</summary>
    /// <param name="member">The member.</param>
    /// <param name="windowStart">Window start.</param>
    /// <param name="windowEnd">Window end.</param>
    /// <returns>The intervals, clipped to the window.</returns>
    private static List<(Instant Start, Instant End)> HoursIntervals(
        MemberAvailability member, Instant windowStart, Instant windowEnd)
    {
        var intervals = new List<(Instant Start, Instant End)>();
        if (windowStart >= windowEnd)
        {
            return intervals;
        }

        // A range lies inside one local day, so only the local dates the window touches matter.
        var lastDate = windowEnd.InZone(member.Zone).Date;
        for (var date = windowStart.InZone(member.Zone).Date; date <= lastDate; date = date.PlusDays(1))
        {
            if (member.Hours.RangeFor(date.DayOfWeek) is not { } range)
            {
                continue;
            }

            AddMerged(
                intervals,
                Instant.Max(AtMinute(date, range.StartMinute, member.Zone), windowStart),
                Instant.Min(AtMinute(date, range.EndMinute, member.Zone), windowEnd));
        }

        return intervals;
    }

    /// <summary>The instant of a wall-clock minute on a local date. Lenient mapping keeps wall-clock
    /// meaning across daylight-saving changes: a time the clocks skip moves forward by the gap, and
    /// a time that occurs twice takes its first occurrence.</summary>
    /// <param name="date">The local date.</param>
    /// <param name="minute">Minutes from midnight; 1440 is the next day's midnight.</param>
    /// <param name="zone">The zone.</param>
    /// <returns>The instant.</returns>
    private static Instant AtMinute(LocalDate date, int minute, DateTimeZone zone)
    {
        var local = minute == AvailableHoursLimits.MinutesPerDay
            ? date.PlusDays(1).AtMidnight()
            : date.AtMidnight().PlusMinutes(minute);
        return local.InZoneLeniently(zone).ToInstant();
    }

    /// <summary>Busy intervals made usable: empty and inverted ones dropped, the rest clipped to
    /// the window, sorted, and merged where they overlap or touch — a provider promises none of it.</summary>
    /// <param name="busy">The raw busy intervals.</param>
    /// <param name="windowStart">Window start.</param>
    /// <param name="windowEnd">Window end.</param>
    /// <returns>Sorted, disjoint busy intervals inside the window.</returns>
    private static List<(Instant Start, Instant End)> Normalize(
        IReadOnlyList<(Instant Start, Instant End)> busy, Instant windowStart, Instant windowEnd)
    {
        var merged = new List<(Instant Start, Instant End)>(busy.Count);
        foreach (var (start, end) in busy.OrderBy(block => block.Start))
        {
            AddMerged(merged, Instant.Max(start, windowStart), Instant.Min(end, windowEnd));
        }

        return merged;
    }

    /// <summary>Appends an interval to a list built in start order, joining it to the last one when
    /// they overlap or touch. An empty or inverted interval is ignored.</summary>
    /// <param name="intervals">The list being built, sorted by start.</param>
    /// <param name="start">The interval's start; not before the last interval's start.</param>
    /// <param name="end">The interval's end.</param>
    private static void AddMerged(List<(Instant Start, Instant End)> intervals, Instant start, Instant end)
    {
        if (start >= end)
        {
            return;
        }

        if (intervals.Count > 0 && start <= intervals[^1].End)
        {
            intervals[^1] = (intervals[^1].Start, Instant.Max(intervals[^1].End, end));
            return;
        }

        intervals.Add((start, end));
    }

    /// <summary>What is left of the intervals once the busy ones are cut out.</summary>
    /// <param name="intervals">Sorted, disjoint intervals.</param>
    /// <param name="busy">Sorted, disjoint busy intervals.</param>
    /// <returns>Sorted, disjoint intervals with no busy time in them.</returns>
    private static List<(Instant Start, Instant End)> Subtract(
        List<(Instant Start, Instant End)> intervals, List<(Instant Start, Instant End)> busy)
    {
        var result = new List<(Instant Start, Instant End)>(intervals.Count + busy.Count);
        var firstBusy = 0;
        foreach (var (start, end) in intervals)
        {
            // Busy intervals that end before this one starts can't touch any later one either.
            while (firstBusy < busy.Count && busy[firstBusy].End <= start)
            {
                firstBusy++;
            }

            var cursor = start;
            for (var i = firstBusy; i < busy.Count && busy[i].Start < end && cursor < end; i++)
            {
                if (busy[i].Start > cursor)
                {
                    result.Add((cursor, busy[i].Start));
                }

                cursor = Instant.Max(cursor, busy[i].End);
            }

            if (cursor < end)
            {
                result.Add((cursor, end));
            }
        }

        return result;
    }

    /// <summary>The time two interval lists share.</summary>
    /// <param name="first">Sorted, disjoint intervals.</param>
    /// <param name="second">Sorted, disjoint intervals.</param>
    /// <returns>Sorted, disjoint intervals present in both.</returns>
    private static List<(Instant Start, Instant End)> Intersect(
        List<(Instant Start, Instant End)> first, List<(Instant Start, Instant End)> second)
    {
        var result = new List<(Instant Start, Instant End)>();
        var i = 0;
        var j = 0;
        while (i < first.Count && j < second.Count)
        {
            var start = Instant.Max(first[i].Start, second[j].Start);
            var end = Instant.Min(first[i].End, second[j].End);
            if (start < end)
            {
                result.Add((start, end));
            }

            // Whichever interval ends first can't overlap anything further along the other list.
            if (first[i].End < second[j].End)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return result;
    }
}
