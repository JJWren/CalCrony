using System.Collections.Concurrent;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.WebSocket;

namespace CalCrony.Bot;

/// <summary>Pushes role snapshots to the API (ADR 0004): which watched roles exist and who holds
/// them, so the API can answer WEB callers' signup restrictions without ever asking Discord. The
/// API publishes what it watches (<c>GET /guilds/roles/watched</c>); this service resolves exactly
/// that and pushes it — a full guild sync at Ready, right after any command that names roles, and
/// whenever a watched role is deleted or renamed; a single-member push on a member update whose
/// role delta touches a watched role. Strictly best-effort in the <see cref="AttendeeRoleManager"/>
/// posture: every failure is logged, nothing throws into the gateway, and the web simply stays
/// fail-closed until the next sync lands.</summary>
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
    /// serialized, the push runs after the sync and rewrites the row from the live cache.</summary>
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> guildLocks = new();

    /// <summary>Full reconcile at Ready: syncs every guild the API lists. Heals a fresh database
    /// and catches role and membership changes missed while offline.</summary>
    public async Task ReconcileAllAsync()
    {
        var result = await api.GetWatchedRolesAsync();
        if (result is not { Success: true, Value: { } response })
        {
            logger.LogWarning("Watched-role lookup failed: {Error}", result.Error ?? "empty response body");
            return;
        }

        var listed = new HashSet<ulong>();
        foreach (var entry in response.Guilds)
        {
            listed.Add((ulong)entry.GuildId);
            if (client.GetGuild((ulong)entry.GuildId) is { } guild)
            {
                await SyncGuildAsync(guild, entry.RoleIds);
            }
        }

        foreach (var stale in watched.Keys.Where(id => !listed.Contains(id)).ToList())
        {
            watched.TryRemove(stale, out _);
        }

        if (response.Guilds.Count > 0)
        {
            logger.LogInformation("Reconciled role snapshots for {Count} guilds.", response.Guilds.Count);
        }
    }

    /// <summary>Syncs one guild against the API's CURRENT watched set — the call a command makes
    /// right after the API accepted a restriction, so the snapshot is authoritative before the
    /// embed can be clicked. A guild the API no longer lists has nothing watched (its last
    /// restriction was cleared); the API's retention drops its rows in due course.</summary>
    /// <param name="guild">The guild to sync.</param>
    public async Task SyncGuildAsync(SocketGuild guild)
    {
        var result = await api.GetWatchedRolesAsync();
        if (result is not { Success: true, Value: { } response })
        {
            logger.LogWarning("Watched-role lookup failed for guild {GuildId}: {Error}", guild.Id, result.Error ?? "empty response body");
            return;
        }

        var entry = response.Guilds.FirstOrDefault(g => g.GuildId == (long)guild.Id);
        if (entry is null)
        {
            watched.TryRemove(guild.Id, out _);
            return;
        }

        await SyncGuildAsync(guild, entry.RoleIds);
    }

    /// <summary>Resolves the watched roles and every member holding one, and replaces the guild's
    /// snapshot. The member cache is lazy (AlwaysDownloadUsers is off), so the full member list is
    /// pulled first — a chunked gateway request, once per sync, not per RSVP.</summary>
    /// <param name="guild">The guild to sync.</param>
    /// <param name="watchedRoleIds">The roles the API wants snapshots for.</param>
    public async Task SyncGuildAsync(SocketGuild guild, IReadOnlyList<long> watchedRoleIds)
    {
        var gate = LockFor(guild.Id);
        await gate.WaitAsync();
        try
        {
            if (!guild.HasAllMembers)
            {
                await guild.DownloadUsersAsync();
            }

            var existing = new Dictionary<long, string>();
            foreach (var roleId in watchedRoleIds)
            {
                if (guild.GetRole((ulong)roleId) is { } role)
                {
                    existing[roleId] = role.Name;
                }
            }

            // Registered before the capture: a member update arriving from here on is judged
            // against this set and queues behind the lock, so it lands after this PUT.
            watched[guild.Id] = [.. existing.Keys.Select(id => (ulong)id)];

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
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim LockFor(ulong guildId) => guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));

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
