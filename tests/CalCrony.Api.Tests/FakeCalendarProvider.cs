using CalCrony.Api.Services;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>In-memory <see cref="ICalendarProvider"/> double — no real network calls. Registered as
/// a singleton by <see cref="CalendarApiFixture"/>, so its mutable state is shared across all
/// requests within one fixture instance; tests should use distinct tokens/users to avoid collisions.</summary>
public sealed class FakeCalendarProvider : ICalendarProvider
{
    public const string InvalidCode = "invalid-code";

    public Dictionary<string, List<(Instant Start, Instant End)>> BusyBlocksByAccessToken { get; } = [];

    public Dictionary<string, CalendarTokenRefreshResult> RefreshOverrides { get; } = [];

    public int RefreshCallCount { get; private set; }

    public List<string> RevokedTokens { get; } = [];

    public string BuildAuthorizationUrl(string redirectUri, string state) =>
        $"https://accounts.google.com/o/oauth2/v2/auth?client_id=fake&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&response_type=code&scope={Uri.EscapeDataString(GoogleCalendarProvider.Scope)}&access_type=offline&prompt=consent&state={state}";

    public Task<CalendarTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        if (code == InvalidCode)
        {
            throw new InvalidOperationException("Fake: Google rejected the code.");
        }

        return Task.FromResult(new CalendarTokenResult(
            AccessToken: $"access-{code}",
            RefreshToken: $"refresh-{code}",
            ExpiresAt: SystemClock.Instance.GetCurrentInstant() + Duration.FromHours(1)));
    }

    public Task<CalendarTokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        RefreshCallCount++;

        if (RefreshOverrides.TryGetValue(refreshToken, out var overrideResult))
        {
            return Task.FromResult(overrideResult);
        }

        return Task.FromResult(CalendarTokenRefreshResult.Success(
            $"refreshed-{refreshToken}", SystemClock.Instance.GetCurrentInstant() + Duration.FromHours(1)));
    }

    public Task<CalendarFreeBusyResult> GetFreeBusyAsync(string accessToken, Instant start, Instant end, CancellationToken cancellationToken)
    {
        var blocks = BusyBlocksByAccessToken.GetValueOrDefault(accessToken, []);
        return Task.FromResult(CalendarFreeBusyResult.Success(blocks));
    }

    public Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        RevokedTokens.Add(token);
        return Task.CompletedTask;
    }
}
