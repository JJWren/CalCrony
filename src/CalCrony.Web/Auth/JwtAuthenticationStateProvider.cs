using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace CalCrony.Web.Auth;

/// <summary>Derives the Blazor authentication state from the stored JWT (expiry-checked client-side; the API stays authoritative).</summary>
public sealed class JwtAuthenticationStateProvider(ITokenStore tokenStore) : AuthenticationStateProvider
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <summary>Builds the current authentication state from the stored token, anonymous when absent or expired.</summary>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await tokenStore.GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthenticationState(Anonymous);
        }

        var claims = JwtParser.ParseClaimsFromJwt(token).ToList();
        if (claims.Count == 0)
        {
            // Malformed token — an "authenticated" UI backed by a token the API rejects helps nobody.
            await tokenStore.ClearAsync();
            return new AuthenticationState(Anonymous);
        }

        // CalCrony JWTs carry `sub` (Discord id) and `name`; map them to the .NET claim types
        // AuthorizeView and friends look for.
        if (claims.All(c => c.Type != ClaimTypes.NameIdentifier) && claims.FirstOrDefault(c => c.Type == "sub") is { } sub)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, sub.Value));
        }

        if (claims.All(c => c.Type != ClaimTypes.Name) && claims.FirstOrDefault(c => c.Type == "name") is { } name)
        {
            claims.Add(new Claim(ClaimTypes.Name, name.Value));
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "jwt")));
    }

    /// <summary>Signals Blazor that the auth state changed (login, refresh, logout).</summary>
    public void NotifyAuthenticationChanged() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
