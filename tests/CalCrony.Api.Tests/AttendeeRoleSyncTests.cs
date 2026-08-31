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

    [Theory]
    [MemberData(nameof(DecisionCases))]
    public void Decide_grants_on_crossing_onto_going_and_revokes_on_crossing_off(
        Guid? oldOption, Guid? newOption, AttendeeRoleAction expected)
    {
        Assert.Equal(expected, AttendeeRoleSync.Decide(oldOption, newOption, Going));
    }

    public static TheoryData<Guid?, Guid?, AttendeeRoleAction> DecisionCases() => new()
    {
        { null, Going, AttendeeRoleAction.Grant },        // fresh Going RSVP
        { Maybe, Going, AttendeeRoleAction.Grant },       // switch onto Going
        { Going, Maybe, AttendeeRoleAction.Revoke },      // switch off Going
        { Going, null, AttendeeRoleAction.Revoke },       // un-RSVP from Going
        { Going, Going, AttendeeRoleAction.None },        // re-click Going
        { Maybe, NotGoing, AttendeeRoleAction.None },     // move between non-Going options
        { null, Maybe, AttendeeRoleAction.None },         // fresh non-Going RSVP
        { Maybe, null, AttendeeRoleAction.None },         // un-RSVP from non-Going
    };

    [Fact]
    public void Role_activity_requires_a_role_and_a_live_status()
    {
        var live = new Event { Title = "T", AttendeeRoleId = 1, Status = EventStatus.Scheduled };
        var started = new Event { Title = "T", AttendeeRoleId = 1, Status = EventStatus.Started };
        var ended = new Event { Title = "T", AttendeeRoleId = 1, Status = EventStatus.Ended };
        var roleless = new Event { Title = "T", Status = EventStatus.Scheduled };

        Assert.True(AttendeeRoleSync.IsRoleActive(live));
        Assert.True(AttendeeRoleSync.IsRoleActive(started));
        Assert.False(AttendeeRoleSync.IsRoleActive(ended));
        Assert.False(AttendeeRoleSync.IsRoleActive(roleless));
    }
}
