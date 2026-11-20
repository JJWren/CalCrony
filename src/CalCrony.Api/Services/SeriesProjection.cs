using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>The live (scheduled or started) occurrence of a series, as far as a projection needs
/// it: when it starts and how long it lasts.</summary>
/// <param name="SeriesId">The series the occurrence belongs to.</param>
/// <param name="StartsAt">When the occurrence starts.</param>
/// <param name="DurationMinutes">Its stored length in minutes; null means
/// <see cref="EventTimes.DefaultLengthMinutes"/>.</param>
public readonly record struct LiveOccurrence(Guid SeriesId, Instant StartsAt, int? DurationMinutes);

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
/// <param name="DurationMinutes">The length of every occurrence, in minutes; null means
/// <see cref="EventTimes.DefaultLengthMinutes"/>.</param>
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

/// <summary>What a running series goes on to post. A series has one live occurrence at a time
/// (IX_Events_SeriesId_Live): the scheduler posts the next one only once that one has ended, and
/// then takes the first date of the schedule after the series' slot cursor that starts after that
/// moment (<see cref="SeriesMaterializer"/>). So a live occurrence that an edit of that occurrence
/// alone moved later — the cursor stays where it was — or that runs long holds back the dates it
/// passes: they are never posted, and they use up none of a count. Pure — no I/O.
/// <see cref="Upcoming"/> gives the later starts the public calendar projects;
/// <see cref="LaterOccurrences"/> gives the occurrences, each with its end, that reach into a
/// free-slots lookup's window.</summary>
public static class SeriesProjection
{
    /// <summary>Hard stop on stepping one series in <see cref="LaterOccurrences"/>. A free-slots
    /// lookup ends at most a year and fifteen days from today and a schedule's smallest step is a
    /// day, so no lookup the route accepts comes near it; it is there so that the loop is bounded
    /// on its own.</summary>
    public const int MaxSteps = 1000;

    /// <summary>When an occurrence ends: <see cref="EventTimes.End(Instant, int?)"/>, where the
    /// API works out an event's end.</summary>
    /// <param name="startsAt">When it starts.</param>
    /// <param name="durationMinutes">Its stored length in minutes; null means
    /// <see cref="EventTimes.DefaultLengthMinutes"/>.</param>
    /// <returns>The instant it ends.</returns>
    public static Instant EndOf(Instant startsAt, int? durationMinutes) =>
        EventTimes.End(startsAt, durationMinutes);

    /// <summary>The moment from which a series can post its next occurrence: now, or the end of
    /// its live occurrence when that is still to come.</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="liveEnd">When the series' live occurrence ends; null when it has none.</param>
    /// <returns>The later of the two.</returns>
    public static Instant FreeFrom(Instant now, Instant? liveEnd) =>
        liveEnd is { } end && end > now ? end : now;

    /// <summary>The occurrences a series goes on to post, in order, when each runs its length:
    /// the first date after the slot cursor that starts after <paramref name="freeFrom"/>, then
    /// the first that starts after that occurrence has ended, and so on — up to and including the
    /// until date, until the count is used up, or until <paramref name="maxSteps"/> occurrences
    /// have been listed, whichever comes first. The weekly day set is passed on: without it a
    /// Mon + Wed + Fri series would step on its first occurrence's weekday alone.</summary>
    /// <param name="series">The running series.</param>
    /// <param name="freeFrom">The moment from which the series can post (<see cref="FreeFrom"/>).</param>
    /// <param name="maxSteps">The most occurrences to list: each costs one step of the schedule,
    /// and for a series without an end the list would otherwise go on without end.</param>
    /// <returns>Start instants, ascending.</returns>
    public static IEnumerable<Instant> Upcoming(EventSeries series, Instant freeFrom, int maxSteps)
    {
        var zone = Endpoints.Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
        var remaining = series.MaxOccurrences is int max ? max - series.OccurrenceCount : int.MaxValue;
        var cursor = series.CurrentOccurrenceDate;
        var floor = freeFrom;
        for (var step = 0; step < maxSteps && remaining > 0; step++)
        {
            var next = RecurrenceCalculator.NextOccurrence(
                series.Unit, series.Interval, series.MonthlyMode, series.AnchorDate, series.StartTime,
                zone, cursor, series.UntilDate, floor, series.DaysOfWeek);
            if (next is null)
            {
                yield break;
            }

            yield return next.Value.Instant;

            // That occurrence is the live one until it ends, at the series' own length. Its end is
            // worked out only when the list steps on from it.
            remaining--;
            if (step + 1 >= maxSteps || remaining <= 0)
            {
                yield break;
            }

            cursor = next.Value.Date;
            floor = EndOf(next.Value.Instant, series.DurationMinutes);
        }
    }

