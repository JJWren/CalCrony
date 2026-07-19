using System.Security.Cryptography;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>
/// Discord web login. All routes are anonymous: start/callback are browser redirects, and
/// refresh/logout authenticate via the HttpOnly refresh cookie (Path-scoped to /auth).
/// </summary>
public static class AuthEndpoints
{
    private const int LoginStateExpiryMinutes = 10;
    public const string RefreshCookieName = "calcrony_refresh";

    /// <summary>Discord OAuth web login: start/callback plus refresh-cookie rotation and logout.</summary>
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").AllowAnonymous();
        group.MapGet("/discord/start", Start);
        group.MapGet("/discord/callback", Callback);
        group.MapPost("/refresh", Refresh);
        group.MapPost("/logout", Logout);
    }

    /// <summary>Begins the Discord OAuth dance: mints CSRF state and redirects to Discord consent.</summary>
    private static async Task<IResult> Start(
        CalCronyDbContext db,
        IDiscordAuthProvider discord,
        IConfiguration configuration,
        IClock clock,
        CancellationToken cancellationToken,
        string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(configuration["Auth:Discord:ClientId"]) ||
            string.IsNullOrWhiteSpace(configuration["Api:PublicBaseUrl"]) ||
            string.IsNullOrWhiteSpace(configuration["Web:Origin"]))
        {
            return Results.Json(
                new ErrorResponse("Web login isn't configured on this server yet — Auth:Discord:*, Api:PublicBaseUrl, and Web:Origin are required."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Relative-only: "/app", never "https://evil" or "//evil".
        if (returnUrl is not null && (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//")))
        {
            returnUrl = null;
        }

        var now = clock.GetCurrentInstant();
        var state = new WebLoginState
        {
            Id = Guid.NewGuid(),
            Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)),
            ReturnUrl = returnUrl,
            CreatedAt = now,
            ExpiresAt = now.Plus(Duration.FromMinutes(LoginStateExpiryMinutes)),
        };
        db.WebLoginStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Redirect(discord.BuildAuthorizationUrl(RedirectUri(configuration), state.Token));
    }

    /// <summary>Discord redirects here: validates state, exchanges the code, snapshots guild memberships, issues tokens, and bounces to the web app.</summary>
    private static async Task<IResult> Callback(
        HttpContext context,
        CalCronyDbContext db,
        IDiscordAuthProvider discord,
        WebTokenService tokens,
        IConfiguration configuration,
        IClock clock,
        ILogger<Program> logger,
        CancellationToken cancellationToken,
        string? code = null,
        string? state = null,
        string? error = null)
    {
        var webOrigin = (configuration["Web:Origin"] ?? "").TrimEnd('/');
        if (error is not null)
        {
            return Results.Redirect($"{webOrigin}/login?error=denied");
        }

        if (code is null || state is null)
        {
            return Results.Redirect($"{webOrigin}/login?error=missing");
        }

        var now = clock.GetCurrentInstant();
        var loginState = await db.WebLoginStates.FirstOrDefaultAsync(s => s.Token == state, cancellationToken);
        if (loginState is null || loginState.ConsumedAt is not null || loginState.ExpiresAt <= now)
        {
            return Results.Redirect($"{webOrigin}/login?error=expired");
        }

        DiscordUserInfo user;
        IReadOnlyList<DiscordGuildInfo> guilds;
        try
        {
            var accessToken = await discord.ExchangeCodeAsync(code, RedirectUri(configuration), cancellationToken);
            user = await discord.GetCurrentUserAsync(accessToken, cancellationToken);
            guilds = await discord.GetGuildsAsync(accessToken, cancellationToken);
            // The Discord user token is deliberately dropped here — never persisted.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord login exchange failed.");
            return Results.Redirect($"{webOrigin}/login?error=discord");
        }

        loginState.ConsumedAt = now;
        await db.UserGuildMemberships.Where(m => m.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        db.UserGuildMemberships.AddRange(guilds.Select(g => new UserGuildMembership
        {
            UserId = user.Id,
            GuildId = g.Id,
            GuildName = g.Name,
            IconHash = g.IconHash,
            CanManage = g.CanManage,
            SnapshotAt = now,
        }));

        var profile = await db.UserProfiles.FindAsync([user.Id], cancellationToken);
        if (profile is null)
        {
            profile = new UserProfile { Id = user.Id };
            db.UserProfiles.Add(profile);
        }

        profile.Username = user.GlobalName ?? user.Username;
        profile.AvatarHash = user.AvatarHash;
        await db.SaveChangesAsync(cancellationToken);

        var refresh = await tokens.IssueRefreshTokenAsync(user.Id, cancellationToken);
        AppendRefreshCookie(context, refresh);

        return Results.Redirect($"{webOrigin}{loginState.ReturnUrl ?? "/app"}");
    }

    /// <summary>Rotates the refresh cookie and issues a fresh access token; reuse of a consumed token revokes the session.</summary>
    private static async Task<IResult> Refresh(
        HttpContext context,
        CalCronyDbContext db,
        WebTokenService tokens,
        CancellationToken cancellationToken)
    {
        var raw = context.Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(raw))
        {
            return Results.Unauthorized();
        }

        var consumed = await tokens.ConsumeRefreshTokenAsync(raw, cancellationToken);
        if (consumed is null)
        {
            ClearRefreshCookie(context);
            return Results.Unauthorized();
        }

        // Display info was captured onto UserProfile at login.
        var profile = await db.UserProfiles.FindAsync([consumed.UserId], cancellationToken);
        var username = profile?.Username ?? $"user-{consumed.UserId}";

        var rotated = await tokens.IssueRefreshTokenAsync(consumed.UserId, cancellationToken);
        AppendRefreshCookie(context, rotated);

        var access = tokens.IssueAccessToken(consumed.UserId, username, profile?.AvatarHash);
        return Results.Ok(new WebSessionResponse(
            access.Value, access.ExpiresAt.ToDateTimeOffset(), consumed.UserId, username, profile?.AvatarHash));
    }

    /// <summary>Revokes the presented refresh token and clears its cookie.</summary>
    private static async Task<IResult> Logout(
        HttpContext context,
        WebTokenService tokens,
        CancellationToken cancellationToken)
    {
        var raw = context.Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrEmpty(raw))
        {
            await tokens.ConsumeRefreshTokenAsync(raw, cancellationToken);
        }

        ClearRefreshCookie(context);
        return Results.NoContent();
    }

    /// <summary>The OAuth redirect URI this API registered with Discord.</summary>
    private static string RedirectUri(IConfiguration configuration) =>
        $"{(configuration["Api:PublicBaseUrl"] ?? "").TrimEnd('/')}/auth/discord/callback";

    /// <summary>Sets the HttpOnly refresh cookie (Path=/auth; SameSite=None only over HTTPS).</summary>
    private static void AppendRefreshCookie(HttpContext context, IssuedRefreshToken refresh)
    {
        context.Response.Cookies.Append(RefreshCookieName, refresh.Raw, BuildCookieOptions(context, refresh.ExpiresAt.ToDateTimeOffset()));
    }

    /// <summary>Expires the refresh cookie.</summary>
    private static void ClearRefreshCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(RefreshCookieName, BuildCookieOptions(context, null));
    }

    /// <summary>Cookie flags shared by append and clear so they always match.</summary>
    private static CookieOptions BuildCookieOptions(HttpContext context, DateTimeOffset? expires)
    {
        var isHttps = context.Request.IsHttps;
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            // SameSite=None (cross-origin SPA -> API) requires Secure; local HTTP dev falls back to Lax.
            SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = expires,
            Path = "/auth",
        };
    }
}
