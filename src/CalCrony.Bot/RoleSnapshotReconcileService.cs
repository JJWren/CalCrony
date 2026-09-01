using Discord;
using Discord.WebSocket;

namespace CalCrony.Bot;

/// <summary>Renews the role snapshots on a timer while the bot runs. The API treats a guild's sync
/// marker as authoritative for a bounded lease (RoleRestriction.SnapshotMaxAge, 30 minutes); this
/// reconcile is what keeps a live bot's marker inside it, so a marker that has aged out means the
/// bot has been gone long enough that member pushes may have been missed — and the web fails
/// closed rather than trusting the stale rows. After the first sync every member is cached, so a
/// reconcile costs one watched-roles lookup plus one PUT per restricted guild.</summary>
/// <param name="client">The Discord socket client.</param>
/// <param name="roleSnapshots">The role-snapshot pusher.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
public sealed class RoleSnapshotReconcileService(
    DiscordSocketClient client,
    RoleSnapshotService roleSnapshots,
    IConfiguration configuration,
    ILogger<RoleSnapshotReconcileService> logger) : BackgroundService
{
    /// <summary>Reconciles every watched guild each tick (Roles:ReconcileMinutes, default 10 —
    /// a third of the API's lease, so one missed tick never expires a marker).</summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, configuration.GetValue("Roles:ReconcileMinutes", 10));
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (client.ConnectionState != ConnectionState.Connected)
                {
                    // Nothing to renew from a disconnected client — and letting the marker age
                    // out while disconnected is the point.
                    continue;
                }

                try
                {
                    await roleSnapshots.ReconcileAllAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Periodic role snapshot reconcile failed; the next tick retries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }
}
