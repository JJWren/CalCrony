using CalCrony.Bot;
using Discord;
using Discord.Net;
using Microsoft.Extensions.Configuration;

namespace CalCrony.Bot.Tests;

/// <summary>Whether the bot asks Discord for the Server Members intent: the setting, the decision
/// from the setting and the application's flags, and what a refusal of the intent leads to. Lets
/// the bot run without the intent instead of being refused at login.</summary>
public class ServerMembersIntentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData(" Auto ")]
    public void An_empty_or_missing_setting_and_auto_in_any_case_mean_auto(string? value)
    {
        Assert.Equal(ServerMembersIntentMode.Auto, ServerMembersIntent.ParseMode(value));
    }

    [Theory]
    [InlineData("on", ServerMembersIntentMode.On)]
    [InlineData("ON", ServerMembersIntentMode.On)]
    [InlineData("off", ServerMembersIntentMode.Off)]
    [InlineData(" Off", ServerMembersIntentMode.Off)]
    public void On_and_off_are_read_in_any_case(string value, ServerMembersIntentMode expected)
    {
        Assert.Equal(expected, ServerMembersIntent.ParseMode(value));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("enabled")]
    [InlineData("o n")]
    public void Any_other_value_stops_the_bot_with_a_sentence_naming_the_setting_and_its_values(string value)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ServerMembersIntent.ParseMode(value));

        Assert.Equal(
            "Discord:ServerMembersIntent (DISCORD_SERVER_MEMBERS_INTENT in the compose files) must be auto, on or off.",
            refused.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApplicationFlags.GatewayGuildMembers)]
    [InlineData(ApplicationFlags.GatewayGuildMembersLimited)]
    [InlineData(ApplicationFlags.GatewayPresence | ApplicationFlags.GatewayMessageContentLimited)]
    public void On_and_off_decide_whatever_the_application_has(ApplicationFlags? flags)
    {
        var on = ServerMembersIntent.Decide(ServerMembersIntentMode.On, flags);
        var off = ServerMembersIntent.Decide(ServerMembersIntentMode.Off, flags);

        Assert.True(on.Enabled);
        Assert.Equal(GatewayIntents.Guilds | GatewayIntents.GuildMembers, on.GatewayIntents);
        Assert.False(off.Enabled);
        Assert.Equal(GatewayIntents.Guilds, off.GatewayIntents);
        Assert.Null(on.LookupError);
        Assert.Null(off.LookupError);
    }

    [Theory]
    [InlineData(ApplicationFlags.GatewayGuildMembers)]
    [InlineData(ApplicationFlags.GatewayGuildMembersLimited)]
    [InlineData(ApplicationFlags.GatewayGuildMembers | ApplicationFlags.GatewayGuildMembersLimited)]
    [InlineData(ApplicationFlags.GatewayGuildMembersLimited | ApplicationFlags.GatewayPresence)]
    public void Auto_asks_for_the_intent_when_the_application_has_it_approved_or_switched_on(ApplicationFlags flags)
    {
        var decided = ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, flags);

        Assert.True(decided.Enabled);
        Assert.Equal(GatewayIntents.Guilds | GatewayIntents.GuildMembers, decided.GatewayIntents);
        Assert.Null(decided.LookupError);
        Assert.Equal("Server Members intent: on (auto: the application has it).", decided.Describe());
    }

    [Theory]
    [InlineData((ApplicationFlags)0)]
    [InlineData(ApplicationFlags.GatewayPresence | ApplicationFlags.GatewayPresenceLimited)]
    [InlineData(ApplicationFlags.GatewayMessageContent | ApplicationFlags.GatewayMessageContentLimited)]
    [InlineData(ApplicationFlags.VerificationPendingGuildLimit)]
    public void Auto_leaves_the_intent_out_when_the_application_does_not_have_it(ApplicationFlags flags)
    {
        var decided = ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, flags);

        Assert.False(decided.Enabled);
        Assert.Equal(GatewayIntents.Guilds, decided.GatewayIntents);
        Assert.Null(decided.LookupError);
        Assert.Equal(
            "Server Members intent: off (auto: the application does not have it). "
            + "Role lists in /availability and role checks on the website are off.",
            decided.Describe());
    }

    [Fact]
    public void Auto_asks_for_the_intent_as_before_when_the_application_cannot_be_read()
    {
        var decided = ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, null, "timed out");

        Assert.True(decided.Enabled);
        Assert.Equal(GatewayIntents.Guilds | GatewayIntents.GuildMembers, decided.GatewayIntents);
        Assert.Equal("timed out", decided.LookupError);
        Assert.Equal(
            "Server Members intent: on (auto: the application could not be read, so the bot asks for it as before: timed out).",
            decided.Describe());
    }

    [Fact]
    public void The_settings_that_are_not_auto_say_so()
    {
        Assert.Equal("Server Members intent: on (set to on).", ServerMembersIntent.Decide(ServerMembersIntentMode.On, null).Describe());
        Assert.Equal(
            "Server Members intent: off (set to off). Role lists in /availability and role checks on the website are off.",
            ServerMembersIntent.Decide(ServerMembersIntentMode.Off, null).Describe());
    }

    [Fact]
    public void Only_auto_with_the_intent_asked_for_restarts_on_a_refusal()
    {
        Assert.True(ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, ApplicationFlags.GatewayGuildMembersLimited).RestartsOnRefusal);
        Assert.True(ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, null, "timed out").RestartsOnRefusal);

        // Auto without the intent never asked for it; on was asked for by hand and stays; off never asks.
        Assert.False(ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, (ApplicationFlags)0).RestartsOnRefusal);
        Assert.False(ServerMembersIntent.Decide(ServerMembersIntentMode.On, ApplicationFlags.GatewayGuildMembers).RestartsOnRefusal);
        Assert.False(ServerMembersIntent.Decide(ServerMembersIntentMode.Off, ApplicationFlags.GatewayGuildMembers).RestartsOnRefusal);
    }

    [Fact]
    public void A_gateway_close_with_code_4014_is_a_refusal_of_an_intent_however_it_is_wrapped()
    {
        var refused = new WebSocketClosedException(4014, "Disallowed intent(s).");

        Assert.True(ServerMembersIntent.IsRefusal(refused));
        Assert.True(ServerMembersIntent.IsRefusal(new Exception("WebSocket connection was closed", refused)));
        Assert.True(ServerMembersIntent.IsRefusal(new AggregateException(new TimeoutException(), refused)));
        Assert.True(ServerMembersIntent.IsRefusal(
            new InvalidOperationException("outer", new AggregateException(new Exception("inner", refused)))));
    }

    [Fact]
    public void Other_closes_and_other_failures_are_not_a_refusal()
    {
        Assert.False(ServerMembersIntent.IsRefusal(null));
        Assert.False(ServerMembersIntent.IsRefusal(new WebSocketClosedException(4013, "Invalid intent(s).")));
        Assert.False(ServerMembersIntent.IsRefusal(new WebSocketClosedException(4004, "Authentication failed.")));
        Assert.False(ServerMembersIntent.IsRefusal(new WebSocketClosedException(1000)));
        Assert.False(ServerMembersIntent.IsRefusal(new Exception("4014")));
        Assert.False(ServerMembersIntent.IsRefusal(new AggregateException(new TimeoutException())));
    }

    [Fact]
    public void The_refusal_log_says_what_happens_next_for_each_setting()
    {
        Assert.Equal(
            "Discord refused the Server Members intent (gateway close code 4014). The bot stops now so that "
            + "it restarts and decides again from the application's settings (Discord:ServerMembersIntent is auto).",
            ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, ApplicationFlags.GatewayGuildMembers).RefusalMessage);
        Assert.Equal(
            "Discord refused the Server Members intent (gateway close code 4014). Discord:ServerMembersIntent is on, "
            + "so the bot keeps asking for it: set it to auto or off, or switch the intent on for the application "
            + "in the Discord Developer Portal.",
            ServerMembersIntent.Decide(ServerMembersIntentMode.On, null).RefusalMessage);
        Assert.Equal(
            "Discord refused an intent the bot asked for (gateway close code 4014).",
            ServerMembersIntent.Decide(ServerMembersIntentMode.Off, null).RefusalMessage);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public async Task On_and_off_are_decided_without_asking_Discord(string value, bool enabled)
    {
        // A token that could never log in: any request to Discord would fail, so a decision that
        // reached Discord would come back with a lookup error.
        var decided = await ServerMembersIntent.ResolveAsync(Configuration(value, "not-a-token"));

        Assert.Equal(enabled, decided.Enabled);
        Assert.Null(decided.LookupError);
    }

    [Fact]
    public async Task Auto_without_a_token_asks_for_the_intent_as_before_and_says_why()
    {
        var decided = await ServerMembersIntent.ResolveAsync(Configuration(null, null));

        Assert.Equal(ServerMembersIntentMode.Auto, decided.Mode);
        Assert.True(decided.Enabled);
        Assert.Equal("no bot token is configured", decided.LookupError);
    }

    [Fact]
    public async Task An_unknown_setting_stops_the_bot_before_anything_else()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServerMembersIntent.ResolveAsync(Configuration("maybe", "not-a-token")));
    }

    private static IConfiguration Configuration(string? mode, string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServerMembersIntent.SettingKey] = mode,
                ["Discord:BotToken"] = token,
            })
            .Build();
}
