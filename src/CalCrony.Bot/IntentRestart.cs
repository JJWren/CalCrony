namespace CalCrony.Bot;

/// <summary>Stops the bot, once, so that its container restarts and the Server Members intent is
/// decided again from the application's flags (both compose files restart the bot whatever its
/// exit code). After Discord refuses the intent the stop waits
/// <see cref="ServerMembersIntent.RestartDelay"/>: Discord resets a bot's token when it opens more
/// than 1,000 new connections in a day, and a restart that met the same refusal at once, again and
/// again, could pass that.</summary>
/// <param name="lifetime">The host lifetime.</param>
/// <param name="logger">The host logger.</param>
public sealed class IntentRestart(IHostApplicationLifetime lifetime, ILogger<IntentRestart> logger)
{
    private int scheduled;

    /// <summary>What a stop does to the process before the host stops: a non-zero exit code.
    /// Replaceable for tests, which must not change their own process's exit code.</summary>
    public Action MarkFailed { get; init; } = () => Environment.ExitCode = 1;

    /// <summary>Logs why and stops the bot after the delay — the first time only.</summary>
    /// <param name="why">The log line, saying what happens next.</param>
    /// <param name="delay">How long to wait before stopping.</param>
    /// <param name="level">How the line is logged: critical for a loss or a refusal, lower for the
    /// intent coming back, which is good news.</param>
    /// <returns>True when this call scheduled the stop; false when one was already scheduled.</returns>
    public bool Schedule(string why, TimeSpan delay, LogLevel level = LogLevel.Critical)
    {
        if (Interlocked.Exchange(ref scheduled, 1) == 1)
        {
            return false;
        }

        logger.Log(level, "{Why}", why);
        _ = Task.Delay(delay, lifetime.ApplicationStopping).ContinueWith(
            _ =>
            {
                MarkFailed();
                lifetime.StopApplication();
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        return true;
    }
}
