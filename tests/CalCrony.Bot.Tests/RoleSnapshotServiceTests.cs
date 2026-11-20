using CalCrony.Bot;

namespace CalCrony.Bot.Tests;

/// <summary>The pure parts of the role-snapshot pusher: the sync payload's shape and the
/// member-update delta test that decides whether a push is due.</summary>
public class RoleSnapshotServiceTests
{
    [Fact]
    public void The_sync_payload_tombstones_missing_roles_and_keeps_only_members_holding_an_existing_watched_role()
    {
        var existing = new Dictionary<long, string> { [1] = "Raider", [2] = "Officer" };

        var request = RoleSnapshotService.BuildSyncRequest(
            watchedRoleIds: [1, 2, 3, 1],
            existing,
            members:
            [
                (100, [1, 50]),      // holds Raider (+ an unwatched role) → row with [1]
                (101, [50]),         // holds nothing watched → no row
                (102, [3]),          // holds only the deleted role → no row
                (103, [2, 1, 2]),    // both, repeated → [1, 2] sorted and distinct
            ]);

        Assert.Equal(
            [(1L, "Raider"), (2L, "Officer"), (3L, (string?)null)],
            request.Roles.Select(r => (r.RoleId, r.Name)));
        Assert.Equal(
            [(100L, new long[] { 1 }), (103L, new long[] { 1, 2 })],
            request.Members.Select(m => (m.UserId, m.RoleIds.ToArray())));
    }

    [Fact]
    public void A_sync_says_its_members_are_known_unless_the_bot_runs_without_the_server_members_intent()
    {
        var existing = new Dictionary<long, string> { [1] = "Raider" };
        (long, IReadOnlyCollection<long>)[] members = [(100, [1])];

        var full = RoleSnapshotService.BuildSyncRequest([1, 2], existing, members);
        Assert.True(full.MembersKnown);
        Assert.Single(full.Members);

        // Without the intent the bot cannot see who holds what: the names and tombstones go as
        // before, no member at all, and the API is told the members are unknown.
        var namesOnly = RoleSnapshotService.BuildSyncRequest([1, 2], existing, members, membersKnown: false);
        Assert.False(namesOnly.MembersKnown);
        Assert.Empty(namesOnly.Members);
        Assert.Equal([(1L, "Raider"), (2L, (string?)null)], namesOnly.Roles.Select(r => (r.RoleId, r.Name)));
    }

    [Fact]
    public void A_member_update_pushes_only_when_the_watched_part_of_the_role_set_changed()
    {
        var watched = new HashSet<ulong> { 1, 2 };

        // Nickname/avatar change, or an unwatched role gained: nothing to push.
        Assert.False(RoleSnapshotService.RoleDeltaTouchesWatched(watched, [1, 9], [1, 9]));
        Assert.False(RoleSnapshotService.RoleDeltaTouchesWatched(watched, [1], [1, 9]));

        // A watched role gained or lost: push.
        Assert.True(RoleSnapshotService.RoleDeltaTouchesWatched(watched, [9], [9, 2]));
        Assert.True(RoleSnapshotService.RoleDeltaTouchesWatched(watched, [1, 2], [2]));

        // Unknown "before" (member wasn't cached): can't compare, so push — it is idempotent.
        Assert.True(RoleSnapshotService.RoleDeltaTouchesWatched(watched, null, [9]));
    }
}
