using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>Who /availability slots may look at. The module hands over what Discord resolved;
/// these rules decide the members asked about, the title's subject, and the refusals.</summary>
public class FreeSlotTargetTests
{
    private const long Caller = 42;
    private const long SomeoneElse = 43;

    [Fact]
    public void A_role_asks_about_its_members_and_leaves_bots_out()
    {
        var target = FreeSlotTarget.ForRole("GM", "<@&77>", [(1, false), (2, true), (3, false)]);

        Assert.Null(target.Refusal);
        Assert.Equal([1L, 3L], target.UserIds);

        // Plain name: mention markup does not render in an embed title.
        Assert.Equal("@GM", target.Subject);
    }

    [Fact]
    public void The_everyone_role_is_not_given_a_second_at_sign()
    {
        // Discord names the role everyone holds "@everyone".
        var target = FreeSlotTarget.ForRole("@everyone", "@everyone", [(1, false), (2, false)]);

        Assert.Equal("@everyone", target.Subject);
    }

    [Fact]
    public void A_role_nobody_holds_is_refused()
    {
        var empty = FreeSlotTarget.ForRole("GM", "<@&77>", []);

        Assert.Equal("Nobody currently has the <@&77> role.", empty.Refusal);
        Assert.Empty(empty.UserIds);
    }

    [Fact]
    public void A_role_held_only_by_bots_is_refused_for_that_reason()
    {
        // "Nobody has the role" would be untrue: the member list shows who does.
        var botsOnly = FreeSlotTarget.ForRole("Helpers", "<@&77>", [(2, true), (3, true)]);

        Assert.Equal("Only bots have the <@&77> role, and bots don't keep a calendar.", botsOnly.Refusal);
        Assert.Empty(botsOnly.UserIds);
    }

    [Fact]
    public void A_role_larger_than_the_limit_is_refused_with_its_size()
    {
        var atLimit = FreeSlotTarget.ForRole("Everyone", "<@&77>", Holders(FreeSlotLimits.MaxUsers));
        var overLimit = FreeSlotTarget.ForRole("Everyone", "<@&77>", Holders(FreeSlotLimits.MaxUsers + 1));

        Assert.Null(atLimit.Refusal);
        Assert.Equal(FreeSlotLimits.MaxUsers, atLimit.UserIds.Count);
        Assert.Equal("The <@&77> role has 51 members; free-slot lists support at most 50 at a time.", overLimit.Refusal);
    }

    [Fact]
    public void Bots_do_not_count_toward_the_limit()
    {
        var holders = Holders(FreeSlotLimits.MaxUsers).Append((UserId: 9999L, IsBot: true));

        Assert.Null(FreeSlotTarget.ForRole("Everyone", "<@&77>", holders).Refusal);
    }

    [Fact]
    public void A_role_is_not_a_lookup_by_name_however_few_hold_it()
    {
        // A role one member holds is still a role lookup: no saved hours are asked of them, and
        // nothing about the answer needs checking.
        var target = FreeSlotTarget.ForRole("GM", "<@&77>", [(SomeoneElse, false)]);

        Assert.False(target.ByName);
        Assert.False(target.NamesSomeoneElse);
        Assert.Null(target.RefusalFor(Answer(SomeoneElse, byName: false)));
    }

    [Fact]
    public void The_caller_is_asked_about_alone_by_name()
    {
        var target = FreeSlotTarget.ForUser(Caller, "Sam", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Null(target.Refusal);
        Assert.Equal([Caller], target.UserIds);
        Assert.Equal("@Sam", target.Subject);
        Assert.True(target.ByName);
        Assert.False(target.NamesSomeoneElse);
    }

    [Fact]
    public void A_display_name_that_starts_with_an_at_sign_is_not_given_a_second()
    {
        var target = FreeSlotTarget.ForUser(Caller, "@Sam", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Equal("@Sam", target.Subject);
    }

    [Fact]
    public void Another_member_of_this_server_is_handed_on_by_name_for_the_api_to_decide()
    {
        // Whether they can be listed turns on whether they have saved their own hours, which the
        // API holds: it is asked by name, and nothing about them is decided here.
        var target = FreeSlotTarget.ForUser(SomeoneElse, "Ana", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Null(target.Refusal);
        Assert.Equal([SomeoneElse], target.UserIds);
        Assert.Equal("@Ana", target.Subject);
        Assert.True(target.ByName);
        Assert.True(target.NamesSomeoneElse);
    }

    [Fact]
    public void A_list_about_someone_else_is_shown_only_when_the_answer_says_it_was_checked_by_name()
    {
        var target = FreeSlotTarget.ForUser(SomeoneElse, "Ana", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Null(target.RefusalFor(Answer(SomeoneElse, byName: true)));

        // An API that does not know the rule answers without the mark, whatever it found: the
        // list is not shown, whether the member was counted or not.
        Assert.Equal("That member's free slots can't be listed by name right now.", target.RefusalFor(Answer(SomeoneElse, byName: false)));
        Assert.Equal(
            FreeSlotTarget.NotCheckedByName,
            target.RefusalFor(Answer(SomeoneElse, byName: false, FreeSlotMemberState.NotCounted)));
    }

    [Fact]
    public void A_list_about_the_caller_is_shown_either_way()
    {
        var target = FreeSlotTarget.ForUser(Caller, "Sam", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Null(target.RefusalFor(Answer(Caller, byName: true)));
        Assert.Null(target.RefusalFor(Answer(Caller, byName: false)));
    }

    [Fact]
    public void A_user_from_outside_this_server_is_refused_as_that()
    {
        // A user option can carry anyone Discord resolves by id. Nobody is asked about, so the
        // module has nothing to send to the API.
        var target = FreeSlotTarget.ForUser(SomeoneElse, "Stranger", isBot: false, isMemberOfThisServer: false, Caller);

        Assert.Equal("That user isn't a member of this server.", target.Refusal);
        Assert.Empty(target.UserIds);
        Assert.Equal("", target.Subject);
        Assert.False(target.ByName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_bot_is_refused_as_that_whether_or_not_it_is_in_the_server(bool isMemberOfThisServer)
    {
        var target = FreeSlotTarget.ForUser(SomeoneElse, "Helper", isBot: true, isMemberOfThisServer, Caller);

        Assert.Equal("Bots don't keep a calendar — pick a member or a role.", target.Refusal);
        Assert.Empty(target.UserIds);
    }

    /// <summary>An answer about one member, marked or not as checked by name.</summary>
    private static FreeSlotsResponse Answer(
        long userId, bool byName, FreeSlotMemberState state = FreeSlotMemberState.DefaultHours) => new(
            "America/Chicago",
            new DateOnly(2026, 11, 12),
            new DateOnly(2026, 11, 12),
            state == FreeSlotMemberState.NotCounted ? 0 : 1,
            [],
            [new FreeSlotMemberDto(userId, state)],
            byName);

    private static IEnumerable<(long UserId, bool IsBot)> Holders(int count) =>
        Enumerable.Range(1, count).Select(i => ((long)i, false));
}
