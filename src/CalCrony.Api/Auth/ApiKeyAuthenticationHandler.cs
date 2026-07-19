using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CalCrony.Api.Auth;

/// <summary>
/// The bot's X-Api-Key credential as a real authentication scheme. Absent header returns
/// NoResult so the Bearer scheme can try; present-but-invalid fails outright. A successful
/// principal carries the <c>client=bot</c> claim, which the BotOnly policy requires.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ClientClaim = "client";
    public const string BotClientValue = "bot";

    /// <summary>Validates the X-Api-Key header; absent header yields NoResult so the JWT scheme can try instead.</summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawKey = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return AuthenticateResult.NoResult();
        }

        if (!await validator.IsValidAsync(rawKey, Context.RequestAborted))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var identity = new ClaimsIdentity([new Claim(ClientClaim, BotClientValue)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>Writes the 401 challenge unless another scheme's challenge already started the response.</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // With a multi-scheme policy both schemes get challenged; only the first writes.
        if (Response.HasStarted)
        {
            return;
        }

        // Keep the pre-scheme-conversion response shape the bot's SendAsync surfaces.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
    }
}
