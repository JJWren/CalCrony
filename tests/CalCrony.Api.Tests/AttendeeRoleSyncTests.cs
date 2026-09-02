using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

public class AttendeeRoleSyncTests
{
    private static readonly Guid Going = Guid.NewGuid();
    private static readonly Guid Maybe = Guid.NewGuid();
    private static readonly Guid NotGoing = Guid.NewGuid();

    [Fact]
    public void Attending_option_is_the_flagged_one_with_a_sort_order_fallback()
    {
        // The IsAttending flag wins regardless of ordering…
        List<RsvpOption> flagged =
        [
            new() { Id = Maybe, Emote = "🤔", Label = "Maybe", SortOrder = 2, IsAttending = true },
            new() { Id = Going, Emote = "✅", Label = "Going", SortOrder = 0 },
            new() { Id = NotGoing, Emote = "❌", Label = "Not going", SortOrder = 1 },
        ];
        Assert.Equal(Maybe, AttendeeRoleSync.AttendingOptionId(flagged));

        // …and unflagged (pre-migration-shaped) data falls back to the minimum SortOrder.
        List<RsvpOption> unflagged =
        [
            new() { Id = Maybe, Emote = "🤔", Label = "Maybe", SortOrder = 2 },
            new() { Id = Going, Emote = "✅", Label = "Going", SortOrder = 0 },
            new() { Id = NotGoing, Emote = "❌", Label = "Not going", SortOrder = 1 },
        ];
        Assert.Equal(Going, AttendeeRoleSync.AttendingOptionId(unflagged));
        Assert.Null(AttendeeRoleSync.AttendingOptionId([]));
    }

    /// <summary>Only Going carries a role here — the shape every pre-v2 event has, so these are
    /// the v1 grant/revoke cases restated against the per-option rule.</summary>
    private static readonly List<RsvpOption> OneRole =
    [
        new() { Id = Going, Emote = "✅", Label = "Going", SortOrder = 0, IsAttending = true, AttendeeRoleId = 10 },
        new() { Id = NotGoing, Emote = "❌", Label = "Not going", SortOrder = 1 },
        new() { Id = Maybe, Emote = "🤔", Label = "Maybe", SortOrder = 2 },
    ];

    /// <summary>Tank/Healer/DPS: three options, three different roles, none of them attending-only.</summary>
    private static readonly List<RsvpOption> RolePerOption =
    [
        new() { Id = Going, Emote = "🛡️", Label = "Tank", SortOrder = 0, IsAttending = true, AttendeeRoleId = 10 },
        new() { Id = NotGoing, Emote = "💚", Label = "Healer", SortOrder = 1, AttendeeRoleId = 20 },
        new() { Id = Maybe, Emote = "⚔️", Label = "DPS", SortOrder = 2, AttendeeRoleId = 30 },
    ];

    /// <summary>The single-seat cases: one option held before, one after (null = none). Each is
    /// stated as the set difference of the roles those seats carry — the v1 grant/revoke matrix,
    /// restated so a single-choice switch and a multi-RSVP add share one rule.</summary>
    /// <param name="rolePerOption">Whether every option carries its own role.</param>
    /// <param name="oldOption">The seat held before, or null.</param>
    /// <param name="newOption">The seat held after, or null.</param>
    /// <param name="revoke">The role expected to come back, or null.</param>
    /// <param name="grant">The role expected to be handed out, or null.</param>
    [Theory]
    [MemberData(nameof(DecisionCases))]
    public void Diff_of_one_seat_swaps_the_role_given_up_for_the_role_taken(
        bool rolePerOption, Guid? oldOption, Guid? newOption, long? revoke, long? grant)
    {
        var options = rolePerOption ? RolePerOption : OneRole;
        var before = AttendeeRoleSync.RolesHeld(options, oldOption is { } was ? [was] : []);
        var after = AttendeeRoleSync.RolesHeld(options, newOption is { } now ? [now] : []);

        var diff = AttendeeRoleSync.Diff(before, after);

        Assert.Equal(revoke is { } r ? [r] : Array.Empty<long>(), diff.Revokes);
        Assert.Equal(grant is { } g ? [g] : Array.Empty<long>(), diff.Grants);
        Assert.Equal(revoke is null && grant is null, diff.IsNoOp);
    }

