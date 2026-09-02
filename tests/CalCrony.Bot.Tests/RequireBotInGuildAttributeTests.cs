using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>Wording of the guild-membership precondition (issue #163): silent when the bot
/// resolved the server, a pointer to servers from a DM, and the invite link — for the application
/// that was actually invoked — from a server the bot isn't in.</summary>
public class RequireBotInGuildAttributeTests
{
    private const ulong TestApp = 999_000_111;

    [Fact]
    public void Passes_when_the_bot_resolved_the_server()
    {
        Assert.Null(RequireBotInGuildAttribute.Problem(botInGuild: true, isDm: false, TestApp));
    }

    [Fact]
    public void Dms_are_told_to_use_a_server()
    {
        var problem = RequireBotInGuildAttribute.Problem(botInGuild: false, isDm: true, TestApp);

        Assert.Equal(
            "This command works inside a server, not in DMs — run it in a channel of a server that has CalCrony.",
            problem);
    }

    [Fact]
    public void A_server_without_the_bot_gets_the_invite_for_the_invoked_application()
    {
        var problem = RequireBotInGuildAttribute.Problem(botInGuild: false, isDm: false, TestApp);

        // The link is wrapped in <> so Discord doesn't unfurl it, and it names the application
        // the caller used — a test bot never advertises production's invite.
        Assert.Equal(
            "CalCrony hasn't been added to this server, so it can't see its channels, roles, or events. "
            + $"A server admin can add it here: <{DiscordInvite.Url(TestApp)}>",
            problem);
        Assert.Contains($"client_id={TestApp}&permissions={DiscordInvite.Permissions}", problem);
    }

    [Fact]
    public void The_bot_and_the_web_build_the_same_invite()
    {
        Assert.Equal(DiscordInvite.Url("1527749302443835532"), DiscordInvite.Url(1527749302443835532UL));
        Assert.Equal(DiscordInvite.Url(null), DiscordInvite.Url(1527749302443835532UL));
    }
}
