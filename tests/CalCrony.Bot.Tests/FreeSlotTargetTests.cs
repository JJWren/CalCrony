using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>Who /availability slots may look at. The module hands over what Discord resolved;
/// these rules decide the members asked about, the title's subject, and the refusals.</summary>
public class FreeSlotTargetTests
{
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
        var botsOnly = FreeSlotTarget.ForRole("GM", "<@&77>", [(2, true)]);

        Assert.Equal("Nobody currently has the <@&77> role.", empty.Refusal);
        Assert.Equal(empty.Refusal, botsOnly.Refusal);
        Assert.Empty(empty.UserIds);
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
    public void A_member_of_this_server_is_asked_about_alone()
    {
        var target = FreeSlotTarget.ForUser(42, "Sam", isBot: false, isMemberOfThisServer: true);

        Assert.Null(target.Refusal);
        Assert.Equal([42L], target.UserIds);
        Assert.Equal("@Sam", target.Subject);
    }

    [Fact]
    public void A_user_from_outside_this_server_is_refused()
    {
        // A user option can carry anyone Discord resolves by id. A server's members see each
        // other's free time — not that of a CalCrony user who isn't in the server.
        var target = FreeSlotTarget.ForUser(42, "Stranger", isBot: false, isMemberOfThisServer: false);

        Assert.Equal("That user isn't a member of this server.", target.Refusal);
        Assert.Empty(target.UserIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_bot_is_refused_whether_or_not_it_is_in_the_server(bool isMemberOfThisServer)
    {
        var target = FreeSlotTarget.ForUser(42, "Helper", isBot: true, isMemberOfThisServer);

        Assert.Equal("Bots don't keep a calendar — pick a member or a role.", target.Refusal);
        Assert.Empty(target.UserIds);
    }

    private static IEnumerable<(long UserId, bool IsBot)> Holders(int count) =>
        Enumerable.Range(1, count).Select(i => ((long)i, false));
}
