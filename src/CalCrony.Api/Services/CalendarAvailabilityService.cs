using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>
/// Orchestrates decrypt -> refresh-if-needed -> live free/busy query across a batch of users.
/// Provider HTTP calls run concurrently per user; all DbContext access happens in two sequential
/// passes (before and after the concurrent section) since EF Core's DbContext is not safe for
/// concurrent use from multiple tasks.
/// </summary>
/// <param name="db">The database context.</param>
/// <param name="provider">The calendar provider.</param>
/// <param name="protector">The scoped data protector.</param>
/// <param name="clock">The time source.</param>
/// <param name="logger">The host logger.</param>
public sealed class CalendarAvailabilityService(
    CalCronyDbContext db,
    ICalendarProvider provider,
    CalendarTokenProtector protector,
    IClock clock,
    ILogger<CalendarAvailabilityService> logger)
{
    /// <summary>Refresh slightly before the recorded expiry to avoid a race against the call that follows.</summary>
    private static readonly Duration RefreshSkew = Duration.FromMinutes(2);

    /// <summary>Checks free/busy for each user concurrently, refreshing expired provider tokens along the way.</summary>
    /// <param name="userIds">The Discord user ids to check.</param>
    /// <param name="start">Window start (UTC).</param>
    /// <param name="end">Window end (UTC).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One availability row per requested user.</returns>
    public async Task<IReadOnlyList<UserAvailabilityDto>> CheckAsync(
        IReadOnlyList<long> userIds, Instant start, Instant end, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var connections = await db.CalendarConnections
            .Where(c => userIds.Contains(c.UserId))
            .ToDictionaryAsync(c => c.UserId, cancellationToken);

        var pipelineResults = await Task.WhenAll(userIds
            .Where(connections.ContainsKey)
            .Select(userId => CheckOneAsync(connections[userId], start, end, now, cancellationToken)));

        foreach (var result in pipelineResults)
        {
            if (result.RefreshedAccessToken is null)
            {
                continue;
            }

            var connection = connections[result.UserId];
            connection.EncryptedAccessToken = protector.Protect(result.RefreshedAccessToken);
            connection.AccessTokenExpiresAt = result.RefreshedExpiresAt!.Value;
            connection.LastRefreshedAt = now;
        }

        if (Array.Exists(pipelineResults, r => r.RefreshedAccessToken is not null))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var byUserId = pipelineResults.ToDictionary(r => r.UserId);
        return userIds
            .Select(userId => byUserId.TryGetValue(userId, out var result)
                ? new UserAvailabilityDto(userId, result.Status, result.BusyBlocks)
                : new UserAvailabilityDto(userId, CalendarAvailabilityStatus.NotConnected, []))
            .ToList();
    }

    /// <summary>One user's pipeline: load connection, refresh if needed, query free/busy, classify the outcome.</summary>
    /// <param name="connection">The stored calendar connection.</param>
    /// <param name="start">Window start (UTC).</param>
    /// <param name="end">Window end (UTC).</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The pipeline outcome, including tokens to persist.</returns>
    private async Task<PipelineResult> CheckOneAsync(
        CalendarConnection connection, Instant start, Instant end, Instant now, CancellationToken cancellationToken)
    {
        var accessToken = protector.Unprotect(connection.EncryptedAccessToken);
        string? refreshedAccessToken = null;
        Instant? refreshedExpiresAt = null;

        if (connection.AccessTokenExpiresAt <= now + RefreshSkew)
        {
            var refreshToken = protector.Unprotect(connection.EncryptedRefreshToken);
            var refresh = await provider.RefreshAsync(refreshToken, cancellationToken);

            if (refresh.Outcome == CalendarTokenRefreshOutcome.ReconnectRequired)
            {
                return new PipelineResult(connection.UserId, CalendarAvailabilityStatus.ReconnectRequired, [], null, null);
            }

            if (refresh.Outcome == CalendarTokenRefreshOutcome.Error)
            {
                logger.LogWarning("Availability check: refresh failed for user {UserId}: {Error}", connection.UserId, refresh.ErrorMessage);
                return new PipelineResult(connection.UserId, CalendarAvailabilityStatus.Error, [], null, null);
            }

            accessToken = refresh.AccessToken!;
            refreshedAccessToken = refresh.AccessToken;
            refreshedExpiresAt = refresh.ExpiresAt;
        }

        var freeBusy = await provider.GetFreeBusyAsync(accessToken, start, end, cancellationToken);
        if (freeBusy.Outcome == CalendarFreeBusyOutcome.Error)
        {
            logger.LogWarning("Availability check: freeBusy failed for user {UserId}: {Error}", connection.UserId, freeBusy.ErrorMessage);
            return new PipelineResult(connection.UserId, CalendarAvailabilityStatus.Error, [], refreshedAccessToken, refreshedExpiresAt);
        }

        var status = freeBusy.BusyBlocks.Count > 0 ? CalendarAvailabilityStatus.Busy : CalendarAvailabilityStatus.Free;
        var busyDtos = freeBusy.BusyBlocks
            .Select(b => new BusyBlockDto(b.Start.ToDateTimeOffset(), b.End.ToDateTimeOffset()))
            .ToList();
        return new PipelineResult(connection.UserId, status, busyDtos, refreshedAccessToken, refreshedExpiresAt);
    }

    /// <summary>Intermediate per-user outcome, including any refreshed tokens to persist.</summary>
    /// <param name="UserId">The Discord user id.</param>
    /// <param name="Status">The resolved availability status.</param>
    /// <param name="BusyBlocks">The busy intervals.</param>
    /// <param name="RefreshedAccessToken">The refreshed access token to persist, when rotation happened.</param>
    /// <param name="RefreshedExpiresAt">The refreshed token expiry, when rotation happened.</param>
    private sealed record PipelineResult(
        long UserId,
        CalendarAvailabilityStatus Status,
        IReadOnlyList<BusyBlockDto> BusyBlocks,
        string? RefreshedAccessToken,
        Instant? RefreshedExpiresAt);
}
