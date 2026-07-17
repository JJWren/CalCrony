using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>
/// Provider-agnostic external-calendar OAuth + free/busy abstraction. Raw token material never
/// crosses into CalCrony.Contracts — these types are CalCrony.Api-internal only.
/// </summary>
public interface ICalendarProvider
{
    string BuildAuthorizationUrl(string redirectUri, string state);

    /// <summary>Exchanges an OAuth code for tokens. Throws on any failure — callers have a single
    /// failure path (render the OAuth failure page).</summary>
    Task<CalendarTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Never throws for provider-rejected refresh tokens — returns a typed outcome so a
    /// multi-user availability fan-out can tell "this one user needs to reconnect" from "transient
    /// failure" without per-item try/catch.</summary>
    Task<CalendarTokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task<CalendarFreeBusyResult> GetFreeBusyAsync(string accessToken, Instant start, Instant end, CancellationToken cancellationToken);

    /// <summary>Best-effort; must never throw. Used by disconnect, which always succeeds locally
    /// even if the provider-side revoke fails.</summary>
    Task RevokeAsync(string token, CancellationToken cancellationToken);
}

public sealed record CalendarTokenResult(string AccessToken, string RefreshToken, Instant ExpiresAt);

public enum CalendarTokenRefreshOutcome
{
    Success,
    ReconnectRequired,
    Error,
}

public sealed record CalendarTokenRefreshResult(
    CalendarTokenRefreshOutcome Outcome, string? AccessToken, Instant ExpiresAt, string? ErrorMessage)
{
    public static CalendarTokenRefreshResult Success(string accessToken, Instant expiresAt) =>
        new(CalendarTokenRefreshOutcome.Success, accessToken, expiresAt, null);

    public static CalendarTokenRefreshResult ReconnectRequired(string message) =>
        new(CalendarTokenRefreshOutcome.ReconnectRequired, null, default, message);

    public static CalendarTokenRefreshResult Error(string message) =>
        new(CalendarTokenRefreshOutcome.Error, null, default, message);
}

public enum CalendarFreeBusyOutcome
{
    Success,
    Error,
}

public sealed record CalendarFreeBusyResult(
    CalendarFreeBusyOutcome Outcome, IReadOnlyList<(Instant Start, Instant End)> BusyBlocks, string? ErrorMessage)
{
    public static CalendarFreeBusyResult Success(IReadOnlyList<(Instant Start, Instant End)> busyBlocks) =>
        new(CalendarFreeBusyOutcome.Success, busyBlocks, null);

    public static CalendarFreeBusyResult Error(string message) =>
        new(CalendarFreeBusyOutcome.Error, [], message);
}
