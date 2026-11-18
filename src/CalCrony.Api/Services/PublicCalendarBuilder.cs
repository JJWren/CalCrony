using CalCrony.Api.Data;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Assembles one month of a server's public calendar. Concrete event rows are history and
/// the present; the future of each running series is projected from its schedule so the grid
/// shows every upcoming occurrence, not just the one that has been posted (the ICS feed makes the
/// same "history is concrete, the future is projected" choice via RRULE). Pure — no I/O — so the
/// projection rules are directly testable.</summary>
public static class PublicCalendarBuilder
{
    /// <summary>Hard stop on schedule stepping per series, so a far-future month against a daily
    /// series can't loop unboundedly (a two-year hop is ~730 steps).</summary>
    public const int MaxStepsPerSeries = 1000;

    /// <summary>How far from the server's current month the view may wander, either way — bounds
    /// the per-request schedule stepping and stops the route doubling as a bulk history export.</summary>
    public const int MaxMonthsFromNow = 24;

    /// <summary>Builds the month view.</summary>
    /// <param name="guildName">The server-name snapshot, or null.</param>
    /// <param name="zone">The server's zone — defines the month window and the local wall times.</param>
    /// <param name="year">The month's year.</param>
    /// <param name="month">The month (1-12).</param>
    /// <param name="events">The guild's non-cancelled events whose start falls inside the month.</param>
    /// <param name="series">The guild's running (non-ended) series.</param>
    /// <param name="channelNames">Channel-name snapshots by channel id (missing = omit the name).</param>
    /// <param name="now">The current instant — projections are strictly in the future.</param>
    /// <param name="liveOccurrences">The live (scheduled or started) occurrence of each running
    /// series, in whatever month it lies: a series posts nothing more before that one has ended.</param>
    /// <returns>The month DTO with entries in start order.</returns>
    public static PublicCalendarDto Build(
        string? guildName,
        DateTimeZone zone,
        int year,
        int month,
        IReadOnlyList<Event> events,
        IReadOnlyList<EventSeries> series,
        IReadOnlyDictionary<long, string> channelNames,
        Instant now,
        IReadOnlyList<LiveOccurrence> liveOccurrences)
    {
        var (windowStart, windowEnd) = MonthWindow(zone, year, month);
        var entries = new List<PublicCalendarEventDto>();

        // Concrete rows: the month's events, the posted occurrences of series among them.
        var concreteStartsBySeries = new Dictionary<Guid, HashSet<Instant>>();
        foreach (var ev in events)
        {
            entries.Add(Entry(
                ev.Title, ev.StartsAt, ev.DurationMinutes, ev.Location,
                channelNames.GetValueOrDefault(ev.ChannelId), DiscordUrl(ev), projected: false, zone));
            if (ev.SeriesId is { } seriesId)
            {
                if (!concreteStartsBySeries.TryGetValue(seriesId, out var starts))
                {
                    starts = [];
                    concreteStartsBySeries[seriesId] = starts;
                }

                starts.Add(ev.StartsAt);
            }
        }

        // When each series' live occurrence ends, in whatever month it lies: the series posts
        // nothing more before then. The database keeps a series to one live occurrence
        // (IX_Events_SeriesId_Live); were there two, the later end would stand, as in the feed.
        // A live occurrence that starts at or after the month's end leaves its series nothing
        // to show in the month: the series is passed over, and that occurrence's end is not
        // worked out.
        var liveEnds = new Dictionary<Guid, Instant>();
        var liveAfterMonth = new HashSet<Guid>();
        foreach (var live in liveOccurrences)
        {
            if (live.StartsAt >= windowEnd)
            {
                liveAfterMonth.Add(live.SeriesId);
                continue;
            }

            var end = SeriesProjection.EndOf(live.StartsAt, live.DurationMinutes);
            if (!liveEnds.TryGetValue(live.SeriesId, out var seen) || end > seen)
            {
                liveEnds[live.SeriesId] = end;
            }
        }

        foreach (var s in series)
        {
            if (liveAfterMonth.Contains(s.Id))
            {
                continue;
            }

            Instant? liveEnd = liveEnds.TryGetValue(s.Id, out var end) ? end : null;
            var concrete = concreteStartsBySeries.GetValueOrDefault(s.Id);
            foreach (var instant in ProjectOccurrences(s, windowStart, windowEnd, now, liveEnd))
            {
                // Belt and braces against a slot that is also a concrete row of the series. The
                // slots start after the live occurrence has ended, so it is never that one; the
                // guard stays for any other row.
                if (concrete is not null && concrete.Contains(instant))
                {
                    continue;
                }

                entries.Add(Entry(
                    s.Title, instant, s.DurationMinutes, s.Location,
                    channelNames.GetValueOrDefault(s.ChannelId), discordUrl: null, projected: true, zone));
            }
        }

        var thisMonth = now.InZone(zone).Date.With(DateAdjusters.StartOfMonth);
        return new PublicCalendarDto(
            guildName, zone.Id, year, month, [.. entries.OrderBy(e => e.StartsAtUtc)],
            thisMonth.PlusMonths(-MaxMonthsFromNow).ToDateTimeUnspecified(),
            thisMonth.PlusMonths(MaxMonthsFromNow).ToDateTimeUnspecified());
    }

