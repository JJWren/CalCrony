using System.Net;
using CalCrony.Bot;
using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalCrony.Bot.Tests;

/// <summary>The watch that, in auto, reads the application's flags again and restarts the bot when
/// they disagree with the decision made at start — and leaves everything else alone.</summary>
public class ServerMembersIntentWatchTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    [Theory]
    [InlineData(ApplicationFlags.GatewayGuildMembersLimited, (ApplicationFlags)0)]
    [InlineData((ApplicationFlags)0, ApplicationFlags.GatewayGuildMembers)]
    public async Task In_auto_flags_that_disagree_with_the_decision_restart_the_bot_once(
        ApplicationFlags atStart, ApplicationFlags readAgain)
    {
        var discord = StubDiscord.Answering(readAgain);
        var (watch, lifetime) = Watch(ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, atStart), discord);

        await watch.StartAsync(CancellationToken.None);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(Tick * 4);
        await watch.StopAsync(CancellationToken.None);

        Assert.Equal(1, lifetime.Stops);
        Assert.Equal(1, discord.Calls); // the watch ends with the restart it asked for
    }

    [Fact]
    public async Task Agreeing_flags_and_failed_reads_change_nothing()
    {
        var agreeing = StubDiscord.Answering(ApplicationFlags.GatewayGuildMembersLimited);
        var failing = StubDiscord.Status(HttpStatusCode.BadGateway, "");
        var decided = ServerMembersIntent.Decide(ServerMembersIntentMode.Auto, ApplicationFlags.GatewayGuildMembersLimited);
        var (first, firstLifetime) = Watch(decided, agreeing);
        var (second, secondLifetime) = Watch(decided, failing);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        await Task.Delay(Tick * 8);
        await first.StopAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);

        Assert.True(agreeing.Calls >= 2); // it kept reading
        Assert.True(failing.Calls >= 2);  // and kept trying
        Assert.Equal(0, firstLifetime.Stops);
        Assert.Equal(0, secondLifetime.Stops);
    }

    [Theory]
    [InlineData(ServerMembersIntentMode.On, "token")]
    [InlineData(ServerMembersIntentMode.Off, "token")]
    [InlineData(ServerMembersIntentMode.Auto, null)]
    public async Task On_off_and_a_bot_without_a_token_read_nothing(ServerMembersIntentMode mode, string? token)
    {
        var discord = StubDiscord.Answering((ApplicationFlags)0);
        var (watch, lifetime) = Watch(ServerMembersIntent.Decide(mode, ApplicationFlags.GatewayGuildMembersLimited), discord, token);

        await watch.StartAsync(CancellationToken.None);
        await Task.Delay(Tick * 4);
        await watch.StopAsync(CancellationToken.None);

        Assert.Equal(0, discord.Calls);
        Assert.Equal(0, lifetime.Stops);
    }

    private static (ServerMembersIntentWatch Watch, IntentRestartTests.FakeLifetime Lifetime) Watch(
        ServerMembersIntent decided, StubDiscord discord, string? token = "token")
    {
        var lifetime = new IntentRestartTests.FakeLifetime();
        var restart = new IntentRestart(lifetime, NullLogger<IntentRestart>.Instance) { MarkFailed = () => { } };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:BotToken"] = token })
            .Build();
        var watch = new ServerMembersIntentWatch(
            decided, discord.Reader(), configuration, restart, NullLogger<ServerMembersIntentWatch>.Instance)
        {
            Interval = Tick,
        };
        return (watch, lifetime);
    }
}
