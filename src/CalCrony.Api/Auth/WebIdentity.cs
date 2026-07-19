using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CalCrony.Api.Auth;

/// <summary>Claims-principal helpers distinguishing the bot (ApiKey scheme) from web users (JWT).</summary>
public static class WebIdentity
{
    /// <summary>True for the bot: full-trust ApiKey callers carry the client=bot claim.</summary>
    public static bool IsBot(this ClaimsPrincipal principal) =>
        principal.HasClaim(ApiKeyAuthenticationHandler.ClientClaim, ApiKeyAuthenticationHandler.BotClientValue);

    /// <summary>The Discord user id of a JWT web caller; null for the bot or anonymous.</summary>
    public static long? WebUserId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return long.TryParse(sub, out var id) ? id : null;
    }
}
