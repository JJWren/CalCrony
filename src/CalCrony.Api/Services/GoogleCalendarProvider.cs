using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Services;

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

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        var url = flow.CreateAuthorizationCodeRequest(redirectUri);
        url.State = state;
        // AccessType defaults to "offline" on GoogleAuthorizationCodeRequestUrl already; not set
        // explicitly here to avoid an unnecessary cast to the concrete Google-specific type.
        return url.Build().ToString();
    }

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

    private static Instant ToExpiresAt(TokenResponse response) =>
        Instant.FromDateTimeUtc(DateTime.SpecifyKind(response.IssuedUtc, DateTimeKind.Utc))
            + Duration.FromSeconds(response.ExpiresInSeconds ?? 3600);

    private sealed record FreeBusyRequestBody(
        [property: JsonPropertyName("timeMin")] string TimeMin,
        [property: JsonPropertyName("timeMax")] string TimeMax,
        [property: JsonPropertyName("items")] IReadOnlyList<FreeBusyRequestItem> Items);

    private sealed record FreeBusyRequestItem([property: JsonPropertyName("id")] string Id);

    private sealed record FreeBusyResponseBody(
        [property: JsonPropertyName("calendars")] Dictionary<string, FreeBusyCalendar>? Calendars);

    private sealed record FreeBusyCalendar(
        [property: JsonPropertyName("busy")] List<FreeBusyInterval>? Busy,
        [property: JsonPropertyName("errors")] List<FreeBusyError>? Errors);

    private sealed record FreeBusyInterval(
        [property: JsonPropertyName("start")] string Start,
        [property: JsonPropertyName("end")] string End);

    private sealed record FreeBusyError([property: JsonPropertyName("reason")] string? Reason);
}
