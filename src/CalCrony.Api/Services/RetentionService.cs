using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>One retention purge: deletes rows whose work finished long ago so tables stay bounded
/// on a long-running deployment. Pending deliveries are NEVER purged — undelivered work staying
/// visible is the point of the outbox.</summary>
/// <param name="db">The database context.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
public sealed class RetentionService(
    CalCronyDbContext db, IConfiguration configuration, ILogger<RetentionService> logger)
{
    /// <summary>Purges done rows older than the retention window (Retention:Days, default 90):
    /// Sent/Failed deliveries, web login states, web refresh tokens, and calendar link tokens —
    /// all by creation age. Tokens live minutes-to-30-days, so anything created before the
    /// cutoff has been dead for at least two months.</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were purged across all tables.</returns>
    public async Task<int> PurgeAsync(Instant now, CancellationToken cancellationToken)
    {
        var cutoff = now.Minus(Duration.FromDays(configuration.GetValue("Retention:Days", 90)));

        var deliveries = await db.Deliveries
            .Where(d => d.Status != DeliveryStatus.Pending && d.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var loginStates = await db.WebLoginStates
            .Where(s => s.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var refreshTokens = await db.WebRefreshTokens
            .Where(t => t.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var linkTokens = await db.CalendarLinkTokens
            .Where(t => t.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var total = deliveries + loginStates + refreshTokens + linkTokens;
        if (total > 0)
        {
            logger.LogInformation(
                "Retention purge removed {Total} rows (deliveries {Deliveries}, login states {LoginStates}, refresh tokens {RefreshTokens}, link tokens {LinkTokens}).",
                total, deliveries, loginStates, refreshTokens, linkTokens);
        }

        return total;
    }
}
