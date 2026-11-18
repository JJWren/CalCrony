using CalCrony.Api.Data;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>The live (scheduled or started) occurrence of a series, as far as a projection needs
/// it: when it starts and how long it lasts.</summary>
/// <param name="SeriesId">The series the occurrence belongs to.</param>
/// <param name="StartsAt">When the occurrence starts.</param>
/// <param name="DurationMinutes">Its stored length in minutes; null means
/// <see cref="EventTimes.DefaultLengthMinutes"/>.</param>
public readonly record struct LiveOccurrence(Guid SeriesId, Instant StartsAt, int? DurationMinutes);

/// <summary>What a running series goes on to post. A series has one live occurrence at a time
/// (IX_Events_SeriesId_Live): the scheduler posts the next one only once that one has ended, and
/// then takes the first date of the schedule after the series' slot cursor that starts after that
/// moment (<see cref="SeriesMaterializer"/>). So a live occurrence that an edit of that occurrence
/// alone moved later — the cursor stays where it was — or that runs long holds back the dates it
/// passes: they are never posted, and they use up none of a count. Pure — no I/O.</summary>
public static class SeriesProjection
{
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

            // That occurrence is the live one until it ends, at the series' own length.
            cursor = next.Value.Date;
            floor = EndOf(next.Value.Instant, series.DurationMinutes);
            remaining--;
        }
    }
}