    public static TheoryData<bool, Guid?, Guid?, long?, long?> DecisionCases() => new()
    {
        // Only the attending option carries a role — the v1 semantics, unchanged.
        { false, null, Going, null, 10L },        // fresh Going RSVP
        { false, Maybe, Going, null, 10L },       // switch onto Going
        { false, Going, Maybe, 10L, null },       // switch off Going
        { false, Going, null, 10L, null },        // un-RSVP from Going
        { false, Going, Going, null, null },      // re-click Going
        { false, Maybe, NotGoing, null, null },   // move between roleless options
        { false, null, Maybe, null, null },       // fresh roleless RSVP
        { false, Maybe, null, null, null },       // un-RSVP from a roleless option
        // Every option carries its own role — the reason this decision moved off the flag.
        { true, null, NotGoing, null, 20L },      // fresh Healer RSVP earns Healer, not "attending"
        { true, Going, Maybe, 10L, 30L },         // Tank → DPS swaps one role for the other
        { true, Maybe, null, 30L, null },         // un-RSVP hands DPS back
        { true, Maybe, Maybe, null, null },       // re-click DPS
    };

    /// <summary>Tank and Healer both grant one "Raider" role: the shape where set semantics
    /// matter most, because a per-seat decision would revoke a role the other seat still earns.</summary>
    private static readonly List<RsvpOption> SharedRole =
    [
        new() { Id = Going, Emote = "🛡️", Label = "Tank", SortOrder = 0, IsAttending = true, AttendeeRoleId = 42 },
        new() { Id = Maybe, Emote = "💚", Label = "Healer", SortOrder = 1, AttendeeRoleId = 42 },
        new() { Id = NotGoing, Emote = "❌", Label = "Can't", SortOrder = 2 },
    ];

    [Fact]
    public void Options_sharing_one_role_dont_churn_it_on_a_switch()
    {
        // Moving between them must not emit a revoke racing a grant for a role the user keeps.
        var tank = AttendeeRoleSync.RolesHeld(SharedRole, [Going]);
        var healer = AttendeeRoleSync.RolesHeld(SharedRole, [Maybe]);
        Assert.True(AttendeeRoleSync.Diff(tank, healer).IsNoOp);

        var fresh = AttendeeRoleSync.Diff(AttendeeRoleSync.NoRoles, healer);
        Assert.Empty(fresh.Revokes);
        Assert.Equal([42L], fresh.Grants);
    }

    [Fact]
    public void Roles_held_are_the_union_over_every_seat()
    {
        // Two seats, one role between them: the set holds it once.
        Assert.Equal([42L], AttendeeRoleSync.RolesHeld(SharedRole, [Going, Maybe]).Order());
        // Two seats, two roles.
        Assert.Equal([10L, 20L], AttendeeRoleSync.RolesHeld(RolePerOption, [Going, NotGoing]).Order());
        // Roleless seats, unknown options and no seats at all hold nothing.
        Assert.Empty(AttendeeRoleSync.RolesHeld(OneRole, [Maybe, NotGoing]));
        Assert.Empty(AttendeeRoleSync.RolesHeld(RolePerOption, [Guid.NewGuid()]));
        Assert.Empty(AttendeeRoleSync.RolesHeld(RolePerOption, []));
    }

    [Fact]
    public void Adding_a_second_seat_with_the_same_role_grants_nothing_and_dropping_one_revokes_nothing()
    {
        // Tank(@raider) → Tank+Healer(@raider): the role was already held, so nothing is delivered…
        var tank = AttendeeRoleSync.RolesHeld(SharedRole, [Going]);
        var both = AttendeeRoleSync.RolesHeld(SharedRole, [Going, Maybe]);
        Assert.True(AttendeeRoleSync.Diff(tank, both).IsNoOp);

        // …and dropping either seat keeps it, because the other still earns it.
        Assert.True(AttendeeRoleSync.Diff(both, tank).IsNoOp);
        Assert.True(AttendeeRoleSync.Diff(both, AttendeeRoleSync.RolesHeld(SharedRole, [Maybe])).IsNoOp);

        // Dropping the LAST role-bearing seat is the one revoke.
        var lastSeat = AttendeeRoleSync.Diff(tank, AttendeeRoleSync.RolesHeld(SharedRole, [NotGoing]));
        Assert.Equal([42L], lastSeat.Revokes);
        Assert.Empty(lastSeat.Grants);
    }

