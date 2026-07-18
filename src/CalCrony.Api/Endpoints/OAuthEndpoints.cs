using System.Text.Encodings.Web;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>
/// The API's only non-JSON, anonymous surface: the browser-facing legs of the Google OAuth dance.
/// Both routes are reachable without X-Api-Key (see ApiKeyMiddleware's "/oauth" prefix) since
/// Google and the user's own browser hit them directly.
/// </summary>
public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/oauth/google/start", Start).AllowAnonymous();
        app.MapGet("/oauth/google/callback", Callback).AllowAnonymous();
    }

    private static async Task<IResult> Start(
        string token,
        CalCronyDbContext db,
        ICalendarProvider provider,
        IConfiguration configuration,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var linkToken = await db.CalendarLinkTokens.FirstOrDefaultAsync(t => t.Token == token, cancellationToken);
        if (!IsUsable(linkToken, clock.GetCurrentInstant()))
        {
            return FailurePage("This calendar-connect link is invalid or has expired. Run /calendar connect again to get a new one.");
        }

        var authorizationUrl = provider.BuildAuthorizationUrl(RedirectUri(configuration), state: token);
        return Results.Redirect(authorizationUrl);
    }

    private static async Task<IResult> Callback(
        CalCronyDbContext db,
        ICalendarProvider provider,
        CalendarTokenProtector protector,
        IConfiguration configuration,
        IClock clock,
        ILogger<Program> logger,
        CancellationToken cancellationToken,
        string? code = null,
        string? state = null,
        string? error = null)
    {
        if (error is not null)
        {
            return FailurePage($"Google reported: {error}. Run /calendar connect again if you'd like to try once more.");
        }

        if (code is null || state is null)
        {
            return FailurePage("Missing parameters from Google's response. Run /calendar connect again.");
        }

        var linkToken = await db.CalendarLinkTokens.FirstOrDefaultAsync(t => t.Token == state, cancellationToken);
        var now = clock.GetCurrentInstant();
        if (!IsUsable(linkToken, now))
        {
            return FailurePage("This calendar-connect link is invalid, expired, or already used. Run /calendar connect again.");
        }

        CalendarTokenResult tokens;
        try
        {
            tokens = await provider.ExchangeCodeAsync(code, RedirectUri(configuration), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google OAuth code exchange failed for user {UserId}.", linkToken!.UserId);
            return FailurePage("Couldn't finish connecting to Google. Run /calendar connect again to retry — this link is still valid.");
        }

        var existing = await db.CalendarConnections
            .FirstOrDefaultAsync(c => c.UserId == linkToken!.UserId && c.Provider == linkToken.Provider, cancellationToken);
        if (existing is null)
        {
            db.CalendarConnections.Add(new CalendarConnection
            {
                Id = Guid.NewGuid(),
                UserId = linkToken!.UserId,
                Provider = linkToken.Provider,
                EncryptedAccessToken = protector.Protect(tokens.AccessToken),
                EncryptedRefreshToken = protector.Protect(tokens.RefreshToken),
                AccessTokenExpiresAt = tokens.ExpiresAt,
                ConnectedAt = now,
            });
        }
        else
        {
            existing.EncryptedAccessToken = protector.Protect(tokens.AccessToken);
            existing.EncryptedRefreshToken = protector.Protect(tokens.RefreshToken);
            existing.AccessTokenExpiresAt = tokens.ExpiresAt;
            existing.ConnectedAt = now;
        }

        linkToken!.ConsumedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return SuccessPage();
    }

    private static bool IsUsable(CalendarLinkToken? token, Instant now) =>
        token is not null && token.ConsumedAt is null && token.ExpiresAt > now;

    private static string RedirectUri(IConfiguration configuration) =>
        $"{(configuration["Api:PublicBaseUrl"] ?? "").TrimEnd('/')}/oauth/google/callback";

    private static IResult SuccessPage() => RenderPage(
        "Calendar connected",
        "✅ Your Google Calendar is connected. You can close this tab and go back to Discord.",
        statusCode: 200);

    private static IResult FailurePage(string message) => RenderPage(
        "Calendar connection failed",
        $"❌ {message}",
        statusCode: 400);

    private static IResult RenderPage(string title, string message, int statusCode)
    {
        // Both values can carry attacker-influenced query-string content (e.g. Google's `error`
        // param) on an anonymous, browser-facing route — always HTML-encode to prevent reflected XSS.
        var safeTitle = HtmlEncoder.Default.Encode(title);
        var safeMessage = HtmlEncoder.Default.Encode(message);
        var html = $"""
            <!doctype html>
            <html>
            <head><meta charset="utf-8"><title>{safeTitle} — CalCrony</title></head>
            <body style="font-family: sans-serif; max-width: 32rem; margin: 4rem auto; text-align: center;">
                <h1>{safeTitle}</h1>
                <p>{safeMessage}</p>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }
}
