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
    /// occurrences. Every stored row is concrete (the live occurrence included); the future is
    /// projected — each non-ended series emits one RRULE-bearing VEVENT (stable series UID)
    /// anchored on the calculator's next unspawned slot, so the two never overlap.</summary>
    /// <param name="token">The token value.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetFeed(
        string token, CalCronyDbContext db, IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var feedToken = await db.IcsFeedTokens.FirstOrDefaultAsync(t => t.Token == token, cancellationToken);
        if (feedToken is null)
        {
            return Results.NotFound();
        }

        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == feedToken.GuildId, cancellationToken);

        // A malformed Web:Origin (stray whitespace, not an http/https URL) degrades to no links
        // rather than letting new Uri(...) throw — or a non-web scheme leak — on this anonymous
        // endpoint.
        var webOrigin = (configuration["Web:Origin"] ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(webOrigin, UriKind.Absolute, out var originUri)
            || (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps))
        {
            webOrigin = "";
        }

        var guildEventsUrl = webOrigin.Length == 0 ? null : $"{webOrigin}/app/guilds/{feedToken.GuildId}/events";

        // Include a month of history so recently finished events don't vanish from subscribers.
        var now = clock.GetCurrentInstant();
        var horizon = now.Minus(NodaTime.Duration.FromDays(30));
        var events = await db.Events
            .Where(e => e.GuildId == feedToken.GuildId
                        && e.Status != EventStatus.Cancelled
                        && e.StartsAt >= horizon)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(cancellationToken);

        var calendar = new Ical.Net.Calendar();
        // The server name lives at the calendar level (a per-feed constant), not in every event.
        calendar.AddProperty("X-WR-CALNAME",
            guild?.Name is { Length: > 0 } guildName ? $"CalCrony · {guildName}" : "CalCrony events");
        calendar.AddProperty("METHOD", "PUBLISH");

        // Channel-name snapshots for every channel this feed will render; missing rows just
        // omit the channel line (names degrade gracefully — see docs/adr/0001).
        var runningSeries = await db.EventSeries
            .Where(s => s.GuildId == feedToken.GuildId && !s.Ended)
            .ToListAsync(cancellationToken);
        var channelIds = events.Select(e => e.ChannelId)
            .Concat(runningSeries.Select(s => s.ChannelId))
            .Distinct()
            .ToList();
        var channelNames = await db.Channels
            .Where(c => channelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        foreach (var ev in events)
        {
            // A running series' live occurrence is concrete too: it is a real materialized row
            // (possibly re-timed at Occurrence scope), and the series VEVENT below projects from
            // the engine's NEXT slot, so nothing doubles and no one-off time change leaks into
            // the projection.
            var eventUrl = webOrigin.Length == 0 ? null : $"{webOrigin}/app/events/{ev.Id}";
            var discordUrl = ev.MessageId is { } messageId
                ? $"https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/{messageId}"
                : null;
            var start = ev.StartsAt.ToDateTimeUtc();
            calendar.Events.Add(new CalendarEvent
            {
                Uid = $"{ev.Id}@calcrony",
                Summary = ev.Title,
                Description = BuildDescription(
                    ev.Description, channelNames.GetValueOrDefault(ev.ChannelId), "Event page", eventUrl, discordUrl),
                Location = ev.Location,
                Url = eventUrl is null ? null : new Uri(eventUrl),
                DtStart = new CalDateTime(start),
                DtEnd = new CalDateTime(start.AddMinutes(ev.DurationMinutes ?? 60)),
                DtStamp = new CalDateTime(ev.CreatedAt.ToDateTimeUtc()),
            });
        }

        // One RRULE VEVENT per running series, anchored on the calculator's next slot after the
        // cursor — never on the live occurrence. An RRULE's phase is only expressible through
        // DTSTART, and a live occurrence can sit off the CURRENT grid after a rule edit (every 2
        // weeks → every 3 leaves a week-2 live row in place while the engine's next slot is week
        // 3; a day-set edit or a whole-series re-anchor shifts the grid the same way). Recomputing
        // DTSTART from the stored rule at feed time keeps the feed on the engine's grid for every
        // edit sequence, and covers the brief gap between a skip/end and the sweep's next spawn.
        // Series VEVENTs link to the guild's events list — the live occurrence (and its Discord
        // message) rotates every cycle, so nothing durable may point at it.
        foreach (var series in runningSeries)
        {
            // NextOccurrence knows nothing about counts (the materializer enforces those), so a
            // count-exhausted series awaiting its Ended sweep must not project a phantom instance.
            if (series.MaxOccurrences is int max && series.OccurrenceCount >= max)
            {
                continue;
            }

            var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
            var next = Services.RecurrenceCalculator.NextOccurrence(
                series.Unit, series.Interval, series.MonthlyMode, series.AnchorDate,
                series.StartTime, zone, series.CurrentOccurrenceDate, series.UntilDate, now,
                series.DaysOfWeek);
            if (next is null)
            {
                continue; // end condition about to retire the series
            }

            AddSeriesEvent(
                calendar, series, next.Value.Instant,
                series.Title,
                BuildDescription(
                    series.Description, channelNames.GetValueOrDefault(series.ChannelId), "Events page", guildEventsUrl, discordUrl: null),
                series.Location, series.DurationMinutes, guildEventsUrl);
        }

        var text = new CalendarSerializer().SerializeToString(calendar);
        return Results.Text(text, "text/calendar; charset=utf-8");
    }

    /// <summary>Adds the RRULE-bearing VEVENT representing a running series. DTSTART/DTEND are
    /// emitted in the series' IANA zone (TZID) — an RRULE projects from DTSTART's wall time, so a
    /// UTC anchor would make subscribers' occurrences drift an hour across DST transitions while
    /// RecurrenceCalculator keeps the local wall time stable.</summary>
    /// <param name="calendar">The calendar under construction.</param>
    /// <param name="series">The series row.</param>
    /// <param name="startsAt">The DTSTART instant: the calculator's next unspawned slot.</param>
    /// <param name="title">The event title.</param>
    /// <param name="description">Optional description text (metadata block already applied).</param>
    /// <param name="location">Optional location text.</param>
    /// <param name="durationMinutes">Duration in minutes.</param>
    /// <param name="url">Optional web URL for the ICS URL property.</param>
    private static void AddSeriesEvent(
        Ical.Net.Calendar calendar, EventSeries series, Instant startsAt,
        string title, string? description, string? location, int? durationMinutes, string? url)
    {
        var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
        CalDateTime dtStart;
        CalDateTime dtEnd;
        if (zone == DateTimeZone.Utc)
        {
            var startUtc = startsAt.ToDateTimeUtc();
            dtStart = new CalDateTime(startUtc);
            dtEnd = new CalDateTime(startUtc.AddMinutes(durationMinutes ?? 60));
        }
        else
        {
            var startLocal = startsAt.InZone(zone).LocalDateTime;
            var endLocal = startLocal.PlusMinutes(durationMinutes ?? 60);
            dtStart = new CalDateTime(startLocal.ToDateTimeUnspecified(), series.TimeZone);
            dtEnd = new CalDateTime(endLocal.ToDateTimeUnspecified(), series.TimeZone);
            if (calendar.TimeZones.All(tz => tz.TzId != series.TimeZone))
            {
                calendar.AddTimeZone(new VTimeZone(series.TimeZone));
            }
        }

        var vevent = new CalendarEvent
        {
            Uid = $"{series.Id}@calcrony",
            Summary = title,
            Description = description,
            Location = location,
            Url = url is null ? null : new Uri(url),
            DtStart = dtStart,
            DtEnd = dtEnd,
            DtStamp = new CalDateTime(series.CreatedAt.ToDateTimeUtc()),
        };
        // DTSTART is an unspawned slot, so the remaining COUNT excludes every counted occurrence.
        vevent.RecurrenceRule = Services.IcsRecurrence.BuildPattern(series, anchorIsCounted: false);
        calendar.Events.Add(vevent);
    }

    /// <summary>Appends the Discord-context metadata block (channel, web link, Discord jump link)
    /// below the user's own description text. Every line is optional and missing pieces are
    /// simply omitted — with nothing to add, the user's text passes through untouched.</summary>
    /// <param name="text">The user-entered description.</param>
    /// <param name="channelName">The channel-name snapshot, or null when none is stored.</param>
    /// <param name="pageLabel">Label for the web link ("Event page" / "Events page").</param>
    /// <param name="pageUrl">The web link, or null when Web:Origin isn't configured.</param>
    /// <param name="discordUrl">The Discord message jump link, or null when there's no message.</param>
    /// <returns>The composed DESCRIPTION value.</returns>
    private static string? BuildDescription(
        string? text, string? channelName, string pageLabel, string? pageUrl, string? discordUrl)
    {
        var lines = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(channelName))
        {
            lines.Add($"📍 #{channelName}");
        }

        if (pageUrl is not null)
        {
            lines.Add($"🔗 {pageLabel}: {pageUrl}");
        }

        if (discordUrl is not null)
        {
            lines.Add($"💬 Open in Discord: {discordUrl}");
        }

        if (lines.Count == 0)
        {
            return text;
        }

        var block = string.Join('\n', lines);
        return string.IsNullOrWhiteSpace(text) ? block : $"{text}\n\n{block}";
    }
}
