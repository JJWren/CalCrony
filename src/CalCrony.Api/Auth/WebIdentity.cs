using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CalCrony.Api.Auth;

public static class WebIdentity
{
    public static bool IsBot(this ClaimsPrincipal principal) =>
        principal.HasClaim(ApiKeyAuthenticationHandler.ClientClaim, ApiKeyAuthenticationHandler.BotClientValue);

    /// <summary>The Discord user id of a JWT web caller; null for the bot or anonymous.</summary>
    public static long? WebUserId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return long.TryParse(sub, out var id) ? id : null;
    }
}
