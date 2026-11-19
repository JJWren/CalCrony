using CalCrony.Bot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalCrony.Bot.Tests;

/// <summary>The stop that lets the bot's container restart and decide the Server Members intent
/// again: once only, after its delay, with a failed exit — and not at all once the host is already
/// stopping.</summary>
public class IntentRestartTests
{
    [Fact]
    public async Task The_first_call_stops_the_bot_after_the_delay_with_a_failed_exit_and_later_calls_do_nothing()
    {
        var lifetime = new FakeLifetime();
        var failed = 0;
        var logger = new ListLogger<IntentRestart>();
        var restart = new IntentRestart(lifetime, logger) { MarkFailed = () => failed++ };

        Assert.True(restart.Schedule("refused", TimeSpan.FromSeconds(1)));
        Assert.False(restart.Schedule("refused again", TimeSpan.Zero));
        Assert.Equal(0, lifetime.Stops); // not before the delay

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.Equal(1, lifetime.Stops);
        Assert.Equal(1, failed);
        Assert.Equal([(LogLevel.Critical, "refused")], logger.Lines); // the second call logged nothing
    }

    [Fact]
    public void The_line_is_logged_at_the_level_given()
    {
        var logger = new ListLogger<IntentRestart>();
        var restart = new IntentRestart(new FakeLifetime(), logger) { MarkFailed = () => { } };

        restart.Schedule("back", TimeSpan.FromMinutes(1), LogLevel.Warning);

        Assert.Equal([(LogLevel.Warning, "back")], logger.Lines);
    }

    [Fact]
    public async Task A_host_already_stopping_is_left_to_stop_on_its_own()
    {
        var lifetime = new FakeLifetime();
        var failed = 0;
        var restart = new IntentRestart(lifetime, NullLogger<IntentRestart>.Instance) { MarkFailed = () => failed++ };

        // A delay far longer than the test: only an ordinary shutdown can end it, and that must
        // not turn into a stop of this class's own.
        restart.Schedule("refused", TimeSpan.FromMinutes(1));
        await lifetime.Stopping.CancelAsync();
        await Task.Delay(300);

        Assert.Equal(0, lifetime.Stops);
        Assert.Equal(0, failed); // an ordinary shutdown keeps its exit code
    }
}
