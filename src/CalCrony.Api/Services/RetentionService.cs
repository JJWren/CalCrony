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
    /// cutoff has been dead for at least two months. Server action log entries purge by their
    /// own window (Retention:ActionLogDays, default 90) so a self-hoster can keep a longer or
    /// shorter audit trail without touching the outbox history. Role snapshots are not
    /// age-bounded but purpose-bounded: a guild with no live signup restriction left has no
    /// reason to hold who-holds-which-role rows, so they go the same sweep (ADR 0004).</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were purged across all tables.</returns>
    public async Task<int> PurgeAsync(Instant now, CancellationToken cancellationToken)
    {
        var cutoff = now.Minus(Duration.FromDays(WindowDays("Retention:Days")));
        var actionLogCutoff = now.Minus(Duration.FromDays(WindowDays("Retention:ActionLogDays")));

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
        var actionLog = await db.ActionLogEntries
            .Where(a => a.CreatedAt < actionLogCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var liveRestrictionGuilds = (await RoleWatchList.WatchedByGuildAsync(db, cancellationToken)).Keys.ToList();
        var staleSnapshotGuilds = await db.Guilds
            .Where(g => g.RolesSyncedAt != null && !liveRestrictionGuilds.Contains(g.Id))
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);
        var roleSnapshots = await Endpoints.RoleSnapshotEndpoints.DropSnapshotsAsync(
            db, staleSnapshotGuilds, cancellationToken);

        var total = deliveries + loginStates + refreshTokens + linkTokens + actionLog + roleSnapshots;
        if (total > 0)
        {
            logger.LogInformation(
                "Retention purge removed {Total} rows (deliveries {Deliveries}, login states {LoginStates}, refresh tokens {RefreshTokens}, link tokens {LinkTokens}, action log {ActionLog}, role snapshots {RoleSnapshots}).",
                total, deliveries, loginStates, refreshTokens, linkTokens, actionLog, roleSnapshots);
        }

        return total;
    }

    /// <summary>Reads a retention window in days (default 90), clamped to at least one day.</summary>
    /// <param name="key">The configuration key.</param>
    /// <returns>The window in whole days.</returns>
    private int WindowDays(string key)
    {
        var days = configuration.GetValue(key, 90);
        if (days < 1)
        {
            // A zero/negative window would purge everything that isn't from the future —
            // clamp instead of trusting a typo with the whole history.
            logger.LogWarning("{Key} was {Days}; clamping to 1 day.", key, days);
            days = 1;
        }

        return days;
    }
}
