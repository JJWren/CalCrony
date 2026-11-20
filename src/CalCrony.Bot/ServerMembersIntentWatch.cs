namespace CalCrony.Bot;

/// <summary>In auto, reads the application's flags again every <see cref="Interval"/> and restarts
/// the bot at once when they disagree with the decision made at start. The intent gone: whether
/// Discord closes a running connection when an application loses it is not known, and if it does
/// not, member events would stop arriving while role snapshots were renewed from a member cache that
/// no longer changes. The intent back (approved after a review, or switched on again): a bot running
/// without it picks it up without being restarted by hand. A read that fails changes nothing.</summary>
/// <param name="intent">The decision made at start.</param>
/// <param name="reader">Reads the application's flags.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="restart">Stops the bot so that it restarts.</param>
/// <param name="logger">The host logger.</param>
public sealed class ServerMembersIntentWatch(
    ServerMembersIntent intent,
    ApplicationFlagsReader reader,
    IConfiguration configuration,
    IntentRestart restart,
    ILogger<ServerMembersIntentWatch> logger) : BackgroundService
{
    /// <summary>How often the flags are read again; <see cref="ServerMembersIntent.RecheckInterval"/>
    /// unless a test sets it.</summary>
    public TimeSpan Interval { get; init; } = ServerMembersIntent.RecheckInterval;

    /// <summary>Reads the flags on a timer until they disagree with the decision or the host stops.</summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = configuration["Discord:BotToken"];
        if (intent.Mode != ServerMembersIntentMode.Auto || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var flags = await reader.ReadAsync(token, stoppingToken);
                    if (intent.ChangedOnRecheck(flags))
                    {
                        restart.Schedule(intent.RecheckMessage, TimeSpan.Zero, intent.RecheckLogLevel);
                        return;
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Could not read the application's flags again; the Server Members intent stays as decided: {Error}",
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
