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
    /// gets no row, so after this call a missing member row means exactly that.</summary>
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
        // old rows going and the new ones landing as "synced, holds nothing".
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);
        await db.GuildRoles.Where(r => r.GuildId == guildId).ExecuteDeleteAsync(cancellationToken);
        await db.GuildMemberRoles.Where(m => m.GuildId == guildId).ExecuteDeleteAsync(cancellationToken);

        var roles = request.Roles.GroupBy(r => r.RoleId).Select(g => g.First()).ToList();
        var existing = new HashSet<long>();
        foreach (var role in roles)
        {
            var name = GuildPresenceEndpoints.Truncate(role.Name, FieldLimits.RoleName);
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
        return Results.NoContent();
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
