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

/// <summary>Tokens returned by a successful code exchange.</summary>
public sealed record CalendarTokenResult(string AccessToken, string RefreshToken, Instant ExpiresAt);

/// <summary>How a token refresh ended: usable tokens, the user must relink, or a transient error.</summary>
public enum CalendarTokenRefreshOutcome
{
    Success,
    ReconnectRequired,
    Error,
}

/// <summary>Refresh outcome with new tokens on success or a reason otherwise.</summary>
public sealed record CalendarTokenRefreshResult(
    CalendarTokenRefreshOutcome Outcome, string? AccessToken, Instant ExpiresAt, string? ErrorMessage)
{
    /// <summary>Successful refresh carrying the new access token.</summary>
    public static CalendarTokenRefreshResult Success(string accessToken, Instant expiresAt) =>
        new(CalendarTokenRefreshOutcome.Success, accessToken, expiresAt, null);

    /// <summary>The stored grant is dead — the user must relink their calendar.</summary>
    public static CalendarTokenRefreshResult ReconnectRequired(string message) =>
        new(CalendarTokenRefreshOutcome.ReconnectRequired, null, default, message);

    /// <summary>Transient refresh failure; the stored connection stays.</summary>
    public static CalendarTokenRefreshResult Error(string message) =>
        new(CalendarTokenRefreshOutcome.Error, null, default, message);
}

/// <summary>How a free/busy query ended.</summary>
public enum CalendarFreeBusyOutcome
{
    Success,
    Error,
}

/// <summary>Free/busy outcome: busy intervals on success, a reason otherwise.</summary>
public sealed record CalendarFreeBusyResult(
    CalendarFreeBusyOutcome Outcome, IReadOnlyList<(Instant Start, Instant End)> BusyBlocks, string? ErrorMessage)
{
    /// <summary>Successful query with the busy intervals.</summary>
    public static CalendarFreeBusyResult Success(IReadOnlyList<(Instant Start, Instant End)> busyBlocks) =>
        new(CalendarFreeBusyOutcome.Success, busyBlocks, null);

    /// <summary>Failed query with the provider's reason.</summary>
    public static CalendarFreeBusyResult Error(string message) =>
        new(CalendarFreeBusyOutcome.Error, [], message);
}