    /// <summary>Whether a requested month lies within <see cref="MaxMonthsFromNow"/> of the current
    /// one. Computed in 64-bit so a crafted year can't wrap back into range and then blow up the
    /// window math.</summary>
    /// <param name="year">The requested year.</param>
    /// <param name="month">The requested month (1-12).</param>
    /// <param name="today">Today's date in the server's zone.</param>
    /// <returns>True when the month may be served.</returns>
    public static bool IsMonthInRange(int year, int month, LocalDate today) =>
        month is >= 1 and <= 12
        && Math.Abs(((long)year - today.Year) * 12 + (month - today.Month)) <= MaxMonthsFromNow;

    /// <summary>The [start, end) instant window of a calendar month in a zone.</summary>
    /// <param name="zone">The zone.</param>
    /// <param name="year">The year.</param>
    /// <param name="month">The month (1-12).</param>
    /// <returns>The window bounds.</returns>
    public static (Instant Start, Instant End) MonthWindow(DateTimeZone zone, int year, int month)
    {
        var first = new LocalDate(year, month, 1);
        return (first.AtStartOfDayInZone(zone).ToInstant(), first.PlusMonths(1).AtStartOfDayInZone(zone).ToInstant());
    }

    /// <summary>The occurrences a series goes on to post that start inside the window
    /// (<see cref="SeriesProjection.Upcoming"/>): the schedule is stepped on past the series' slot
    /// cursor (the last materialized occurrence, so the live one is never repeated), starting
    /// after the end of its live occurrence when that is later than now, honoring the weekly day
    /// set, the until-date and the remaining count, under <see cref="MaxStepsPerSeries"/>.
    /// Occurrences before the window still use up the count — they are posted first.</summary>
    /// <param name="series">The running series.</param>
    /// <param name="windowStart">Inclusive window start.</param>
    /// <param name="windowEnd">Exclusive window end.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="liveEnd">When the series' live occurrence ends; null when it has none.</param>
    /// <returns>Occurrence start instants inside the window, ascending.</returns>
    public static IEnumerable<Instant> ProjectOccurrences(
        EventSeries series, Instant windowStart, Instant windowEnd, Instant now, Instant? liveEnd)
    {
        var freeFrom = SeriesProjection.FreeFrom(now, liveEnd);
        if (freeFrom >= windowEnd)
        {
            yield break;
        }

        foreach (var instant in SeriesProjection.Upcoming(series, freeFrom, MaxStepsPerSeries))
        {
            if (instant >= windowEnd)
            {
                yield break;
            }

            if (instant >= windowStart)
            {
                yield return instant;
            }
        }
    }

    private static PublicCalendarEventDto Entry(
        string title, Instant startsAt, int? durationMinutes, string? location, string? channelName,
        string? discordUrl, bool projected, DateTimeZone zone) =>
        new(
            title,
            startsAt.ToDateTimeOffset(),
            startsAt.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
            durationMinutes,
            location,
            channelName,
            discordUrl,
            projected);

    private static string? DiscordUrl(Event ev) =>
        ev.MessageId is { } messageId
            ? $"https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/{messageId}"
            : null;
}
