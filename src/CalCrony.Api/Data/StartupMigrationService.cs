using CalCrony.Api.Auth;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Data;

/// <summary>
/// Applies pending migrations on startup and seeds the bootstrap API key from
/// configuration (<c>Auth:BootstrapApiKey</c>) when no keys exist yet.
/// Disabled entirely when <c>Database:AutoMigrate</c> is false (e.g. in tests).
/// </summary>
public sealed class StartupMigrationService(
    IServiceProvider services,
    IConfiguration configuration,
    IClock clock,
    ILogger<StartupMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Database:AutoMigrate", true))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.Database.MigrateAsync(cancellationToken);

        var bootstrapKey = configuration["Auth:BootstrapApiKey"];
        if (!string.IsNullOrWhiteSpace(bootstrapKey) && !await db.ApiKeys.AnyAsync(cancellationToken))
        {
            db.ApiKeys.Add(new ApiKey
            {
                Id = Guid.NewGuid(),
                Label = "bootstrap",
                KeyHash = ApiKeyValidator.Hash(bootstrapKey),
                CreatedAt = clock.GetCurrentInstant(),
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded bootstrap API key from configuration.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