    /// <summary>The later occurrences of a running series that reach into a window, in order —
    /// what the scheduler will create, as far as that can be foreseen: a skip, a cancellation, a
    /// move or an edit made later changes it, and the scheduler acts on an end at its next sweep,
    /// which makes a difference only to an occurrence that ends less than a sweep before a slot.
    /// The first is the first slot after the series' cursor that starts once the live occurrence
    /// is over — after <paramref name="now"/> when there is no live occurrence, or it is over
    /// already — and each one after that is the first slot that starts after the one before it
    /// ends. None falls after the until date, and with a count only as many as are left are
    /// given: the ones before the window use up the count as well. A series that cannot reach the
    /// window (<see cref="CanReachWindow"/>) is passed over before its rule is stepped. A rule
    /// that cannot be stepped, such as one with an interval of zero, throws; a free-slots lookup
    /// leaves that series' later occurrences out (<see cref="EventBusyTimes"/>).</summary>
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

    /// <summary>Whether an occurrence of a series can still be running when the next date of its
    /// schedule begins, between two moments, so that the series does not post that date. It can
    /// when its length, with the largest step forward of its zone's clocks between those moments
    /// added, reaches the fewest days between two dates of its schedule
    /// (<see cref="RecurrenceCalculator.ShortestGapDays"/>): a night in which the clocks go
    /// forward is that much shorter, and so is the time between the dates on either side of it.
    /// False means the series' own length holds no date back between those moments — a live
    /// occurrence moved or made longer on its own still can, and <see cref="FreeFrom"/> allows for
    /// that; true means a date can be held back.</summary>
    /// <param name="series">The series.</param>
    /// <param name="from">The first moment looked at.</param>
    /// <param name="until">The last moment looked at; not before <paramref name="from"/>.</param>
    /// <returns>Whether an occurrence can reach the next date.</returns>
    public static bool CanHoldDatesBack(EventSeries series, Instant from, Instant until) =>
        LengthSeconds(series) + LargestStepForwardSeconds(ZoneOf(series), from, until) >= GapSeconds(series);

    /// <summary>Whether an occurrence of a series lasts as long as the fewest days between two
    /// dates of its schedule, or longer (<see cref="RecurrenceCalculator.ShortestGapDays"/>), so
    /// that it holds dates back wherever the clocks are: every second date of a daily series a day
    /// long. Such a length is refused when a series is made or changed; a series made before that
    /// can still have one.</summary>
    /// <param name="series">The series.</param>
    /// <returns>Whether its length reaches the gap.</returns>
    public static bool LastsItsGap(EventSeries series) => LengthSeconds(series) >= GapSeconds(series);