    [Fact]
    public void Dropping_one_of_two_different_roles_revokes_only_that_one()
    {
        // Tank(@10) + Healer(@20) → Healer(@20): Tank's role comes back, Healer's stays.
        var both = AttendeeRoleSync.RolesHeld(RolePerOption, [Going, NotGoing]);
        var healerOnly = AttendeeRoleSync.RolesHeld(RolePerOption, [NotGoing]);

        var dropped = AttendeeRoleSync.Diff(both, healerOnly);
        Assert.Equal([10L], dropped.Revokes);
        Assert.Empty(dropped.Grants);

        // The reverse — adding Tank beside Healer — grants only Tank's role.
        var added = AttendeeRoleSync.Diff(healerOnly, both);
        Assert.Empty(added.Revokes);
        Assert.Equal([10L], added.Grants);
    }

    [Fact]
    public void Diff_between_the_empty_set_and_a_set_is_a_pure_grant_or_a_pure_revoke_in_role_order()
    {
        var all = AttendeeRoleSync.RolesHeld(RolePerOption, [Maybe, Going, NotGoing]);

        var joined = AttendeeRoleSync.Diff(AttendeeRoleSync.NoRoles, all);
        Assert.Empty(joined.Revokes);
        Assert.Equal([10L, 20L, 30L], joined.Grants);

        var left = AttendeeRoleSync.Diff(all, AttendeeRoleSync.NoRoles);
        Assert.Equal([10L, 20L, 30L], left.Revokes);
        Assert.Empty(left.Grants);

        Assert.True(AttendeeRoleSync.Diff(AttendeeRoleSync.NoRoles, AttendeeRoleSync.NoRoles).IsNoOp);
    }

    [Fact]
    public void Role_lookup_is_null_without_a_seat_a_match_or_a_role()
    {
        Assert.Equal(10, AttendeeRoleSync.RoleFor(RolePerOption, Going));
        Assert.Null(AttendeeRoleSync.RoleFor(RolePerOption, null));         // no seat
        Assert.Null(AttendeeRoleSync.RoleFor(RolePerOption, Guid.NewGuid())); // option not on this event
        Assert.Null(AttendeeRoleSync.RoleFor(OneRole, NotGoing));           // option carries no role
    }

    [Fact]
    public void Role_activity_requires_some_option_role_and_a_live_status()
    {
        static Event WithRole(EventStatus status) => new()
        {
            Title = "T",
            Status = status,
            Options = [new RsvpOption { Emote = "✅", Label = "Going", IsAttending = true, AttendeeRoleId = 1 }],
        };

        Assert.True(AttendeeRoleSync.IsRoleActive(WithRole(EventStatus.Scheduled)));
        Assert.True(AttendeeRoleSync.IsRoleActive(WithRole(EventStatus.Started)));
        Assert.False(AttendeeRoleSync.IsRoleActive(WithRole(EventStatus.Ended)));

        // A role on ANY option counts, not just the attending one.
        var nonAttendingRole = new Event
        {
            Title = "T",
            Status = EventStatus.Scheduled,
            Options =
            [
                new RsvpOption { Emote = "✅", Label = "Going", IsAttending = true },
                new RsvpOption { Emote = "💚", Label = "Healer", SortOrder = 1, AttendeeRoleId = 7 },
            ],
        };
        Assert.True(AttendeeRoleSync.IsRoleActive(nonAttendingRole));

        var roleless = new Event
        {
            Title = "T",
            Status = EventStatus.Scheduled,
            Options = [new RsvpOption { Emote = "✅", Label = "Going", IsAttending = true }],
        };
        Assert.False(AttendeeRoleSync.IsRoleActive(roleless));
    }
}
