using System.Linq.Expressions;
using System.Text;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Endpoints;

/// <summary>The server action log (who did what, newest first, filterable) and the events CSV
/// export — both manager-only for web callers, both free for every server (sesh gates them
/// behind premium and its dashboard). The bot passes the guard like everywhere else but has no
/// command for either yet (issue #124 defers <c>/logs</c>).</summary>
public static class ActionLogEndpoints
{
    /// <summary>Page size when the caller names none.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Hard cap per page — the web page loads more on demand.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Events per round-trip while streaming the export. Bounds memory to one chunk of
    /// projected rows (plus their RSVPs) regardless of how much history a guild has kept.</summary>
    public const int ExportChunkSize = 200;

    private const string CursorSeparator = "|";

    /// <summary>Maps the action log and export routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapActionLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/guilds/{guildId:long}/actions", ListActions);
        app.MapGet("/guilds/{guildId:long}/export/events.csv", ExportEventsCsv);
    }

    /// <summary>Lists a guild's action log newest first with keyset paging. <c>before</c> is the
    /// opaque <c>NextCursor</c> from the previous page (created-at plus id, so entries written in
    /// the same instant never repeat or vanish between pages); <c>action</c> and <c>userId</c>
    /// filter, and a bad action name or cursor is a friendly 400. Each entry says whether its
    /// target still exists, so clients only link to pages that will actually load.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="action">Optional <see cref="ActionLogAction"/> name to filter by.</param>
    /// <param name="userId">Optional actor Discord id to filter by.</param>
    /// <param name="before">Opaque cursor from a previous page's NextCursor.</param>
    /// <param name="limit">Maximum number of rows to return (1-100).</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ListActions(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CalCronyDbContext db,
        CancellationToken cancellationToken,
        string? action = null,
        long? userId = null,
        string? before = null,
        int limit = DefaultPageSize)
    {
        if (await EventEndpoints.GuardGuildManageAsync(
                context, access, guildId, cancellationToken,
                "Only server managers can view the activity log.") is { } denied)
        {
            return denied;
        }

        ActionLogAction? actionFilter = null;
        if (!string.IsNullOrWhiteSpace(action))
        {
            if (!Enum.TryParse<ActionLogAction>(action, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Results.BadRequest(new ErrorResponse(
                    $"Unknown action \"{action}\". Valid actions: {string.Join(", ", Enum.GetNames<ActionLogAction>())}."));
            }

            actionFilter = parsed;
        }

        Instant? cursorAt = null;
        Guid cursorId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(before))
        {
            if (!TryParseCursor(before, out var at, out cursorId))
            {
                return Results.BadRequest(new ErrorResponse("That paging cursor isn't valid — reload the log from the start."));
            }

            cursorAt = at;
        }

        limit = Math.Clamp(limit, 1, MaxPageSize);
        var query = db.ActionLogEntries.AsNoTracking().Where(a => a.GuildId == guildId);
        if (actionFilter is { } filterAction)
        {
            query = query.Where(a => a.Action == filterAction);
        }

        if (userId is { } filterUser)
        {
            query = query.Where(a => a.ActorUserId == filterUser);
        }

        if (cursorAt is { } at2)
        {
            // Keyset on (CreatedAt desc, Id desc): strictly older, or same instant with a smaller
            // id — the same total order the sort below uses, so pages never overlap or skip.
            query = query.Where(a => a.CreatedAt < at2 || (a.CreatedAt == at2 && a.Id.CompareTo(cursorId) < 0));
        }

        // One extra row tells us whether another page exists without a second count query.
        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        // Actor names come from the web-login snapshot (UserProfile.Username) — the only name
        // source the API has for users (ADR 0001); users who never signed in show as their id.
        var actorIds = rows.Where(r => r.ActorUserId is not null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var names = actorIds.Count == 0
            ? []
            : await db.UserProfiles
                .Where(u => actorIds.Contains(u.Id) && u.Username != null)
                .ToDictionaryAsync(u => u.Id, u => u.Username!, cancellationToken);

        // Target existence is resolved now, not at write time: a "created" entry outlives its
        // event, and only the current state says whether the event's page would 404.
        var existing = new HashSet<Guid>();
        existing.UnionWith(await ExistingIdsAsync(db.Events, e => e.Id, TargetIds(rows, ActionTargetType.Event), cancellationToken));
        existing.UnionWith(await ExistingIdsAsync(db.Polls, p => p.Id, TargetIds(rows, ActionTargetType.Poll), cancellationToken));
        existing.UnionWith(await ExistingIdsAsync(db.EventSeries, s => s.Id, TargetIds(rows, ActionTargetType.Series), cancellationToken));
        existing.UnionWith(await ExistingIdsAsync(db.EventTemplates, t => t.Id, TargetIds(rows, ActionTargetType.Template), cancellationToken));
        existing.UnionWith(await ExistingIdsAsync(db.LiveLists, l => l.Id, TargetIds(rows, ActionTargetType.LiveList), cancellationToken));

        var entries = rows.Select(r => new ActionLogEntryDto(
            r.Id,
            r.GuildId,
            r.ActorUserId,
            r.ActorUserId is { } actorId ? names.GetValueOrDefault(actorId) : null,
            r.Source,
            r.Action,
            r.TargetType,
            r.TargetId,
            // Guild-level entries target the guild itself, which is by definition still here.
            r.TargetType == ActionTargetType.Guild || (r.TargetId is { } targetId && existing.Contains(targetId)),
            r.Summary,
            r.DetailsJson,
            r.CreatedAt.ToDateTimeOffset())).ToList();

        return Results.Ok(new ActionLogPageDto(entries, hasMore ? FormatCursor(rows[^1]) : null));
    }

    /// <summary>Streams every event the guild still has (retention already bounds what is kept,
    /// so there is no separate window) with one row per RSVP — see CsvExport for the row model.
    /// Events are walked in <see cref="ExportChunkSize"/> keyset chunks by id, each chunk's RSVPs
    /// fetched in one joined query, and rows are flushed to the response as they are written, so
    /// memory never holds more than one chunk. The download itself is logged: exporting attendee
    /// data is a management action a server's other managers should be able to see.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="chunkHook">The test seam invoked after each chunk is flushed.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ExportEventsCsv(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CalCronyDbContext db,
        IClock clock,
        ExportChunkHook chunkHook,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildManageAsync(
                context, access, guildId, cancellationToken,
                "Only server managers can export events.") is { } denied)
        {
            return denied;
        }

        // The entry commits before the first byte streams: a download that is interrupted
        // half-way still exposed attendee data, so it is still worth recording.
        var now = clock.GetCurrentInstant();
        var eventCount = await db.Events.CountAsync(e => e.GuildId == guildId, cancellationToken);
        ActionLog.Record(
            db, guildId, ActionLog.ActorFor(context), ActionLogAction.EventsExported, ActionTargetType.Guild, null,
            $"Exported the events CSV ({eventCount} events)", now);
        await db.SaveChangesAsync(cancellationToken);

        var fileName = $"calcrony-events-{guildId}-{now.InUtc().Date:yyyyMMdd}.csv";
        // Results.Stream sets Content-Disposition: attachment with the file name and hands us the
        // response body once headers are committed; the scoped DbContext outlives the callback
        // because result execution happens inside the request's scope.
        return Results.Stream(
            body => WriteExportAsync(db, guildId, body, chunkHook, context.RequestAborted),
            "text/csv; charset=utf-8",
            fileName);
    }

    /// <summary>Writes the BOM, header, and every event's rows to the response body in keyset
    /// chunks ordered by event id. The id is the only key that cannot change under a running
    /// export — a start time edited mid-stream would move an event across a StartsAt cursor and
    /// export it twice or not at all — so rows come out in id order (see CsvExport: sort by
    /// <c>starts_at_utc</c> in the spreadsheet) and each event appears exactly once.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="body">The response body.</param>
    /// <param name="chunkHook">The test seam invoked after each chunk is flushed.</param>
    /// <param name="cancellationToken">Cancels when the client goes away.</param>
    private static async Task WriteExportAsync(
        CalCronyDbContext db, long guildId, Stream body, ExportChunkHook chunkHook, CancellationToken cancellationToken)
    {
        await body.WriteAsync(CsvExport.Utf8Bom, cancellationToken);

        // Rows for one chunk are composed in memory and pushed with a single async write: a
        // StreamWriter over the body would flush its internal buffer synchronously mid-row,
        // which Kestrel rejects (AllowSynchronousIO is off). The BOM went out by hand above, so
        // the encoder must not emit another.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var writer = new StringWriter();
        CsvExport.WriteHeader(writer);

        async Task FlushChunkAsync()
        {
            var builder = writer.GetStringBuilder();
            await body.WriteAsync(utf8.GetBytes(builder.ToString()), cancellationToken);
            await body.FlushAsync(cancellationToken);
            builder.Clear();
        }

        Guid? afterId = null;
        var chunkIndex = 0;
        while (true)
        {
            var query = db.Events.AsNoTracking().Where(e => e.GuildId == guildId);
            if (afterId is { } last)
            {
                // Keyset on the immutable Id: an event edited while the export runs stays put in
                // this order, so chunks never overlap or skip.
                query = query.Where(e => e.Id.CompareTo(last) > 0);
            }

            var chunk = await query
                .OrderBy(e => e.Id)
                .Take(ExportChunkSize)
                .Select(e => new CsvExport.EventRow(
                    e.Id, e.Title, e.StartsAt, e.TimeZone, e.DurationMinutes, e.Location, e.Status, e.SeriesId, e.ChannelId, e.CreatorId))
                .ToListAsync(cancellationToken);
            if (chunk.Count == 0)
            {
                break;
            }

            // One joined query for the chunk's RSVPs (no cartesian Include): an RSVP always
            // references one of its event's options, so the inner join drops nothing. (The record
            // is constructed only in the final Select — EF can't translate a constructor call
            // that an OrderBy still has to see through.)
            var ids = chunk.Select(e => e.Id).ToList();
            var rsvps = await db.Rsvps.AsNoTracking()
                .Where(r => ids.Contains(r.EventId))
                .Join(db.RsvpOptions, r => r.OptionId, o => o.Id, (r, o) => new { Rsvp = r, Option = o })
                .OrderBy(x => x.Rsvp.CreatedAt)
                .ThenBy(x => x.Rsvp.UserId)
                .Select(x => new CsvExport.RsvpRow(
                    x.Rsvp.EventId, x.Option.Emote, x.Option.Label, x.Option.IsAttending,
                    x.Rsvp.UserId, x.Rsvp.Waitlisted, x.Rsvp.CreatedAt))
                .ToListAsync(cancellationToken);
            var byEvent = rsvps.ToLookup(r => r.EventId);

            foreach (var ev in chunk)
            {
                CsvExport.WriteEvent(writer, ev, byEvent[ev.Id].ToList());
            }

            await FlushChunkAsync();
            if (chunkHook.AfterChunkFlushed is { } afterChunk)
            {
                await afterChunk(chunkIndex, cancellationToken);
            }

            chunkIndex++;
            if (chunk.Count < ExportChunkSize)
            {
                break;
            }

            afterId = chunk[^1].Id;
        }

        // A guild with no events still gets its header row.
        await FlushChunkAsync();
    }

    /// <summary>The target ids of the rows with the given target type.</summary>
    private static List<Guid> TargetIds(IEnumerable<ActionLogEntry> rows, ActionTargetType type) =>
        [.. rows.Where(r => r.TargetType == type && r.TargetId is not null).Select(r => r.TargetId!.Value).Distinct()];

    /// <summary>Which of the given ids still exist in a table; skips the query when there are none.</summary>
    private static async Task<List<Guid>> ExistingIdsAsync<T>(
        IQueryable<T> set, Expression<Func<T, Guid>> id, List<Guid> ids, CancellationToken cancellationToken)
        where T : class =>
        ids.Count == 0 ? [] : await set.Select(id).Where(x => ids.Contains(x)).ToListAsync(cancellationToken);

    /// <summary>Encodes a row's (CreatedAt, Id) as the opaque paging cursor.</summary>
    /// <param name="entry">The last entry of the page.</param>
    /// <returns>The cursor text.</returns>
    internal static string FormatCursor(ActionLogEntry entry) =>
        $"{InstantPattern.ExtendedIso.Format(entry.CreatedAt)}{CursorSeparator}{entry.Id:N}";

    /// <summary>Decodes a cursor produced by <see cref="FormatCursor"/>.</summary>
    /// <param name="cursor">The cursor text.</param>
    /// <param name="createdAt">The decoded instant.</param>
    /// <param name="id">The decoded id.</param>
    /// <returns>True when the cursor was well-formed.</returns>
    internal static bool TryParseCursor(string cursor, out Instant createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        var parts = cursor.Split(CursorSeparator, 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var parsed = InstantPattern.ExtendedIso.Parse(parts[0]);
        if (!parsed.Success || !Guid.TryParseExact(parts[1], "N", out id))
        {
            return false;
        }

        createdAt = parsed.Value;
        return true;
    }
}
