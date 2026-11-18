using CalCrony.Api.Endpoints;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>The schedule of a running series, as far as its later occurrences depend on it: the
/// rule, the cursor and the count the scheduler keeps, and the length every occurrence has.
/// Nothing else of the series is in it — no title, place or channel.</summary>
/// <param name="Unit">The recurrence unit.</param>
/// <param name="Interval">Every N units (1-12).</param>
/// <param name="MonthlyMode">The monthly mode (ignored for non-month units).</param>
/// <param name="DaysOfWeek">The weekly day set (ignored for non-week units; None = the anchor's weekday).</param>
/// <param name="AnchorDate">The local date of the first occurrence.</param>
/// <param name="StartTime">The local wall-clock start time of every occurrence.</param>
/// <param name="TimeZone">The IANA zone the schedule is read in; an id that is not known reads as UTC.</param>
/// <param name="UntilDate">The inclusive last local date an occurrence may fall on, when set.</param>
/// <param name="MaxOccurrences">How many occurrences the series may have in all, when set.</param>
/// <param name="CurrentOccurrenceDate">The slot date of the last occurrence the scheduler created.</param>
/// <param name="OccurrenceCount">How many occurrences the scheduler has created so far.</param>
/// <param name="DurationMinutes">The length of every occurrence, in minutes; null for the default length.</param>
public sealed record SeriesSchedule(
    RecurrenceUnit Unit,
    int Interval,
    MonthlyMode MonthlyMode,
    RecurrenceDays DaysOfWeek,
    LocalDate AnchorDate,
    LocalTime StartTime,
    string TimeZone,
    LocalDate? UntilDate,
    int? MaxOccurrences,
    LocalDate CurrentOccurrenceDate,
    int OccurrenceCount,
    int? DurationMinutes);

/// <summary>The occurrence of a series that exists as an event, as far as the next one depends on
/// it: when it starts and how long it lasts.</summary>
/// <param name="StartsAt">When it starts.</param>
/// <param name="DurationMinutes">Its length in minutes; null for the default length.</param>
public sealed record LiveOccurrence(Instant StartsAt, int? DurationMinutes);

/// <summary>Which occurrences a running series will have after the one that exists. Only one
/// occurrence of a series is a row at any time: the scheduler creates the next once that one is
/// over, as the first slot of the schedule that starts after it. This follows the same steps
/// ahead of time, so what it gives is what the scheduler will create, as far as that can be
/// foreseen: a skip, a cancellation, a move or an edit made later changes it, and the scheduler
/// acts on an end at its next sweep, which makes a difference only to an occurrence that ends
/// less than a sweep before a slot. So it is not every slot of the schedule: an occurrence moved
/// past later slots, or one that outlasts the gap to the next, leaves the slots it passes empty.
/// Pure: no clock and no database.</summary>
public static class SeriesProjection
{
    /// <summary>Hard stop on stepping one series. A free-slots lookup ends at most a year and
    /// fifteen days from today and a schedule's smallest step is a day, so no lookup the route
    /// accepts comes near it; it is there so that the loop is bounded on its own.</summary>
    public const int MaxSteps = 1000;

    /// <summary>The later occurrences of a running series that reach into a window, in order.
    /// The first is the first slot after the series' cursor that starts once the live occurrence
    /// is over — after <paramref name="now"/> when there is no live occurrence, or it is over
    /// already — and each one after that is the first slot that starts after the one before it
    /// ends. None falls after the until date, and with a count only as many as are left are
    /// given: the ones before the window use up the count as well. A series that cannot reach the
    /// window (<see cref="CanReachWindow"/>) is passed over before its rule is stepped. A rule
    /// that cannot be stepped, such as one with an interval of zero, throws; a free-slots lookup
    /// leaves that series out (<see cref="EventBusyTimes"/>).</summary>
    /// <param name="schedule">The series' schedule. The series is running: one that was stopped
    /// has no later occurrences and is not asked about.</param>
    /// <param name="live">The series' live occurrence; null when it has none.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="windowStart">Window start: an occurrence counts when it ends after this.</param>
    /// <param name="windowEnd">Window end: an occurrence counts when it starts before this.</param>
    /// <returns>Each occurrence's start and end. They may reach outside the window.</returns>
    public static IReadOnlyList<(Instant Start, Instant End)> LaterOccurrences(
        SeriesSchedule schedule, LiveOccurrence? live, Instant now, Instant windowStart, Instant windowEnd)
    {
        if (!CanReachWindow(schedule, live, windowEnd))
        {
            return [];
        }

        var occurrences = new List<(Instant Start, Instant End)>();

        // The count the scheduler checks before it creates an occurrence: none once it is used up.
        var remaining = schedule.MaxOccurrences is int max ? max - schedule.OccurrenceCount : int.MaxValue;

        // Nothing is created while an occurrence is live, so the next one starts once it is over:
        // after it ends, and never before it starts.
        var floor = now;
        if (live is { } current)
        {
            var currentEnd = EventTimes.End(current.StartsAt, current.DurationMinutes);
            floor = Instant.Max(floor, Instant.Max(current.StartsAt, currentEnd));
        }

        var zone = ZoneOf(schedule);

        var cursor = schedule.CurrentOccurrenceDate;
        for (var step = 0; step < MaxSteps && remaining > 0; step++)
        {
            var next = RecurrenceCalculator.NextOccurrence(
                schedule.Unit, schedule.Interval, schedule.MonthlyMode, schedule.AnchorDate, schedule.StartTime,
                zone, cursor, schedule.UntilDate, floor, schedule.DaysOfWeek);
            if (next is null || next.Value.Instant >= windowEnd)
            {
                break;
            }

            var occurrenceEnd = EventTimes.End(next.Value.Instant, schedule.DurationMinutes);
            if (occurrenceEnd > windowStart)
            {
                occurrences.Add((next.Value.Instant, occurrenceEnd));
            }

            cursor = next.Value.Date;
            floor = occurrenceEnd;
            remaining--;
        }

        return occurrences;
    }

    /// <summary>Whether a running series can have a later occurrence that starts before a window
    /// ends. Every later occurrence starts after the live one, on a date after the last one
    /// created and not before the first. So a series cannot when its live occurrence starts as the
    /// window ends or later, when its first date lies more than a day past the date the window
    /// ends on, or when its last created date lies past that date. The day left between keeps a
    /// clock change near midnight from mattering.</summary>
    /// <param name="schedule">The series' schedule.</param>
    /// <param name="live">The series' live occurrence; null when it has none.</param>
    /// <param name="windowEnd">Window end.</param>
    /// <returns>False when nothing of the series can start before the window ends. True does not
    /// say that something will.</returns>
    public static bool CanReachWindow(SeriesSchedule schedule, LiveOccurrence? live, Instant windowEnd)
    {
        var dayPastTheWindow = windowEnd.InZone(ZoneOf(schedule)).Date.PlusDays(1);
        var passedOver = (live is { } later && later.StartsAt >= windowEnd)
                         || schedule.AnchorDate > dayPastTheWindow
                         || schedule.CurrentOccurrenceDate >= dayPastTheWindow;
        return !passedOver;
    }

    /// <summary>The zone a schedule is read in: its own, or UTC for an id that is not known.</summary>
    /// <param name="schedule">The series' schedule.</param>
    /// <returns>The zone.</returns>
    private static DateTimeZone ZoneOf(SeriesSchedule schedule) => Mapping.FindZone(schedule.TimeZone) ?? DateTimeZone.Utc;
}
