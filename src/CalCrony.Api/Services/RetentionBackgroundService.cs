using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Drives RetentionService.PurgeAsync on a slow timer (default 24h). Unlike the
/// scheduler loop, the first purge runs immediately at startup — at a daily cadence, waiting a
/// full interval would leave a freshly restarted long-idle deployment unpurged for a day.</summary>
/// <param name="services">The request service provider.</param>
/// <param name="clock">The time source.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
public sealed class RetentionBackgroundService(
    IServiceProvider services,
    IClock clock,
    IConfiguration configuration,
    ILogger<RetentionBackgroundService> logger) : BackgroundService
{
    /// <summary>Purges once at start, then on each timer tick until shutdown; per-tick failures log and continue.</summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = configuration.GetValue("Retention:SweepHours", 24);
        if (hours < 1)
        {
            // PeriodicTimer throws on non-positive intervals, which would take the host down.
            logger.LogWarning("Retention:SweepHours was {Hours}; clamping to 1 hour.", hours);
            hours = 1;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));

        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
                await retention.PurgeAsync(clock.GetCurrentInstant(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention purge failed; will retry next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
