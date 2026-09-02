using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Bot-only role snapshot endpoints (ADR 0004). The API never asks Discord who holds a
/// role; the bot, which runs the GuildMembers intent, writes snapshots here and the API reads
/// them to answer WEB callers' signup restrictions. Rows exist only for watched roles — roles a
/// live restriction names — and only for members holding at least one of them, following the
/// channel-snapshot model: the API publishes what it references, the bot pushes exactly that.</summary>
public static class RoleSnapshotEndpoints
{
    /// <summary>Maps role-snapshot routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapRoleSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/guilds/roles/watched", GetWatched).RequireAuthorization("BotOnly");
        app.MapGet("/guilds/{guildId:long}/roles/watched", GetWatchedForGuild).RequireAuthorization("BotOnly");
        app.MapPut("/guilds/{guildId:long}/roles/sync", SyncGuild).RequireAuthorization("BotOnly");
        app.MapPut("/guilds/{guildId:long}/members/{userId:long}/roles", PutMemberRoles).RequireAuthorization("BotOnly");
    }

    /// <summary>Lists, per bot-present guild, the roles its live signup restrictions name —
    /// scheduled/started events' options, running series' option templates, and open polls. The
    /// bot's Ready-time reconcile syncs every guild listed; guilds with no live restriction are
    /// absent, and the bot can't resolve roles in guilds it has left.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetWatched(CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var watched = await RoleWatchList.WatchedByGuildAsync(db, cancellationToken);
        var guildIds = watched.Keys.ToList();
        var present = await db.Guilds
            .Where(g => g.BotPresent && guildIds.Contains(g.Id))
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);
        return Results.Ok(new WatchedRolesResponse(
            [.. present.Order().Select(id => new GuildWatchedRolesDto(id, [.. watched[id].Order()]))]));
    }

    /// <summary>One guild's watched roles — what a single-guild sync resolves against, computed
    /// from that guild's restrictions alone so a per-guild sync never rescans every guild. Empty
    /// when the guild has no live restriction (or is unknown).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetWatchedForGuild(long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var watched = await RoleWatchList.WatchedForGuildAsync(db, guildId, cancellationToken);
        return Results.Ok(new GuildWatchedRolesDto(guildId, [.. watched.Order()]));
    }

    /// <summary>Replaces one guild's whole role snapshot and stamps it synced. Every role the bot
    /// was asked about gets a row — a null name is the tombstone for a role that no longer exists,
    /// which is what makes a restriction on a deleted role vacuous rather than unsatisfiable.
    /// Members are stored only with the existing watched roles they hold; a member holding none
    /// gets no row, so after this call a missing member row means exactly that. The API's own
    /// watch list, read in the same transaction, decides what may be stored at all: a payload the
    /// bot captured before a restriction was removed (a web clear, an event ending) is filtered
    /// to the roles still watched, so a sync can never re-add rows for a role nothing names.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SyncGuild(
        long guildId, RoleSyncRequest request, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (request.Roles is null || request.Members is null)
        {
            return Results.BadRequest(new ErrorResponse("Roles and Members are required (empty lists clear the snapshot)."));
        }

        var now = clock.GetCurrentInstant();
        // Replace-not-merge inside one transaction: a reader must never see the gap between the
        // old rows going and the new ones landing as "synced, holds nothing". The guild row is
        // locked first so this serializes with the presence routes: a sync that was in flight
        // when the bot left waits for the leave to commit, then sees BotPresent false and stops —
        // it can't resurrect what the leave dropped.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockGuildRowAsync(db, guildId, cancellationToken);
        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);
        if (!guild.BotPresent)
        {
            return Results.Conflict(new ErrorResponse("The bot is not in this server — there is nothing to snapshot."));
        }

        await db.GuildRoles.Where(r => r.GuildId == guildId).ExecuteDeleteAsync(cancellationToken);
        await db.GuildMemberRoles.Where(m => m.GuildId == guildId).ExecuteDeleteAsync(cancellationToken);

        var watchedNow = await RoleWatchList.WatchedForGuildAsync(db, guildId, cancellationToken);
        var roles = request.Roles
            .Where(r => watchedNow.Contains(r.RoleId))
            .GroupBy(r => r.RoleId)
            .Select(g => g.First())
            .ToList();
        var existing = new HashSet<long>();
        foreach (var role in roles)
        {
            // Null is the tombstone and means exactly "the bot reported this role gone" — a name
            // the bot did report is kept whatever it contains (Discord allows whitespace-only
            // names), only clamped to the column; the guild-name helper's blank-to-null rule
            // would otherwise turn such a role into a vacuous restriction.
            var name = role.Name is null ? null : ClampName(role.Name, FieldLimits.RoleName);
            if (name is not null)
            {
                existing.Add(role.RoleId);
            }

            db.GuildRoles.Add(new GuildRole { GuildId = guildId, RoleId = role.RoleId, Name = name, SnapshotAt = now });
        }

        foreach (var member in request.Members.GroupBy(m => m.UserId).Select(g => g.First()))
        {
            var held = (member.RoleIds ?? []).Where(existing.Contains).Distinct().ToArray();
            if (held.Length == 0)
            {
                continue;
            }

            db.GuildMemberRoles.Add(new GuildMemberRole
            {
                GuildId = guildId, UserId = member.UserId, RoleIds = held, SnapshotAt = now,
            });
        }

        guild.RolesSyncedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Upserts one member's watched roles from a member-update push. Only roles the
    /// guild's snapshot knows to exist are kept — the bot's watched set can run ahead of the
    /// API's between syncs — and an empty result removes the row (no row = holds none).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutMemberRoles(
        long guildId, long userId, PutMemberRolesRequest request, CalCronyDbContext db, IClock clock,
        CancellationToken cancellationToken)
    {
        if (request.RoleIds is null)
        {
            return Results.BadRequest(new ErrorResponse("RoleIds is required (an empty list removes the member)."));
        }

        // Same serialization with the presence routes as the full sync; an unknown or bot-absent
        // guild holds nothing, so there is nothing to upsert into.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockGuildRowAsync(db, guildId, cancellationToken);
        var present = await db.Guilds
            .Where(g => g.Id == guildId)
            .Select(g => (bool?)g.BotPresent)
            .FirstOrDefaultAsync(cancellationToken);
        if (present is not true)
        {
            return Results.NoContent();
        }

        var known = (await db.GuildRoles
            .Where(r => r.GuildId == guildId && r.Name != null)
            .Select(r => r.RoleId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var held = request.RoleIds.Where(known.Contains).Distinct().ToArray();

        var row = await db.GuildMemberRoles.FindAsync([guildId, userId], cancellationToken);
        if (held.Length == 0)
        {
            if (row is not null)
            {
                db.GuildMemberRoles.Remove(row);
            }
        }
        else if (row is null)
        {
            db.GuildMemberRoles.Add(new GuildMemberRole
            {
                GuildId = guildId, UserId = userId, RoleIds = held, SnapshotAt = clock.GetCurrentInstant(),
            });
        }
        else
        {
            row.RoleIds = held;
            row.SnapshotAt = clock.GetCurrentInstant();
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Clamps a reported role name to its column, never splitting a surrogate pair and
    /// never turning a present name into null (see the sync for why null is reserved).</summary>
    /// <param name="name">The reported name.</param>
    /// <param name="limit">The column max length.</param>
    /// <returns>The storable name.</returns>
    private static string ClampName(string name, int limit)
    {
        if (name.Length <= limit)
        {
            return name;
        }

        var cut = char.IsHighSurrogate(name[limit - 1]) ? limit - 1 : limit;
        return name[..cut];
    }

    /// <summary>Drops whatever snapshot rows exist for roles a request is about to make NEWLY
    /// watched — named by it but by no live restriction before it — so the web fails closed on
    /// them until the bot's post-command sync lands. Rows for such a role can only be leftovers
    /// from an earlier watch interval (they are trimmed by the next reconcile or retention, but
    /// that may not have happened yet); with a fresh lease they would otherwise answer for the
    /// new restriction with old membership if the best-effort sync failed. Roles already watched
    /// keep their rows — those are maintained live. Call before the request's own SaveChanges,
    /// while the new restriction is not yet visible to the watch list.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="namedRoleIds">Every role the request's restrictions name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    internal static async Task InvalidateNewlyWatchedAsync(
        CalCronyDbContext db, long guildId, IEnumerable<long> namedRoleIds, CancellationToken cancellationToken)
    {
        var named = namedRoleIds.Distinct().ToList();
        if (named.Count == 0)
        {
            return;
        }

        var watchedBefore = await RoleWatchList.WatchedForGuildAsync(db, guildId, cancellationToken);
        var newlyWatched = named.Where(id => !watchedBefore.Contains(id)).ToList();
        if (newlyWatched.Count == 0)
        {
            return;
        }

        await db.GuildRoles
            .Where(r => r.GuildId == guildId && newlyWatched.Contains(r.RoleId))
            .ExecuteDeleteAsync(cancellationToken);
        var holders = await db.GuildMemberRoles
            .Where(m => m.GuildId == guildId && m.RoleIds.Any(id => newlyWatched.Contains(id)))
            .ToListAsync(cancellationToken);
        foreach (var member in holders)
        {
            var kept = member.RoleIds.Where(id => !newlyWatched.Contains(id)).ToArray();
            if (kept.Length == 0)
            {
                db.GuildMemberRoles.Remove(member);
            }
            else
            {
                member.RoleIds = kept;
            }
        }
    }

    /// <summary>Takes a FOR UPDATE lock on the guild row inside the ambient transaction, so the
    /// snapshot writers and the presence routes for one guild serialize. A guild with no row yet
    /// locks nothing — it has no snapshot a leave could have dropped.</summary>
    /// <param name="db">The database context (a transaction must be open).</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    internal static Task LockGuildRowAsync(CalCronyDbContext db, long guildId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "Guilds" WHERE "Id" = {guildId} FOR UPDATE""", cancellationToken);

    /// <summary>The retention step for one guild, under its row lock with the watch set re-read
    /// inside: a guild with no live restriction left — or one the bot has left, whatever
    /// restrictions it still carries — loses its snapshot outright; one that still has some keeps
    /// it, trimmed to exactly the roles those restrictions name. Reading the decision inputs
    /// inside the same lock the snapshot writes take is what stops a sweep that started before a
    /// restriction was created and synced from dropping that fresh snapshot.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The guild to reconcile.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many snapshot rows were removed.</returns>
    internal static async Task<int> ReconcileSnapshotAsync(
        CalCronyDbContext db, long guildId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockGuildRowAsync(db, guildId, cancellationToken);
        var guild = await db.Guilds
            .Where(g => g.Id == guildId)
            .Select(g => new { g.BotPresent, g.RolesSyncedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (guild?.RolesSyncedAt is null)
        {
            // Nothing to reconcile — the leave or another sweep got here first.
            return 0;
        }

        var watched = await RoleWatchList.WatchedForGuildAsync(db, guildId, cancellationToken);
        var removed = !guild.BotPresent || watched.Count == 0
            ? await DropSnapshotsAsync(db, [guildId], cancellationToken)
            : await PruneSnapshotAsync(db, guildId, watched, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    /// <summary>Trims one guild's snapshot to the roles its live restrictions still name: rows for
    /// roles no restriction names anymore go, and members lose those ids (a member left holding
    /// nothing watched loses the row). An ended event or a closed poll does not trigger a bot
    /// sync, so without this the roles it named would stay held until the next full sync — and
    /// the API must not keep who-holds-what for a role nothing references (ADR 0004).</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The guild to trim.</param>
    /// <param name="watchedRoleIds">The roles the guild's live restrictions name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many role rows and member rows were removed.</returns>
    internal static async Task<int> PruneSnapshotAsync(
        CalCronyDbContext db, long guildId, IReadOnlyCollection<long> watchedRoleIds, CancellationToken cancellationToken)
    {
        var watched = watchedRoleIds.ToList();
        var removedRoles = await db.GuildRoles
            .Where(r => r.GuildId == guildId && !watched.Contains(r.RoleId))
            .ExecuteDeleteAsync(cancellationToken);

        var removedMembers = 0;
        var members = await db.GuildMemberRoles.Where(m => m.GuildId == guildId).ToListAsync(cancellationToken);
        foreach (var member in members)
        {
            var kept = member.RoleIds.Where(watched.Contains).ToArray();
            if (kept.Length == member.RoleIds.Length)
            {
                continue;
            }

            if (kept.Length == 0)
            {
                db.GuildMemberRoles.Remove(member);
                removedMembers++;
            }
            else
            {
                member.RoleIds = kept;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return removedRoles + removedMembers;
    }

    /// <summary>Drops the role snapshots of the given guilds and clears their sync markers — the
    /// bot-left path and the retention purge for guilds with no live restriction. Executes
    /// immediately (bulk statements), outside any pending SaveChanges.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildIds">The guilds whose snapshots go.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many snapshot rows were removed.</returns>
    internal static async Task<int> DropSnapshotsAsync(
        CalCronyDbContext db, IReadOnlyCollection<long> guildIds, CancellationToken cancellationToken)
    {
        if (guildIds.Count == 0)
        {
            return 0;
        }

        var roles = await db.GuildRoles
            .Where(r => guildIds.Contains(r.GuildId))
            .ExecuteDeleteAsync(cancellationToken);
        var members = await db.GuildMemberRoles
            .Where(m => guildIds.Contains(m.GuildId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Guilds
            .Where(g => guildIds.Contains(g.Id) && g.RolesSyncedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RolesSyncedAt, (Instant?)null), cancellationToken);
        return roles + members;
    }
}
