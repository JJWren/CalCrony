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
    public void The_caller_is_asked_about_alone()
    {
        var target = FreeSlotTarget.ForUser(Caller, "Sam", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Null(target.Refusal);
        Assert.Equal([Caller], target.UserIds);
        Assert.Equal("@Sam", target.Subject);
    }

    [Fact]
    public void A_display_name_that_starts_with_an_at_sign_is_not_given_a_second()
    {
        var target = FreeSlotTarget.ForUser(Caller, "@Sam", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Equal("@Sam", target.Subject);
    }

    [Fact]
    public void Another_member_of_this_server_is_refused_and_nobody_is_asked_about()
    {
        // Naming a member lists one person's free time, so only the member themself may. With
        // no member to ask about, the module has nothing to send to the API.
        var target = FreeSlotTarget.ForUser(SomeoneElse, "Ana", isBot: false, isMemberOfThisServer: true, Caller);

        Assert.Equal("Another member's free slots can't be listed yet — pick yourself or a role.", target.Refusal);
        Assert.Empty(target.UserIds);
        Assert.Equal("", target.Subject);
    }

    [Fact]
    public void A_user_from_outside_this_server_is_refused_as_that()
    {
        // A user option can carry anyone Discord resolves by id. The specific reason is given
        // ahead of the one for another member.
        var target = FreeSlotTarget.ForUser(SomeoneElse, "Stranger", isBot: false, isMemberOfThisServer: false, Caller);

        Assert.Equal("That user isn't a member of this server.", target.Refusal);
        Assert.Empty(target.UserIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_bot_is_refused_as_that_whether_or_not_it_is_in_the_server(bool isMemberOfThisServer)
    {
        var target = FreeSlotTarget.ForUser(SomeoneElse, "Helper", isBot: true, isMemberOfThisServer, Caller);

        Assert.Equal("Bots don't keep a calendar — pick yourself or a role.", target.Refusal);
        Assert.Empty(target.UserIds);
    }

    private static IEnumerable<(long UserId, bool IsBot)> Holders(int count) =>
        Enumerable.Range(1, count).Select(i => ((long)i, false));
}
