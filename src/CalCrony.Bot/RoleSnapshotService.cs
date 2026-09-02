using System.Collections.Concurrent;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.WebSocket;

namespace CalCrony.Bot;

/// <summary>Pushes role snapshots to the API (ADR 0004): which watched roles exist and who holds
/// them, so the API can answer WEB callers' signup restrictions without ever asking Discord. The
/// API publishes what it watches (<c>GET /guilds/roles/watched</c>); this service resolves exactly
/// that and pushes it — a full guild sync at Ready and on a timer, right after any command that
/// names roles, and whenever a watched role is deleted or renamed; a single-member push on a
/// member update whose role delta touches a watched role, and an empty push when a member leaves.
/// Every write to one guild's snapshot — full or single-member — goes through that guild's lock,
/// and a full sync reads the watched set INSIDE the lock, so no writer can land with a set older
/// than one already written. Strictly best-effort in the <see cref="AttendeeRoleManager"/> posture:
/// every failure is logged, nothing throws into the gateway, and the web simply stays fail-closed
/// until the next sync lands.</summary>
/// <param name="client">The Discord socket client.</param>
/// <param name="api">The CalCrony API client.</param>
/// <param name="logger">The host logger.</param>
public sealed class RoleSnapshotService(DiscordSocketClient client, CalCronyApiClient api, ILogger<RoleSnapshotService> logger)
{
    /// <summary>The existing watched roles per guild — what a member update is compared against.
    /// Registered at the START of a sync, before the member list is captured, so an update that
    /// arrives mid-sync is judged against the set the sync is about to write. Absent or empty =
    /// nothing to push for that guild.</summary>
    private readonly ConcurrentDictionary<ulong, HashSet<ulong>> watched = new();

    /// <summary>One lock per guild, held by a full sync for the whole capture-and-PUT and by a
    /// member push for its PUT. Without it a full sync that captured the member list BEFORE a
    /// role loss could land AFTER that loss's push and restore the stale row under a fresh lease;
    /// serialized, the push runs after the sync and rewrites the row from the live cache. Locks
    /// are never retired: a lock replaced while held would strand its waiters on the old instance
    /// while new work took the new one, and one idle semaphore per guild ever synced is nothing.
    /// The API is the backstop for a guild the bot has left — it refuses snapshot writes for a
    /// bot-absent guild under its own row lock — so ordering against the leave does not matter.</summary>
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> guildLocks = new();

    /// <summary>Full reconcile — at Ready and on the timer: syncs every guild the API lists plus
    /// every guild this bot still holds a watched set for. Heals a fresh database, catches role
    /// and membership changes missed while offline, and renews each guild's lease. The listing
    /// only ENUMERATES guilds: each sync re-reads its own (guild-scoped) watched set under the
    /// guild's lock, so a restriction created while this loop is on another guild is never
    /// overwritten by the older listing, and a guild that lost its restrictions is dropped from
    /// the cache only once its own fresh read says so. A cached guild the client no longer has is
    /// forgotten here as well.</summary>
    public async Task ReconcileAllAsync()
    {
        var result = await api.GetWatchedRolesAsync();
        if (result is not { Success: true, Value: { } response })
        {
            logger.LogWarning("Watched-role lookup failed: {Error}", result.Error ?? "empty response body");
            return;
        }

        var guildIds = response.Guilds.Select(g => (ulong)g.GuildId).Union(watched.Keys).ToList();
        foreach (var guildId in guildIds)
        {
            if (client.GetGuild(guildId) is { } guild)
            {
                await SyncGuildAsync(guild);
            }
            else
            {
                Forget(guildId);
            }
        }

        if (response.Guilds.Count > 0)
        {
            logger.LogInformation("Reconciled role snapshots for {Count} guilds.", response.Guilds.Count);
        }
    }

