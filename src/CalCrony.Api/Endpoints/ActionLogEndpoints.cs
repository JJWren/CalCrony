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
    /// filter, and a bad action name or cursor is a friendly 400.</summary>
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

        var entries = rows.Select(r => new ActionLogEntryDto(
            r.Id,
            r.GuildId,
            r.ActorUserId,
            r.ActorUserId is { } actorId ? names.GetValueOrDefault(actorId) : null,
            r.Source,
            r.Action,
            r.TargetType,
            r.TargetId,
            r.Summary,
            r.DetailsJson,
            r.CreatedAt.ToDateTimeOffset())).ToList();

        return Results.Ok(new ActionLogPageDto(entries, hasMore ? FormatCursor(rows[^1]) : null));
    }

    /// <summary>Downloads every event the guild still has (retention already bounds what is
    /// kept, so there is no separate window) with one row per RSVP — see CsvExport for the row
    /// model. The download itself is logged: exporting attendee data is a management action a
    /// server's other managers should be able to see.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ExportEventsCsv(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildManageAsync(
                context, access, guildId, cancellationToken,
                "Only server managers can export events.") is { } denied)
        {
            return denied;
        }

        var events = await db.Events
            .AsNoTracking()
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .Where(e => e.GuildId == guildId)
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        var now = clock.GetCurrentInstant();
        ActionLog.Record(
            db, guildId, ActionLog.ActorFor(context), ActionLogAction.EventsExported, ActionTargetType.Guild, null,
            $"Exported the events CSV ({events.Count} events)", now, new { eventCount = events.Count });
        await db.SaveChangesAsync(cancellationToken);

        var bytes = CsvExport.ToUtf8WithBom(CsvExport.BuildEventsCsv(events));
        var fileName = $"calcrony-events-{guildId}-{now.InUtc().Date:yyyyMMdd}.csv";
        // Results.File sets Content-Disposition: attachment with the file name for us.
        return Results.File(bytes, "text/csv; charset=utf-8", fileName);
    }

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
