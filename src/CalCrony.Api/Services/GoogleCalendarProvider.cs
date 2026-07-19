using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Services;

/// <summary>Google implementation: OAuth via Google.Apis.Auth, free/busy via the REST endpoint (least-privilege scope).</summary>
public sealed class GoogleCalendarProvider : ICalendarProvider
{
    /// <summary>Least-privilege: only free/busy blocks, never event titles/details.</summary>
    public const string Scope = "https://www.googleapis.com/auth/calendar.freebusy";

    private const string FreeBusyUrl = "https://www.googleapis.com/calendar/v3/freeBusy";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";

    private static readonly InstantPattern Rfc3339 = InstantPattern.ExtendedIso;

    private readonly HttpClient http;
    private readonly ILogger<GoogleCalendarProvider> logger;
    private readonly GoogleAuthorizationCodeFlow flow;

    /// <summary>Reads client credentials from configuration; missing values fail fast at first use.</summary>
    public GoogleCalendarProvider(HttpClient http, IConfiguration configuration, ILogger<GoogleCalendarProvider> logger)
    {
        this.http = http;
        this.logger = logger;
        flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = configuration["Calendar:Google:ClientId"],
                ClientSecret = configuration["Calendar:Google:ClientSecret"],
            },
            Scopes = [Scope],
            // Force a refresh_token on every consent so re-linking always yields offline access.
            // DataStore is intentionally left null: CalCrony owns persistence via CalendarConnection
            // (EF Core), not Google's own on-disk credential cache.
            Prompt = "consent",
        });
    }

    /// <summary>Consent-page URL requesting offline access so a refresh token is issued.</summary>
    /// <param name="redirectUri">The OAuth redirect URI registered with the provider.</param>
    /// <param name="state">The CSRF state value.</param>
    /// <returns>The consent-page URL.</returns>
    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        var url = flow.CreateAuthorizationCodeRequest(redirectUri);
        url.State = state;
        // AccessType defaults to "offline" on GoogleAuthorizationCodeRequestUrl already; not set
        // explicitly here to avoid an unnecessary cast to the concrete Google-specific type.
        return url.Build().ToString();
    }

    /// <summary>Exchanges the authorization code for access + refresh tokens.</summary>
    /// <param name="code">The authorization code from the provider callback.</param>
    /// <param name="redirectUri">The OAuth redirect URI registered with the provider.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The issued token pair.</returns>
    /// <exception cref="InvalidOperationException">When Google issues no refresh token (consent was not re-prompted).</exception>
    public async Task<CalendarTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        var response = await flow.ExchangeCodeForTokenAsync(
            userId: "unused", code, redirectUri, cancellationToken);

        if (string.IsNullOrEmpty(response.RefreshToken))
        {
            throw new InvalidOperationException(
                "Google didn't grant offline access (no refresh_token) — this should not happen with prompt=consent.");
        }

        return new CalendarTokenResult(response.AccessToken, response.RefreshToken, ToExpiresAt(response));
    }

    /// <summary>Refreshes the access token; invalid_grant maps to ReconnectRequired (expected in Testing mode every 7 days).</summary>
    /// <param name="refreshToken">The provider refresh token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The refresh outcome.</returns>
    public async Task<CalendarTokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var response = await flow.RefreshTokenAsync(userId: "unused", refreshToken, cancellationToken);
            return CalendarTokenRefreshResult.Success(response.AccessToken, ToExpiresAt(response));
        }
        catch (TokenResponseException ex)
        {
            // Google's token endpoint rejected the refresh token outright (commonly invalid_grant —
            // revoked by the user, or the 7-day expiry Testing-mode OAuth apps are subject to).
            // Any such rejection means the same fix: the user has to reconnect.
            logger.LogInformation(ex, "Google refresh token rejected; user needs to reconnect.");
            return CalendarTokenRefreshResult.ReconnectRequired(ex.Error.ErrorDescription ?? ex.Error.Error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transient failure refreshing Google access token.");
            return CalendarTokenRefreshResult.Error(ex.Message);
        }
    }

    /// <summary>Queries the primary calendar's busy intervals over the window.</summary>
    /// <param name="accessToken">The provider access token.</param>
    /// <param name="start">Window start (UTC).</param>
    /// <param name="end">Window end (UTC).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The free/busy outcome.</returns>
    public async Task<CalendarFreeBusyResult> GetFreeBusyAsync(
        string accessToken, Instant start, Instant end, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, FreeBusyUrl)
            {
                Content = JsonContent.Create(new FreeBusyRequestBody(
                    Rfc3339.Format(start), Rfc3339.Format(end), [new FreeBusyRequestItem("primary")])),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Google freeBusy call failed ({Status}): {Body}", response.StatusCode, body);
                return CalendarFreeBusyResult.Error($"Google returned {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<FreeBusyResponseBody>(cancellationToken);
            var calendar = payload?.Calendars?.GetValueOrDefault("primary");
            if (calendar is null)
            {
                return CalendarFreeBusyResult.Error("Google returned no calendar data for 'primary'.");
            }

            if (calendar.Errors is { Count: > 0 })
            {
                var reason = calendar.Errors[0].Reason ?? "unknown";
                return CalendarFreeBusyResult.Error($"Google reported a calendar error: {reason}.");
            }

            var busy = (calendar.Busy ?? [])
                .Select(b => (Start: Rfc3339.Parse(b.Start).Value, End: Rfc3339.Parse(b.End).Value))
                .ToList();
            return CalendarFreeBusyResult.Success(busy);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transient failure calling Google freeBusy.");
            return CalendarFreeBusyResult.Error(ex.Message);
        }
    }

    /// <summary>Best-effort token revocation on disconnect.</summary>
    /// <param name="token">The token value.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]);
            using var response = await http.PostAsync(RevokeUrl, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Google token revoke returned {Status} (non-fatal).", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Google token revoke failed (non-fatal); local connection is still removed.");
        }
    }

    /// <summary>Absolute expiry instant from a token response's relative lifetime.</summary>
    /// <param name="response">The provider token response.</param>
    /// <returns>The absolute expiry instant.</returns>
    private static Instant ToExpiresAt(TokenResponse response) =>
        Instant.FromDateTimeUtc(DateTime.SpecifyKind(response.IssuedUtc, DateTimeKind.Utc))
            + Duration.FromSeconds(response.ExpiresInSeconds ?? 3600);

    /// <summary>freeBusy request body shape.</summary>
    /// <param name="TimeMin">Window start (RFC3339).</param>
    /// <param name="TimeMax">Window end (RFC3339).</param>
    /// <param name="Items">The requested calendars.</param>
    private sealed record FreeBusyRequestBody(
        [property: JsonPropertyName("timeMin")] string TimeMin,
        [property: JsonPropertyName("timeMax")] string TimeMax,
        [property: JsonPropertyName("items")] IReadOnlyList<FreeBusyRequestItem> Items);

    /// <summary>freeBusy request calendar item.</summary>
    /// <param name="Id">The calendar id.</param>
    private sealed record FreeBusyRequestItem([property: JsonPropertyName("id")] string Id);

    /// <summary>freeBusy response body shape.</summary>
    /// <param name="Calendars">Per-calendar results.</param>
    private sealed record FreeBusyResponseBody(
        [property: JsonPropertyName("calendars")] Dictionary<string, FreeBusyCalendar>? Calendars);

    /// <summary>Per-calendar busy list in a freeBusy response.</summary>
    /// <param name="Busy">The busy intervals.</param>
    /// <param name="Errors">Per-calendar errors.</param>
    private sealed record FreeBusyCalendar(
        [property: JsonPropertyName("busy")] List<FreeBusyInterval>? Busy,
        [property: JsonPropertyName("errors")] List<FreeBusyError>? Errors);

    /// <summary>One busy interval in a freeBusy response.</summary>
    /// <param name="Start">Window start (UTC).</param>
    /// <param name="End">Window end (UTC).</param>
    private sealed record FreeBusyInterval(
        [property: JsonPropertyName("start")] string Start,
        [property: JsonPropertyName("end")] string End);

    /// <summary>Per-calendar error in a freeBusy response.</summary>
    /// <param name="Reason">The provider error reason.</param>
    private sealed record FreeBusyError([property: JsonPropertyName("reason")] string? Reason);
}
