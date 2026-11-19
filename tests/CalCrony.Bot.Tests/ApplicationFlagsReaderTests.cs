using System.Net;
using System.Text.Json;
using CalCrony.Bot;
using Discord;

namespace CalCrony.Bot.Tests;

/// <summary>The read of the bot's own application flags: what is asked of Discord, how the answer
/// is read, and how a refusal, a slow answer or a cancellation come back — none of them carrying
/// the token.</summary>
public class ApplicationFlagsReaderTests
{
    [Fact]
    public async Task It_asks_discord_for_the_bots_own_application_with_the_bot_token_and_reads_its_flags()
    {
        var discord = StubDiscord.Json("""{"id": "1", "name": "CalCrony", "flags": 10518528}""");

        var flags = await discord.Reader().ReadAsync("the-token");

        // 10518528 is what the hosted application answered with on 2026-11-13: the intent switched on.
        Assert.True(flags.HasFlag(ApplicationFlags.GatewayGuildMembersLimited));
        Assert.False(flags.HasFlag(ApplicationFlags.GatewayGuildMembers));
        var request = Assert.Single(discord.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://discord.com/api/v10/applications/@me", request.RequestUri!.ToString());
        Assert.Equal("Bot the-token", request.Headers.Authorization!.ToString());
        Assert.StartsWith("DiscordBot (https://github.com/JJWren/CalCrony, ", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task An_error_answer_names_the_status_alone()
    {
        var discord = StubDiscord.Status(HttpStatusCode.Unauthorized, """{"message": "401: Unauthorized for the-token"}""");

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => discord.Reader().ReadAsync("the-token"));

        Assert.Equal("Discord answered 401 Unauthorized.", refused.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Theory]
    [InlineData("""{"id": "1"}""")]
    [InlineData("""{"flags": "many"}""")]
    [InlineData("""[]""")]
    public async Task An_answer_without_flags_is_refused(string body)
    {
        var discord = StubDiscord.Json(body);

        await Assert.ThrowsAsync<JsonException>(() => discord.Reader().ReadAsync("the-token"));
    }

    [Fact]
    public async Task No_answer_within_the_time_limit_is_a_timeout()
    {
        var discord = StubDiscord.Silent();

        var timedOut = await Assert.ThrowsAsync<TimeoutException>(
            () => discord.Reader(TimeSpan.FromMilliseconds(100)).ReadAsync("the-token"));

        Assert.Equal("Discord did not answer within 0.1 seconds.", timedOut.Message);
        Assert.Equal(TimeSpan.FromSeconds(15), ApplicationFlagsReader.DefaultTimeout);
    }

    [Fact]
    public async Task A_cancelled_read_is_a_cancellation_not_a_timeout()
    {
        var discord = StubDiscord.Silent();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var cancelled = await Record.ExceptionAsync(() => discord.Reader().ReadAsync("the-token", cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
    }
}
