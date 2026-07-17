using NodaTime;

namespace CalCrony.Api.Services;

public sealed class SchedulerBackgroundService(
    IServiceProvider services,
    IClock clock,
    IConfiguration configuration,
    ILogger<SchedulerBackgroundService> logger) : BackgroundService
{
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
