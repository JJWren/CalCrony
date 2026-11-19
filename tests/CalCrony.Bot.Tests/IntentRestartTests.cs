using CalCrony.Bot;
using Microsoft.Extensions.Hosting;
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
        var restart = new IntentRestart(lifetime, NullLogger<IntentRestart>.Instance) { MarkFailed = () => failed++ };

        Assert.True(restart.Schedule("refused", TimeSpan.FromSeconds(1)));
        Assert.False(restart.Schedule("refused again", TimeSpan.Zero));
        Assert.Equal(0, lifetime.Stops); // not before the delay

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.Equal(1, lifetime.Stops);
        Assert.Equal(1, failed);
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

    /// <summary>A host lifetime that records stops.</summary>
    internal sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Stopping { get; } = new();

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Stops { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => Stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Stops++;
            Stopped.TrySetResult();
        }
    }
}
