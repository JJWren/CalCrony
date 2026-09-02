using CalCrony.Api.Services;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The pure role-restriction matrix (RSVP v2 §3.5, ADR 0004): what the API decides for
/// a WEB caller from the guild's role snapshot. The bot never reaches this — it checks live.</summary>
public class RoleRestrictionTests
{
    private const long Tank = 1;
    private const long Healer = 2;
    private const long Deleted = 3;
    private const long Unchecked = 99;

    /// <summary>A synced guild's GuildRoles: Tank and Healer exist, role 3 was checked and found
    /// deleted (a tombstone), and role 99 has never been checked.</summary>
    private static readonly Dictionary<long, string?> Checked = new()
    {
        [Tank] = "Tank",
        [Healer] = "Healer",
        [Deleted] = null,
    };

    [Fact]
    public void An_empty_restriction_allows_everyone_even_before_any_sync()
    {
        var result = RoleRestriction.Evaluate([], rolesSynced: false, Checked, [], bypass: false);

        Assert.Equal(RoleRestrictionVerdict.Allowed, result.Verdict);
        Assert.Empty(result.EffectiveRoleIds);
    }

    [Fact]
    public void A_bypass_allows_regardless_of_the_snapshot()
    {
        // The creator and managers pass before the snapshot is even consulted.
        var result = RoleRestriction.Evaluate([Tank], rolesSynced: false, new Dictionary<long, string?>(), [], bypass: true);

        Assert.Equal(RoleRestrictionVerdict.Allowed, result.Verdict);
    }

    [Fact]
    public void A_guild_that_was_never_synced_is_unverifiable()
    {
        // No sync marker means a missing member row could be "never looked", not "holds none" —
        // the web must fail closed rather than read absence as either answer.
        var result = RoleRestriction.Evaluate([Tank], rolesSynced: false, Checked, [Tank], bypass: false);

        Assert.Equal(RoleRestrictionVerdict.Unverifiable, result.Verdict);
    }

    [Fact]
    public void Holding_any_allowed_role_is_allowed()
    {
        var result = RoleRestriction.Evaluate([Tank, Healer], rolesSynced: true, Checked, [Healer], bypass: false);

        Assert.Equal(RoleRestrictionVerdict.Allowed, result.Verdict);
        Assert.Equal([Tank, Healer], result.EffectiveRoleIds);
    }

    [Fact]
    public void Holding_none_of_them_is_denied_and_the_denial_names_what_gated()
    {
        var result = RoleRestriction.Evaluate([Tank, Healer], rolesSynced: true, Checked, [], bypass: false);

        Assert.Equal(RoleRestrictionVerdict.Denied, result.Verdict);
        Assert.Equal("@Tank or @Healer", RoleRestriction.DescribeRoles(result.EffectiveRoleIds, Checked));
    }

    [Fact]
    public void A_deleted_role_drops_out_of_the_restriction()
    {
        // The tombstone can't be held by anyone, so it stops gating; Tank still does.
        var result = RoleRestriction.Evaluate([Deleted, Tank], rolesSynced: true, Checked, [], bypass: false);

        Assert.Equal(RoleRestrictionVerdict.Denied, result.Verdict);
        Assert.Equal([Tank], result.EffectiveRoleIds);
    }

    [Fact]
    public void A_restriction_whose_roles_are_all_deleted_is_vacuous()
    {
        // Deleting a role must never lock a server out of its own event (ADR 0004).
        var result = RoleRestriction.Evaluate([Deleted], rolesSynced: true, Checked, [], bypass: false);

        Assert.Equal(RoleRestrictionVerdict.Allowed, result.Verdict);
        Assert.Empty(result.EffectiveRoleIds);
    }

    [Fact]
    public void A_role_the_bot_has_not_checked_since_it_became_watched_is_unverifiable()
    {
        // No row is neither "exists" nor "deleted" — it is "not looked at yet", and admitting on
        // that would be the fail-open the design rejects. Holding the other allowed role doesn't
        // rescue it either: the snapshot as a whole isn't authoritative for this restriction.
        var result = RoleRestriction.Evaluate([Tank, Unchecked], rolesSynced: true, Checked, [Tank], bypass: false);

        Assert.Equal(RoleRestrictionVerdict.Unverifiable, result.Verdict);
    }

    [Fact]
    public void A_sync_marker_is_fresh_only_inside_its_lease()
    {
        var now = Instant.FromUtc(2026, 9, 1, 12, 0);

        Assert.False(RoleRestriction.IsSnapshotFresh(null, now));
        Assert.True(RoleRestriction.IsSnapshotFresh(now.Minus(Duration.FromMinutes(29)), now));
        Assert.True(RoleRestriction.IsSnapshotFresh(now.Minus(RoleRestriction.SnapshotMaxAge), now));
        // Past the lease the bot may have missed member changes — back to unverifiable.
        Assert.False(RoleRestriction.IsSnapshotFresh(now.Minus(Duration.FromMinutes(31)), now));
    }

    [Theory]
    [InlineData(new long[] { Tank }, "@Tank")]
    [InlineData(new long[] { Tank, Healer }, "@Tank or @Healer")]
    [InlineData(new long[] { Tank, Healer, Unchecked }, "@Tank, @Healer, or role #99")]
    [InlineData(new long[0], "")]
    public void Role_lists_read_naturally_with_an_id_fallback(long[] roleIds, string expected)
    {
        Assert.Equal(expected, RoleRestriction.DescribeRoles(roleIds, Checked));
    }
}
