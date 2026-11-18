namespace CalCrony.Bot;

/// <summary>In auto, with the intent asked for, reads the application's flags again every
/// <see cref="ServerMembersIntent.RecheckInterval"/>. Whether Discord closes a running connection
/// when an application loses the Server Members intent is not known; if it does not, the bot would
/// go on believing it has the intent, with member events no longer arriving and role snapshots
/// renewed from a member cache that no longer changes. Finding the flags gone, the bot stops at
/// once so that it restarts without the intent. A read that fails changes nothing.</summary>
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
    /// <summary>Reads the flags on a timer until they show the intent gone or the host stops.</summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = configuration["Discord:BotToken"];
        if (!intent.RestartsOnRefusal || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        using var timer = new PeriodicTimer(ServerMembersIntent.RecheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var flags = await reader.ReadAsync(token, stoppingToken);
                    if (intent.LostOnRecheck(flags))
                    {
                        restart.Schedule(ServerMembersIntent.LostMessage, TimeSpan.Zero);
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
