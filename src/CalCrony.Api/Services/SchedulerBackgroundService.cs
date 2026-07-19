using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Drives DeliveryScheduler.SweepAsync on a fixed timer (default 15s); each tick is isolated so one failure never kills the loop.</summary>
/// <param name="services">The request service provider.</param>
/// <param name="clock">The time source.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
public sealed class SchedulerBackgroundService(
    IServiceProvider services,
    IClock clock,
    IConfiguration configuration,
    ILogger<SchedulerBackgroundService> logger) : BackgroundService
{
    /// <summary>Ticks the sweep until shutdown, logging and continuing on per-tick failures.</summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Scheduler:SweepSeconds", 15));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
                await scheduler.SweepAsync(clock.GetCurrentInstant(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler sweep failed; will retry next tick.");
            }
        }
    }
}
