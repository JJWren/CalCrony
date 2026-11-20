using System.Globalization;
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
    /// <summary>How many days ahead a series that is listed entry by entry is listed: a year, with
    /// a day more for a leap year.</summary>
    public const int EntryByEntryDays = 366;

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
    /// anchored on the first date it will post (<see cref="Services.SeriesProjection"/>), so the
    /// two never overlap. A series posts only the dates that begin after the occurrence before has
    /// ended. One whose occurrences last as long as the gap between two of its dates
    /// (<see cref="Services.SeriesProjection.LastsItsGap"/>) holds every second date or more back,
    /// which its repeat rule does not name: it emits one VEVENT for each occurrence it will post
    /// instead, <see cref="EntryByEntryDays"/> ahead. One that a clock change brings to its gap
    /// (<see cref="Services.SeriesProjection.CanHoldDatesBack"/>) keeps its RRULE, with the dates
    /// it holds back within that time as EXDATEs.</summary>
    /// <param name="token">The token value.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="configuration">The app configuration (for the web origin).</param>
    /// <param name="logger">Logs a series whose rule cannot be stepped.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetFeed(
        string token, CalCronyDbContext db, IClock clock, IConfiguration configuration, ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var feedToken = await db.IcsFeedTokens.FirstOrDefaultAsync(t => t.Token == token, cancellationToken);
        if (feedToken is null)
        {
            return Results.NotFound();
        }

        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == feedToken.GuildId, cancellationToken);

        // A malformed Web:Origin degrades to no links on this anonymous endpoint (see WebOrigin).
        var webOrigin = Services.WebOrigin.Resolve(configuration) ?? "";

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
                DtEnd = new CalDateTime(Services.EventTimes.End(ev.StartsAt, ev.DurationMinutes).ToDateTimeUtc()),
                DtStamp = new CalDateTime(ev.CreatedAt.ToDateTimeUtc()),
            });
        }

        // One RRULE VEVENT per running series (one VEVENT per occurrence for a series as long as its
        // gap, below), anchored on the calculator's next slot after the cursor — never on the live
        // occurrence. An RRULE's phase is only expressible through DTSTART, and a live occurrence
        // can sit off the CURRENT grid after a rule edit (every 2 weeks → every 3 leaves a week-2
        // live row in place while the engine's next slot is week 3; a day-set edit or a
        // whole-series re-anchor shifts the grid the same way). Recomputing
        // DTSTART from the stored rule at feed time keeps the feed on the engine's grid for every
        // edit sequence, and covers the brief gap between a skip/end and the sweep's next spawn.
        // Series VEVENTs link to the guild's events list — the live occurrence (and its Discord
        // message) rotates every cycle, so nothing durable may point at it.
        // The live occurrence also pins the projection FLOOR. The scheduler spawns the next
        // occurrence only once the live one ends, and an Occurrence-scoped time edit can move the
        // live row onto (or past) later nominal slots without touching the series cursor —
        // projecting from the cursor alone would then double the moved date and list intermediate
        // slots that can never materialize. Projecting from the live row's end reproduces exactly
        // what the end sweep will compute (same length default as the scheduler's), while the
        // cursor itself stays the rule's nominal position.
        var liveEndBySeries = events
            .Where(e => e.SeriesId is not null && e.Status is EventStatus.Scheduled or EventStatus.Started)
            .GroupBy(e => e.SeriesId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Max(e => Services.EventTimes.End(e.StartsAt, e.DurationMinutes)));

        // The dates come from SeriesProjection, as the public calendar's do: the first is the first
        // date after the cursor that begins once the live occurrence has ended (or after now), and
        // the helper applies the count, which NextOccurrence knows nothing about — a
        // count-exhausted series awaiting its Ended sweep projects nothing, and neither does one
        // whose end condition is about to retire it.
        var entryByEntryUntil = now + NodaTime.Duration.FromDays(EntryByEntryDays);
        foreach (var series in runningSeries)
        {
            // Each series is worked out on its own, as the scheduler advances each on its own: one
            // whose rule cannot be stepped is logged and its upcoming dates are left out, rather
            // than failing the whole feed.
            try
            {
                AddUpcoming(calendar, series, liveEndBySeries, now, entryByEntryUntil, channelNames, guildEventsUrl);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Could not step series {SeriesId} in guild {GuildId} for the feed; leaving its upcoming dates out.",
                    series.Id,
                    series.GuildId);
            }
        }

        var text = new CalendarSerializer().SerializeToString(calendar);
        return Results.Text(text, "text/calendar; charset=utf-8");
    }

    /// <summary>Adds what the feed lists of a running series' upcoming dates: one VEVENT with the
    /// RRULE, anchored on the first date it will post, with the dates it holds back within
    /// <paramref name="entryByEntryUntil"/> as EXDATEs; or, for a series whose occurrences last
    /// as long as the gap between two of its dates, one VEVENT for each occurrence it will post
    /// until then (see <see cref="GetFeed"/>).</summary>
    /// <param name="calendar">The calendar under construction.</param>
    /// <param name="series">The running series.</param>
    /// <param name="liveEndBySeries">When each series' live occurrence ends.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="entryByEntryUntil">How far ahead dates are listed one by one, or held back.</param>
    /// <param name="channelNames">Channel-name snapshots by channel id.</param>
    /// <param name="guildEventsUrl">The server's events page, or null when Web:Origin isn't configured.</param>
    private static void AddUpcoming(
        Ical.Net.Calendar calendar, EventSeries series, IReadOnlyDictionary<Guid, Instant> liveEndBySeries, Instant now,
        Instant entryByEntryUntil, IReadOnlyDictionary<long, string> channelNames, string? guildEventsUrl)
    {
        var description = BuildDescription(
            series.Description, channelNames.GetValueOrDefault(series.ChannelId), "Events page", guildEventsUrl, discordUrl: null);
        Instant? liveEnd = liveEndBySeries.TryGetValue(series.Id, out var end) ? end : null;
        var freeFrom = Services.SeriesProjection.FreeFrom(now, liveEnd);

        // A series whose occurrences last as long as the gap between two of its dates holds
        // dates back all the time — every second date of a daily series a day long — which its
        // repeat rule does not name: it is listed entry by entry, as far as EntryByEntryDays ahead.
        // Such a length is refused now, so only a series made before can have one.
        // The dates are worked out before anything is added, so a series that cannot be stepped
        // adds nothing.
        if (Services.SeriesProjection.LastsItsGap(series))
        {
            var starts = Services.SeriesProjection
                .Upcoming(series, freeFrom, Services.PublicCalendarBuilder.MaxStepsPerSeries)
                .TakeWhile(startsAt => startsAt < entryByEntryUntil)
                .ToList();
            foreach (var startsAt in starts)
            {
                AddOccurrenceEvent(calendar, series, startsAt, description, guildEventsUrl);
            }

            return;
        }

        // Any other series keeps its one entry with the repeat rule. One that a clock change
        // brings to its gap holds back the date after a night the clocks go forward; those
        // dates, as far as EntryByEntryDays ahead, are written into the entry as exceptions.
        Services.HeldBack? heldBack = null;
        if (Services.SeriesProjection.CanHoldDatesBack(series, now, entryByEntryUntil))
        {
            heldBack = Services.SeriesProjection.HeldBackDates(
                series, freeFrom, entryByEntryUntil, Services.PublicCalendarBuilder.MaxStepsPerSeries);
        }

        foreach (var first in Services.SeriesProjection.Upcoming(series, freeFrom, maxSteps: 1).ToList())
        {
            AddSeriesEvent(
                calendar, series, first, series.Title, description, series.Location, series.DurationMinutes, guildEventsUrl,
                heldBack is { Dates.Count: > 0 } ? heldBack : null);
        }
    }

    /// <summary>Adds the RRULE-bearing VEVENT representing a running series. DTSTART/DTEND are
    /// emitted in the series' IANA zone (TZID) — an RRULE projects from DTSTART's wall time, so a
    /// UTC anchor would make subscribers' occurrences drift an hour across DST transitions while
    /// RecurrenceCalculator keeps the local wall time stable.</summary>
    /// <param name="calendar">The calendar under construction.</param>
    /// <param name="series">The series row.</param>
    /// <param name="startsAt">The DTSTART instant: the first date the series will post.</param>
    /// <param name="title">The event title.</param>
    /// <param name="description">Optional description text (metadata block already applied).</param>
    /// <param name="location">Optional location text.</param>
    /// <param name="durationMinutes">Duration in minutes.</param>
    /// <param name="url">Optional web URL for the ICS URL property.</param>
    /// <param name="heldBack">The dates the series will not post because the occurrence before is
    /// still running then (<see cref="Services.SeriesProjection.HeldBackDates"/>), written as
    /// exceptions in the form of DTSTART; null when there are none.</param>
    private static void AddSeriesEvent(
        Ical.Net.Calendar calendar, EventSeries series, Instant startsAt,
        string title, string? description, string? location, int? durationMinutes, string? url,
        Services.HeldBack? heldBack = null)
    {
        var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
        CalDateTime dtStart;
        CalDateTime dtEnd;
        if (zone == DateTimeZone.Utc)
        {
            dtStart = new CalDateTime(startsAt.ToDateTimeUtc());
            dtEnd = new CalDateTime(Services.EventTimes.End(startsAt, durationMinutes).ToDateTimeUtc());
        }
        else
        {
            var startLocal = startsAt.InZone(zone).LocalDateTime;
            var endLocal = Services.EventTimes.End(startLocal, durationMinutes);
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
        if (heldBack is not null)
        {
            // A COUNT counts the dates an exception leaves out as well. So when the series' count is
            // used up within the dates looked at, the rule ends on the last date it posts; otherwise
            // its count is raised by the dates left out.
            if (heldBack.LastPosted is { } lastPosted)
            {
                vevent.RecurrenceRule.Count = null;
                vevent.RecurrenceRule.Until = new CalDateTime(lastPosted.ToDateTimeUtc());
            }
            else if (vevent.RecurrenceRule.Count is int count)
            {
                vevent.RecurrenceRule.Count = (int)Math.Min((long)count + heldBack.Dates.Count, int.MaxValue);
            }

            foreach (var date in heldBack.Dates)
            {
                vevent.ExceptionDates.Add(zone == DateTimeZone.Utc
                    ? new CalDateTime(date.ToDateTimeUtc())
                    : new CalDateTime(date.InZone(zone).LocalDateTime.ToDateTimeUnspecified(), series.TimeZone));
            }
        }

        calendar.Events.Add(vevent);
    }

    /// <summary>Adds one occurrence of a series that is listed entry by entry: the series' title,
    /// place and description, its start written in UTC as a posted occurrence's is, and its end at
    /// <see cref="Services.EventTimes.End(Instant, int?)"/>. Its UID is the series' with the
    /// occurrence's date in the series' zone, so it stays the same from one read of the feed to
    /// the next and a calendar app updates the entry in place.</summary>
    /// <param name="calendar">The calendar under construction.</param>
    /// <param name="series">The series row.</param>
    /// <param name="startsAt">When the occurrence starts.</param>
    /// <param name="description">The description text (metadata block already applied).</param>
    /// <param name="url">Optional web URL for the ICS URL property.</param>
    private static void AddOccurrenceEvent(
        Ical.Net.Calendar calendar, EventSeries series, Instant startsAt, string? description, string? url)
    {
        var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
        var date = startsAt.InZone(zone).Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        calendar.Events.Add(new CalendarEvent
        {
            Uid = $"{series.Id}-{date}@calcrony",
            Summary = series.Title,
            Description = description,
            Location = series.Location,
            Url = url is null ? null : new Uri(url),
            DtStart = new CalDateTime(startsAt.ToDateTimeUtc()),
            DtEnd = new CalDateTime(Services.EventTimes.End(startsAt, series.DurationMinutes).ToDateTimeUtc()),
            DtStamp = new CalDateTime(series.CreatedAt.ToDateTimeUtc()),
        });
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
