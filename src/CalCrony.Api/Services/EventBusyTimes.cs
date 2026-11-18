using System.Data;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>
/// The time CalCrony's own events keep members busy, for a free-slots lookup. An event of the
/// server counts for the member who created it and for a member holding a seat on its attending
/// option — an RSVP there that is not waitlisted. A running series counts for the member who
/// created it, on the later occurrences the scheduler will create
/// (<see cref="SeriesProjection"/>). Only live events are read, scheduled or started, and only
/// those of the one server. Nothing but ids, times, lengths and schedules is selected — no title,
/// description, place or channel — and nothing is stored or cached, so an event that is moved,
/// cancelled or deleted shows in the next lookup.
/// </summary>
/// <param name="db">The database context.</param>
public sealed class EventBusyTimes(CalCronyDbContext db)
{
    /// <summary>How long before a window an event may start and still reach into it: the longest
    /// length an event can be given.</summary>
    private static readonly Duration LongestEvent = Duration.FromMinutes(FieldLimits.MaxMinutes);

    /// <summary>The busy intervals of each member from the events of one server. At most five
    /// reads, whatever the number of members, days or events: the server's live events that can
    /// reach into the window; these members' seats on them; the options of the events that have
    /// such a seat; the running series these members created; and those series' live occurrences.
    /// The reads share one snapshot of the database, in a transaction of their own, so this is
    /// called outside a transaction; the series are stepped once it is closed.</summary>
    /// <param name="guildId">The server whose events count. No other server's are read.</param>
    /// <param name="memberIds">The members to answer for: the ones the lookup counts.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="windowStart">Window start.</param>
    /// <param name="windowEnd">Window end.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One entry per member, with that member's busy intervals in no particular order.
    /// They may overlap and may reach outside the window; a member with none has an empty list.</returns>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<(Instant Start, Instant End)>>> GetAsync(
        long guildId,
        IReadOnlyCollection<long> memberIds,
        Instant now,
        Instant windowStart,
        Instant windowEnd,
        CancellationToken cancellationToken)
    {
        var busy = memberIds.Distinct().ToDictionary(id => id, _ => new List<(Instant Start, Instant End)>());
        if (busy.Count > 0 && windowStart < windowEnd)
        {
            var ids = busy.Keys.ToArray();

            // One snapshot for every read. The scheduler ends a series' live occurrence and creates
            // the next in one transaction; reads on either side of it could otherwise see the next
            // occurrence neither as an event nor as a later date of its series.
            IReadOnlyList<RunningSeries> running;
            await using (var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken))
            {
                await AddEventsAsync(busy, guildId, ids, windowStart, windowEnd, cancellationToken);
                running = await ReadRunningSeriesAsync(guildId, ids, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            // A series' later occurrences count for its creator and for nobody else: RSVPs start
            // afresh on each occurrence, so nobody is seated in one that does not exist yet.
            foreach (var series in running)
            {
                busy[series.CreatorId].AddRange(
                    SeriesProjection.LaterOccurrences(series.Schedule, series.Live, now, windowStart, windowEnd));
            }
        }

        return busy.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<(Instant Start, Instant End)>)pair.Value);
    }

    /// <summary>Adds the server's live events to the members they count for: the creator, and
    /// whoever holds a seat on the attending option. An event counts once for a member, however
    /// many ties they have to it.</summary>
    /// <param name="busy">The intervals being collected, by member.</param>
    /// <param name="guildId">The server.</param>
    /// <param name="memberIds">The members answered for.</param>
    /// <param name="windowStart">Window start.</param>
    /// <param name="windowEnd">Window end.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private async Task AddEventsAsync(
        Dictionary<long, List<(Instant Start, Instant End)>> busy,
        long guildId,
        long[] memberIds,
        Instant windowStart,
        Instant windowEnd,
        CancellationToken cancellationToken)
    {
        // An event's end is not a column, so the range is on its start: before the window ends,
        // and later than the longest event before the window starts. One that starts exactly
        // that long before ends as the window starts at the latest, so it is not in the window.
        var earliestStart = windowStart - LongestEvent;
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.GuildId == guildId
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started)
                        && e.StartsAt < windowEnd
                        && e.StartsAt > earliestStart)
            .Select(e => new { e.Id, e.CreatorId, e.StartsAt, e.DurationMinutes })
            .ToListAsync(cancellationToken);
        var events = rows
            .Select(e => (e.Id, e.CreatorId, Start: e.StartsAt, End: EventTimes.End(e.StartsAt, e.DurationMinutes)))
            .Where(e => e.End > windowStart)
            .ToList();
        if (events.Count == 0)
        {
            return;
        }

        // Only these members' RSVPs are read, and a waitlisted one holds no seat.
        var eventIds = events.Select(e => e.Id).ToArray();
        var seats = await db.Rsvps.AsNoTracking()
            .Where(r => eventIds.Contains(r.EventId) && !r.Waitlisted && memberIds.Contains(r.UserId))
            .Select(r => new { r.EventId, r.UserId, r.OptionId })
            .ToListAsync(cancellationToken);

        // Which option of an event is the attending one is RsvpPolicy's rule, applied to the two
        // columns it needs. Events nobody here holds a seat in need no options read.
        var attendingOptions = new HashSet<Guid>();
        var seatedEventIds = seats.Select(s => s.EventId).Distinct().ToArray();
        if (seatedEventIds.Length > 0)
        {
            var options = await db.RsvpOptions.AsNoTracking()
                .Where(o => seatedEventIds.Contains(o.EventId))
                .Select(o => new OptionRow(o.EventId, o.Id, o.IsAttending, o.SortOrder))
                .ToListAsync(cancellationToken);
            foreach (var eventOptions in options.GroupBy(o => o.EventId))
            {
                if (RsvpPolicy.AttendingOf(eventOptions, o => o.IsAttending, o => o.SortOrder) is { } attending)
                {
                    attendingOptions.Add(attending.Id);
                }
            }
        }

        var seated = seats
            .Where(s => attendingOptions.Contains(s.OptionId))
            .Select(s => (s.EventId, s.UserId))
            .ToHashSet();

        foreach (var ev in events)
        {
            foreach (var (memberId, intervals) in busy)
            {
                if (ev.CreatorId == memberId || seated.Contains((ev.Id, memberId)))
                {
                    intervals.Add((ev.Start, ev.End));
                }
            }
        }
    }

    /// <summary>Reads the running series the members created, each with its schedule and its live
    /// occurrence, for <see cref="SeriesProjection"/> to step once the reads are over.</summary>
    /// <param name="guildId">The server.</param>
    /// <param name="memberIds">The members answered for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The series, none when the members created none.</returns>
    private async Task<IReadOnlyList<RunningSeries>> ReadRunningSeriesAsync(
        long guildId, long[] memberIds, CancellationToken cancellationToken)
    {
        var series = await db.EventSeries.AsNoTracking()
            .Where(s => s.GuildId == guildId && !s.Ended && memberIds.Contains(s.CreatorId))
            .Select(s => new
            {
                s.Id,
                s.CreatorId,
                s.Unit,
                s.Interval,
                s.MonthlyMode,
                s.DaysOfWeek,
                s.AnchorDate,
                s.StartTime,
                s.TimeZone,
                s.UntilDate,
                s.MaxOccurrences,
                s.CurrentOccurrenceDate,
                s.OccurrenceCount,
                s.DurationMinutes,
            })
            .ToListAsync(cancellationToken);
        if (series.Count == 0)
        {
            return [];
        }

        // The occurrence that exists is an event like any other and is counted as one. Here it
        // only says when the next can start. A series has at most one: the index on live
        // occurrences allows no more.
        var seriesIds = series.Select(s => s.Id).ToArray();
        var liveRows = await db.Events.AsNoTracking()
            .Where(e => e.SeriesId != null
                        && seriesIds.Contains(e.SeriesId.Value)
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started))
            .Select(e => new { SeriesId = e.SeriesId!.Value, e.StartsAt, e.DurationMinutes })
            .ToListAsync(cancellationToken);
        var live = liveRows
            .GroupBy(e => e.SeriesId)
            .ToDictionary(g => g.Key, g => new LiveOccurrence(g.First().StartsAt, g.First().DurationMinutes));

        return
        [
            .. series.Select(s => new RunningSeries(
                s.CreatorId,
                new SeriesSchedule(
                    Unit: s.Unit,
                    Interval: s.Interval,
                    MonthlyMode: s.MonthlyMode,
                    DaysOfWeek: s.DaysOfWeek,
                    AnchorDate: s.AnchorDate,
                    StartTime: s.StartTime,
                    TimeZone: s.TimeZone,
                    UntilDate: s.UntilDate,
                    MaxOccurrences: s.MaxOccurrences,
                    CurrentOccurrenceDate: s.CurrentOccurrenceDate,
                    OccurrenceCount: s.OccurrenceCount,
                    DurationMinutes: s.DurationMinutes),
                live.GetValueOrDefault(s.Id))),
        ];
    }

    /// <summary>A running series as the projection needs it.</summary>
    /// <param name="CreatorId">The member who created it, the one its later occurrences count for.</param>
    /// <param name="Schedule">Its schedule.</param>
    /// <param name="Live">Its live occurrence; null when it has none.</param>
    private sealed record RunningSeries(long CreatorId, SeriesSchedule Schedule, LiveOccurrence? Live);

    /// <summary>The columns of an RSVP option that decide which one is the attending option.</summary>
    /// <param name="EventId">The event the option belongs to.</param>
    /// <param name="Id">The option's id.</param>
    /// <param name="IsAttending">The option's attending flag.</param>
    /// <param name="SortOrder">The option's sort order.</param>
    private sealed record OptionRow(Guid EventId, Guid Id, bool IsAttending, int SortOrder);
}
