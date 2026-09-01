namespace CalCrony.Api.Services;

/// <summary>What a role check decided for one caller.</summary>
public enum RoleRestrictionVerdict
{
    /// <summary>The caller may proceed: no restriction, a bypass, a vacuous restriction, or a
    /// confirmed role match.</summary>
    Allowed = 0,

    /// <summary>The snapshot is authoritative and the caller holds none of the allowed roles.</summary>
    Denied = 1,

    /// <summary>The snapshot cannot answer — the guild has never been synced, or an allowed role
    /// has not been checked since it became watched. The web fails closed on this (ADR 0004).</summary>
    Unverifiable = 2,
}

/// <summary>The verdict plus the roles that actually gated it: the allowed set minus roles the
/// bot found deleted. Empty when nothing gated (unrestricted, bypass, or every role deleted).</summary>
/// <param name="Verdict">What the check decided.</param>
/// <param name="EffectiveRoleIds">The allowed roles still in force — what a denial names.</param>
public readonly record struct RoleRestrictionResult(RoleRestrictionVerdict Verdict, IReadOnlyList<long> EffectiveRoleIds);

/// <summary>Pure evaluation of a signup restriction against the guild's role snapshot — the rule
/// the API applies to WEB callers (the bot checks Discord live and never reads snapshots). Kept
/// free of I/O so the whole matrix is directly testable.</summary>
public static class RoleRestriction
{
    /// <summary>Decides whether a member may take a restricted option. In order: an empty
    /// restriction or a bypass allows; a guild that has never been synced is unverifiable; roles
    /// the bot found deleted (a checked row with no name) drop out, and a restriction whose roles
    /// are all gone is vacuous — allowed, so deleting a role can never lock a server out of its
    /// own events; any remaining role with no checked row at all is unverifiable, because absence
    /// means "not looked at since it became watched", never "not held"; otherwise the member is
    /// allowed exactly when they hold at least one remaining role.</summary>
    /// <param name="allowedRoleIds">The option's restriction (empty = unrestricted).</param>
    /// <param name="rolesSynced">Whether the guild carries a sync marker (Guild.RolesSyncedAt).</param>
    /// <param name="checkedRoles">Every GuildRoles row for the guild: role id to name, where a null
    /// name is the bot's tombstone for a role that no longer exists.</param>
    /// <param name="memberRoleIds">The watched roles the member holds per the snapshot (empty
    /// when they have no row — which, after a sync, means they hold none).</param>
    /// <param name="bypass">True for the event creator and server managers.</param>
    /// <returns>The verdict and the roles that gated it.</returns>
    public static RoleRestrictionResult Evaluate(
        IReadOnlyCollection<long> allowedRoleIds,
        bool rolesSynced,
        IReadOnlyDictionary<long, string?> checkedRoles,
        IReadOnlyCollection<long> memberRoleIds,
        bool bypass)
    {
        if (allowedRoleIds.Count == 0 || bypass)
        {
            return new RoleRestrictionResult(RoleRestrictionVerdict.Allowed, []);
        }

        if (!rolesSynced)
        {
            return new RoleRestrictionResult(RoleRestrictionVerdict.Unverifiable, [.. allowedRoleIds]);
        }

        // A checked-and-deleted role can't be held by anyone, so it stops gating rather than
        // making the restriction unsatisfiable.
        var effective = allowedRoleIds
            .Where(id => !(checkedRoles.TryGetValue(id, out var name) && name is null))
            .Distinct()
            .ToList();
        if (effective.Count == 0)
        {
            return new RoleRestrictionResult(RoleRestrictionVerdict.Allowed, []);
        }

        if (effective.Any(id => !checkedRoles.ContainsKey(id)))
        {
            return new RoleRestrictionResult(RoleRestrictionVerdict.Unverifiable, effective);
        }

        var held = memberRoleIds as IReadOnlySet<long> ?? memberRoleIds.ToHashSet();
        return new RoleRestrictionResult(
            effective.Any(held.Contains) ? RoleRestrictionVerdict.Allowed : RoleRestrictionVerdict.Denied,
            effective);
    }

    /// <summary>Renders role names for a denial: "@Tank", "@Tank or @Healer", "@Tank, @Healer, or
    /// @DPS". A role without a name (which a denial never produces, but callers may format other
    /// sets) falls back to its id.</summary>
    /// <param name="roleIds">The roles to name, in order.</param>
    /// <param name="names">Role id to name snapshot.</param>
    /// <returns>The joined list.</returns>
    public static string DescribeRoles(IReadOnlyList<long> roleIds, IReadOnlyDictionary<long, string?> names)
    {
        var rendered = roleIds
            .Select(id => names.TryGetValue(id, out var name) && name is not null ? $"@{name}" : $"role #{id}")
            .ToList();
        return rendered.Count switch
        {
            0 => "",
            1 => rendered[0],
            2 => $"{rendered[0]} or {rendered[1]}",
            _ => $"{string.Join(", ", rendered[..^1])}, or {rendered[^1]}",
        };
    }
}