    /// <summary>The dates of a series' schedule that it will not post before <paramref name="until"/>,
    /// because the occurrence before is still running when they begin — what
    /// <see cref="Upcoming"/> steps over — and, when its count is used up before then, the last
    /// date it posts. Every date of the schedule from the first one <see cref="Upcoming"/> lists
    /// is either posted or held back, so the dates it posts are the schedule's less these.</summary>
    /// <param name="series">The running series.</param>
    /// <param name="freeFrom">The moment from which the series can post (<see cref="FreeFrom"/>).</param>
    /// <param name="until">The end of the time looked at: a date that begins then or later is
    /// not looked at.</param>
    /// <param name="maxSteps">The most dates of the schedule to step through, posted or not.</param>
    /// <returns>The dates held back, in order, and the last date posted when the count is used up
    /// before <paramref name="until"/>.</returns>
    public static HeldBack HeldBackDates(EventSeries series, Instant freeFrom, Instant until, int maxSteps)
    {
        var zone = ZoneOf(series);
        var remaining = series.MaxOccurrences is int max ? max - series.OccurrenceCount : int.MaxValue;
        var held = new List<Instant>();
        Instant? lastPosted = null;
        var cursor = series.CurrentOccurrenceDate;

        // The schedule's next date is the first that begins after `named`; it is posted when it
        // begins after `free`, the end of the occurrence before it, as in Upcoming.
        var named = freeFrom;
        var free = freeFrom;
        for (var step = 0; step < maxSteps && remaining > 0; step++)
        {
            var next = RecurrenceCalculator.NextOccurrence(
                series.Unit, series.Interval, series.MonthlyMode, series.AnchorDate, series.StartTime,
                zone, cursor, series.UntilDate, named, series.DaysOfWeek);
            if (next is null || next.Value.Instant >= until)
            {
                break;
            }

            cursor = next.Value.Date;
            named = next.Value.Instant;
            if (next.Value.Instant > free)
            {
                lastPosted = next.Value.Instant;
                remaining--;
                if (remaining > 0)
                {
                    free = EndOf(next.Value.Instant, series.DurationMinutes);
                }
            }
            else
            {
                held.Add(next.Value.Instant);
            }
        }

        return new HeldBack(held, remaining == 0 ? lastPosted : null);
    }

    /// <summary>A series' length, in whole seconds.</summary>
    /// <param name="series">The series.</param>
    /// <returns>Its length, or the default hour's, in seconds.</returns>
    private static long LengthSeconds(EventSeries series) =>
        (long)(series.DurationMinutes ?? EventTimes.DefaultLengthMinutes) * 60;

    /// <summary>The fewest days between two dates a series repeats on, in whole seconds.</summary>
    /// <param name="series">The series.</param>
    /// <returns>The gap, in seconds.</returns>
    private static long GapSeconds(EventSeries series) =>
        RecurrenceCalculator.ShortestGapDays(series.Unit, series.Interval, series.MonthlyMode, series.DaysOfWeek, series.AnchorDate)
        * 24 * 60 * 60;

    /// <summary>The zone a series is read in: its own, or UTC for an id that is not known.</summary>
    /// <param name="series">The series.</param>
    /// <returns>The zone.</returns>
    private static DateTimeZone ZoneOf(EventSeries series) => Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;

    /// <summary>The largest step forward of a zone's clocks between two moments, in seconds: zero
    /// for a zone whose clocks do not go forward between them.</summary>
    /// <param name="zone">The zone.</param>
    /// <param name="from">The first moment.</param>
    /// <param name="until">The last moment; not before <paramref name="from"/>.</param>
    /// <returns>The step, in seconds.</returns>
    private static long LargestStepForwardSeconds(DateTimeZone zone, Instant from, Instant until)
    {
        long largest = 0;
        Offset? before = null;
        foreach (var interval in zone.GetZoneIntervals(from, until))
        {
            if (before is { } previous)
            {
                largest = Math.Max(largest, (long)interval.WallOffset.Seconds - previous.Seconds);
            }

            before = interval.WallOffset;
        }

        return largest;
    }
}

/// <summary>The dates of a series' schedule it will not post within a time looked at, because the
/// occurrence before is still running when they begin (<see cref="SeriesProjection.HeldBackDates"/>).</summary>
/// <param name="Dates">The start instants of those dates, in order.</param>
/// <param name="LastPosted">The last date the series posts, when its count is used up within that
/// time; null otherwise.</param>
public sealed record HeldBack(IReadOnlyList<Instant> Dates, Instant? LastPosted);
