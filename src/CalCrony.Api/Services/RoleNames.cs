using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Services;

/// <summary>Role-name snapshot lookups for DTO mapping. The API holds names only for watched
/// roles (see <see cref="RoleWatchList"/>: restricted and granted roles alike), so a name can
/// still be missing — a role named since the bot's last sync, or one the bot found deleted —
/// and consumers fall back to the id, the ADR 0001 posture for every name snapshot.</summary>
public static class RoleNames
{
    private static readonly IReadOnlyDictionary<long, string?> Empty = new Dictionary<long, string?>();

    /// <summary>Loads the names the guild's snapshot holds for the given roles — one query, or
    /// none when there is nothing to look up.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="roleIds">The roles to name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Role id to name; roles the snapshot doesn't know are absent.</returns>
    public static async Task<IReadOnlyDictionary<long, string?>> LoadAsync(
        CalCronyDbContext db, long guildId, IEnumerable<long> roleIds, CancellationToken cancellationToken)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return Empty;
        }

        return await db.GuildRoles
            .Where(r => r.GuildId == guildId && ids.Contains(r.RoleId))
            .ToDictionaryAsync(r => r.RoleId, r => r.Name, cancellationToken);
    }

    /// <summary>Names for everything an event's options reference: their restrictions and their
    /// attendee roles (the §3.6 chip reads better as "@Raider" than "role #123456" when the API
    /// happens to hold the name).</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="options">The event's options.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Role id to name for the roles the snapshot knows.</returns>
    public static Task<IReadOnlyDictionary<long, string?>> ForOptionsAsync(
        CalCronyDbContext db, long guildId, IEnumerable<RsvpOption> options, CancellationToken cancellationToken) =>
        LoadAsync(
            db, guildId,
            options.SelectMany(o => o.AllowedRoleIds.Concat(o.AttendeeRoleId is { } role ? [role] : [])),
            cancellationToken);
}
