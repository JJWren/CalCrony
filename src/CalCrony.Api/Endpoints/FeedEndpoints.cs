using System.Security.Cryptography;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>ICS calendar feed: per-guild tokenized subscribe URLs (the token is the credential — the feed route is anonymous).</summary>
public static class FeedEndpoints
{
    /// <summary>Maps feed-token and feed routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        // Authenticated (bot, or a web member of the guild): mints/returns the guild's feed token.
        app.MapPost("/guilds/{guildId:long}/feed-token", GetOrCreateToken);

        // Anonymous by design — the unguessable token IS the credential.
        app.MapGet("/feeds/{token}.ics", GetFeed).AllowAnonymous();
    }

    /// <summary>Returns the guild's feed token, minting one on first use.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetOrCreateToken(
        HttpContext context, GuildAccessService access, long guildId, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);

        var existing = await db.IcsFeedTokens.FirstOrDefaultAsync(t => t.GuildId == guildId, cancellationToken);
        if (existing is null)
        {
            existing = new IcsFeedToken
            {
                Id = Guid.NewGuid(),
                GuildId = guildId,
                Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)),
                CreatedAt = clock.GetCurrentInstant(),
            };
            db.IcsFeedTokens.Add(existing);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new FeedTokenDto(existing.Token, $"/feeds/{existing.Token}.ics"));
    }

    /// <summary>Serves the iCalendar document: the last 30 days plus upcoming, excluding cancelled
    /// occurrences. History is concrete; the future is projected — each non-ended series emits one
    /// RRULE-bearing VEVENT (stable series UID) anchored on its live occurrence, which is skipped
    /// in the concrete loop so nothing doubles.</summary>
    /// <param name="token">The token value.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetFeed(
        string token, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var feedToken = await db.IcsFeedTokens.FirstOrDefaultAsync(t => t.Token == token, cancellationToken);
        if (feedToken is null)
        {
            return Results.NotFound();
        }

        // Include a month of history so recently finished events don't vanish from subscribers.
        var now = clock.GetCurrentInstant();
        var horizon = now.Minus(NodaTime.Duration.FromDays(30));
        var events = await db.Events
            .Include(e => e.Series)
            .Where(e => e.GuildId == feedToken.GuildId
                        && e.Status != EventStatus.Cancelled
                        && e.StartsAt >= horizon)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(cancellationToken);

        var calendar = new Ical.Net.Calendar();
        calendar.AddProperty("X-WR-CALNAME", "CalCrony events");
        calendar.AddProperty("METHOD", "PUBLISH");

        foreach (var ev in events)
        {
            if (IsLiveSeriesOccurrence(ev))
            {
                // Represented by its series' RRULE VEVENT below — emitting both would double it.
                continue;
            }

            var start = ev.StartsAt.ToDateTimeUtc();
            calendar.Events.Add(new CalendarEvent
            {
                Uid = $"{ev.Id}@calcrony",
                Summary = ev.Title,
                Description = ev.Description,
                Location = ev.Location,
                DtStart = new CalDateTime(start),
                DtEnd = new CalDateTime(start.AddMinutes(ev.DurationMinutes ?? 60)),
                DtStamp = new CalDateTime(ev.CreatedAt.ToDateTimeUtc()),
            });
        }

        // One RRULE VEVENT per running series, anchored on its live occurrence.
        var liveBySeries = events
            .Where(IsLiveSeriesOccurrence)
            .ToDictionary(e => e.SeriesId!.Value);
        foreach (var (seriesId, live) in liveBySeries)
        {
            AddSeriesEvent(
                calendar, live.Series!, live.StartsAt, anchorIsCounted: true,
                live.Title, live.Description, live.Location, live.DurationMinutes);
        }

        // A series can briefly lack a live occurrence (between an end/skip and the sweep's next
        // spawn) — project it from the computed next slot so it never vanishes from the feed.
        var gapSeries = await db.EventSeries
            .Where(s => s.GuildId == feedToken.GuildId && !s.Ended)
            .ToListAsync(cancellationToken);
        foreach (var series in gapSeries.Where(s => !liveBySeries.ContainsKey(s.Id)))
        {
            var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
            var next = Services.RecurrenceCalculator.NextOccurrence(
                series.Unit, series.Interval, series.MonthlyMode, series.AnchorDate,
                series.StartTime, zone, series.CurrentOccurrenceDate, series.UntilDate, now);
            if (next is null)
            {
                continue; // end condition about to retire the series
            }

            AddSeriesEvent(
                calendar, series, next.Value.Instant, anchorIsCounted: false,
                series.Title, series.Description, series.Location, series.DurationMinutes);
        }

        var text = new CalendarSerializer().SerializeToString(calendar);
        return Results.Text(text, "text/calendar; charset=utf-8");
    }

    /// <summary>Whether the event is the live occurrence of a running series (the one the series'
    /// RRULE VEVENT anchors on).</summary>
    /// <param name="ev">The event (Series navigation loaded).</param>
    /// <returns>True for live occurrences of non-ended series.</returns>
    private static bool IsLiveSeriesOccurrence(Event ev) =>
        ev is { SeriesId: not null, Series.Ended: false, Status: EventStatus.Scheduled or EventStatus.Started };

    /// <summary>Adds the RRULE-bearing VEVENT representing a running series.</summary>
    /// <param name="calendar">The calendar under construction.</param>
    /// <param name="series">The series row.</param>
    /// <param name="startsAt">The DTSTART instant (live occurrence start, or the computed next slot).</param>
    /// <param name="anchorIsCounted">Whether DTSTART is an already-counted occurrence (shifts COUNT math).</param>
    /// <param name="title">The event title.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="location">Optional location text.</param>
    /// <param name="durationMinutes">Duration in minutes.</param>
    private static void AddSeriesEvent(
        Ical.Net.Calendar calendar, EventSeries series, Instant startsAt, bool anchorIsCounted,
        string title, string? description, string? location, int? durationMinutes)
    {
        var start = startsAt.ToDateTimeUtc();
        var vevent = new CalendarEvent
        {
            Uid = $"{series.Id}@calcrony",
            Summary = title,
            Description = description,
            Location = location,
            DtStart = new CalDateTime(start),
            DtEnd = new CalDateTime(start.AddMinutes(durationMinutes ?? 60)),
            DtStamp = new CalDateTime(series.CreatedAt.ToDateTimeUtc()),
        };
        vevent.RecurrenceRule = Services.IcsRecurrence.BuildPattern(series, anchorIsCounted);
        calendar.Events.Add(vevent);
    }
}
