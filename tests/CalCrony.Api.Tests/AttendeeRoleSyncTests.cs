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

    [Theory]
    [MemberData(nameof(DecisionCases))]
    public void Decide_swaps_the_role_of_the_seat_given_up_for_the_role_of_the_seat_taken(
        bool rolePerOption, Guid? oldOption, Guid? newOption, long? revoke, long? grant)
    {
        var options = rolePerOption ? RolePerOption : OneRole;
        Assert.Equal(
            new AttendeeRoleChange(revoke, grant), AttendeeRoleSync.Decide(options, oldOption, newOption));
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

    [Fact]
    public void Options_sharing_one_role_dont_churn_it_on_a_switch()
    {
        // A raid where Tank and Healer both grant the same "Raider" role: moving between them
        // must not emit a revoke racing a grant for a role the user keeps throughout.
        List<RsvpOption> shared =
        [
            new() { Id = Going, Emote = "🛡️", Label = "Tank", SortOrder = 0, IsAttending = true, AttendeeRoleId = 42 },
            new() { Id = Maybe, Emote = "💚", Label = "Healer", SortOrder = 1, AttendeeRoleId = 42 },
        ];

        var change = AttendeeRoleSync.Decide(shared, Going, Maybe);
        Assert.True(change.IsNoOp);
        Assert.Equal(new AttendeeRoleChange(null, 42), AttendeeRoleSync.Decide(shared, null, Maybe));
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
