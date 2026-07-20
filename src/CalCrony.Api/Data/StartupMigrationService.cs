using CalCrony.Api.Auth;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Data;

/// <summary>
/// Applies pending migrations on startup and seeds the bootstrap API key from
/// configuration (<c>Auth:BootstrapApiKey</c>) when no keys exist yet. Fails fast with clear
/// messages when the database is unreachable or the API would boot keyless (the bot could
/// never authenticate). Disabled entirely when <c>Database:AutoMigrate</c> is false
/// (e.g. in tests, or for operators managing schema and keys manually).
/// </summary>
/// <param name="services">The request service provider.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="clock">The time source.</param>
/// <param name="logger">The host logger.</param>
public sealed class StartupMigrationService(
    IServiceProvider services,
    IConfiguration configuration,
    IClock clock,
    ILogger<StartupMigrationService> logger) : IHostedService
{
    /// <summary>Applies pending EF migrations at boot when Database:AutoMigrate is on.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Database:AutoMigrate", true))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(
                "Cannot reach PostgreSQL using connection string 'CalCrony' — "
                + "check ConnectionStrings__CalCrony and that the database is up.", ex);
        }

        var bootstrapKey = configuration["Auth:BootstrapApiKey"];
        var anyKeys = await db.ApiKeys.AnyAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(bootstrapKey) && !anyKeys)
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
        else if (string.IsNullOrWhiteSpace(bootstrapKey) && !anyKeys)
        {
            // A keyless API means the bot can never authenticate — every call 401s with no
            // startup signal. Almost certainly a misconfiguration; operators who truly want a
            // keyless schema can disable Database:AutoMigrate and manage keys manually.
            throw new InvalidOperationException(
                "No API keys exist and Auth:BootstrapApiKey is empty — the bot can never authenticate. "
                + "Set Auth__BootstrapApiKey before first boot.");
        }
    }

    /// <summary>Nothing to stop; migration runs once at startup.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