    /// <summary>Syncs one guild against the API's CURRENT watched set — the call a command makes
    /// right after the API accepted a restriction, so the snapshot is authoritative before the
    /// embed can be clicked. The set is read inside the guild's lock, after any sync or member
    /// push already in flight, so two syncs can never land out of order; and it is read per guild,
    /// so a sync costs that guild's restrictions, not every guild's. A guild with nothing watched
    /// (its last restriction was cleared) is dropped from the cache; the API's retention drops its
    /// rows in due course.</summary>
    /// <param name="guild">The guild to sync.</param>
    public async Task SyncGuildAsync(SocketGuild guild)
    {
        var gate = LockFor(guild.Id);
        await gate.WaitAsync();
        try
        {
            var result = await api.GetGuildWatchedRolesAsync((long)guild.Id);
            if (result is not { Success: true, Value: { } entry })
            {
                logger.LogWarning("Watched-role lookup failed for guild {GuildId}: {Error}", guild.Id, result.Error ?? "empty response body");
                return;
            }

            if (entry.RoleIds.Count == 0)
            {
                // Nothing watched anymore: clear the API's rows too, not just the cache. Left in
                // place under a fresh lease, they would answer for a role restricted again within
                // the lease with membership from the earlier watch — before the post-command sync
                // lands, or if that best-effort sync fails. An empty sync leaves no row to answer
                // from, so a re-restriction fails closed until it is synced. The cache entry goes
                // only once the clear has landed: kept, it makes the next reconcile (which unions
                // cached guilds in) retry a clear that failed, since an unrestricted guild is
                // absent from the API's own listing.
                var cleared = await api.SyncGuildRolesAsync((long)guild.Id, new RoleSyncRequest([], []));
                if (cleared.Success)
                {
                    watched.TryRemove(guild.Id, out _);
                }
                else
                {
                    logger.LogWarning("Role snapshot clear failed for guild {GuildId}; the next reconcile retries: {Error}", guild.Id, cleared.Error);
                }

                return;
            }

            await SyncLockedAsync(guild, entry.RoleIds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort role snapshot sync failed for guild {GuildId}.", guild.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The sync body, run under the guild's lock: resolves the watched roles and every
    /// member holding one, and replaces the guild's snapshot. The member cache is lazy
    /// (AlwaysDownloadUsers is off), so the full member list is pulled first — a chunked gateway
    /// request, once per sync, not per RSVP.</summary>
    /// <param name="guild">The guild to sync.</param>
    /// <param name="watchedRoleIds">The roles the API wants snapshots for.</param>
    private async Task SyncLockedAsync(SocketGuild guild, IReadOnlyList<long> watchedRoleIds)
    {
        try
        {
            var existing = new Dictionary<long, string>();
            foreach (var roleId in watchedRoleIds)
            {
                if (guild.GetRole((ulong)roleId) is { } role)
                {
                    existing[roleId] = role.Name;
                }
            }

            // Registered BEFORE the member download, not just before the capture: the download
            // can take a while on a large guild, and a member update or departure during it must
            // already see this set so it queues behind the lock and lands after this PUT.
            watched[guild.Id] = [.. existing.Keys.Select(id => (ulong)id)];

            if (!guild.HasAllMembers)
            {
                await guild.DownloadUsersAsync();
            }

            var request = BuildSyncRequest(
                watchedRoleIds, existing,
                guild.Users.Select(u => ((long)u.Id, (IReadOnlyCollection<long>)[.. u.Roles.Select(r => (long)r.Id)])));
            var result = await api.SyncGuildRolesAsync((long)guild.Id, request);
            if (!result.Success)
            {
                logger.LogWarning("Role snapshot sync failed for guild {GuildId}: {Error}", guild.Id, result.Error);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort role snapshot sync failed for guild {GuildId}.", guild.Id);
        }
    }

    private SemaphoreSlim LockFor(ulong guildId) => guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));

    /// <summary>Retires a guild's watched set so the reconcile stops unioning the guild back in
    /// every tick and member events stop pushing for it. The lock stays (see <see cref="guildLocks"/>).</summary>
    /// <param name="guildId">The guild to forget.</param>
    private void Forget(ulong guildId) => watched.TryRemove(guildId, out _);

    /// <summary>The bot left the guild: the API drops the snapshot on the presence report and
    /// refuses any later write for the guild, and the bot forgets its side (under the lock, after
    /// any write in flight) so the reconcile stops unioning the guild back in every tick.</summary>
    /// <param name="guild">The guild the bot left.</param>
    public async Task OnLeftGuildAsync(SocketGuild guild)
    {
        var gate = LockFor(guild.Id);
        await gate.WaitAsync();
        try
        {
            Forget(guild.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Member departure (leave, kick, or ban — none of which raise a member update): an
    /// empty push removes the member's row at once. Without it a former member's row would stay
    /// authoritative until the next reconcile, while their web membership snapshot can outlive
    /// the departure by days.</summary>
    /// <param name="guild">The guild the member left.</param>
    /// <param name="user">The departed member.</param>
    public async Task OnUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        try
        {
            if (!watched.TryGetValue(guild.Id, out var set) || set.Count == 0)
            {
                return;
            }

            var gate = LockFor(guild.Id);
            await gate.WaitAsync();
            try
            {
                var result = await api.PutMemberRolesAsync((long)guild.Id, (long)user.Id, []);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Member departure push failed for user {UserId} in guild {GuildId}: {Error}", user.Id, guild.Id, result.Error);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort member departure push failed for user {UserId} in guild {GuildId}.", user.Id, guild.Id);
        }
    }

    /// <summary>The sync payload, pure: a row for EVERY watched role — with the name when it exists
    /// and null when the guild no longer has it, the tombstone that makes a restriction on a deleted
    /// role vacuous — and one row per member holding at least one existing watched role.</summary>
    /// <param name="watchedRoleIds">The roles the API wants snapshots for.</param>
    /// <param name="existingRoles">The watched roles the guild still has, with their names.</param>
    /// <param name="members">Every member with the roles they hold.</param>
    /// <returns>The request the sync route takes.</returns>
    public static RoleSyncRequest BuildSyncRequest(
        IReadOnlyList<long> watchedRoleIds,
        IReadOnlyDictionary<long, string> existingRoles,
        IEnumerable<(long UserId, IReadOnlyCollection<long> RoleIds)> members)
    {
        var roles = watchedRoleIds
            .Distinct()
            .Select(id => new RoleNameDto(id, existingRoles.GetValueOrDefault(id)))
            .ToList();
        var existingWatched = roles.Where(r => r.Name is not null).Select(r => r.RoleId).ToHashSet();
        var rows = members
            .Select(m => new MemberRolesDto(m.UserId, [.. m.RoleIds.Where(existingWatched.Contains).Distinct().Order()]))
            .Where(m => m.RoleIds.Count > 0)
            .ToList();
        return new RoleSyncRequest(roles, rows);
    }

    /// <summary>Member update: pushes the member's watched roles when the delta touches one. A
    /// nickname or avatar change costs nothing; a role change outside the watched set costs
    /// nothing either.</summary>
    /// <param name="before">The member before the update, when cached.</param>
    /// <param name="after">The member after the update.</param>
    public async Task OnMemberUpdatedAsync(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after)
    {
        try
        {
            if (!watched.TryGetValue(after.Guild.Id, out var set) || set.Count == 0)
            {
                return;
            }

            var afterRoles = after.Roles.Select(r => r.Id).ToHashSet();
            var beforeRoles = before.HasValue ? before.Value.Roles.Select(r => r.Id).ToHashSet() : null;
            if (!RoleDeltaTouchesWatched(set, beforeRoles, afterRoles))
            {
                return;
            }

            // Behind the guild lock, and re-read from the live cache once inside it: if a full
            // sync is mid-flight this lands after it, with the member's current roles.
            var gate = LockFor(after.Guild.Id);
            await gate.WaitAsync();
            try
            {
                var current = watched.TryGetValue(after.Guild.Id, out var latest) ? latest : set;
                var held = after.Roles.Select(r => r.Id).Where(current.Contains).Select(id => (long)id).Order().ToList();
                var result = await api.PutMemberRolesAsync((long)after.Guild.Id, (long)after.Id, held);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Member role push failed for user {UserId} in guild {GuildId}: {Error}", after.Id, after.Guild.Id, result.Error);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort member role push failed for user {UserId} in guild {GuildId}.", after.Id, after.Guild.Id);
        }
    }

    /// <summary>Whether a member's role change alters what the snapshot holds for them, pure. An
    /// unknown "before" (member not cached) can't be compared, so it pushes — the push is an
    /// idempotent upsert, and after a full sync every member is cached anyway.</summary>
    /// <param name="watched">The guild's existing watched roles.</param>
    /// <param name="before">The roles held before, or null when unknown.</param>
    /// <param name="after">The roles held now.</param>
    /// <returns>True when a push is needed.</returns>
    public static bool RoleDeltaTouchesWatched(
        IReadOnlySet<ulong> watched, IReadOnlyCollection<ulong>? before, IReadOnlyCollection<ulong> after)
    {
        if (before is null)
        {
            return true;
        }

        var beforeWatched = before.Where(watched.Contains).ToHashSet();
        var afterWatched = after.Where(watched.Contains).ToHashSet();
        return !beforeWatched.SetEquals(afterWatched);
    }

    /// <summary>A deleted watched role re-syncs the guild so the API records the tombstone and the
    /// restriction naming it becomes vacuous within seconds, on the web as well as in Discord.</summary>
    /// <param name="role">The deleted role.</param>
    public async Task OnRoleDeletedAsync(SocketRole role)
    {
        if (watched.TryGetValue(role.Guild.Id, out var set) && set.Contains(role.Id))
        {
            await SyncGuildAsync(role.Guild);
        }
    }

    /// <summary>A renamed watched role re-syncs the guild so the web's name snapshot follows.</summary>
    /// <param name="before">The role before the update.</param>
    /// <param name="after">The role after the update.</param>
    public async Task OnRoleUpdatedAsync(SocketRole before, SocketRole after)
    {
        if (before.Name != after.Name && watched.TryGetValue(after.Guild.Id, out var set) && set.Contains(after.Id))
        {
            await SyncGuildAsync(after.Guild);
        }
    }
}
