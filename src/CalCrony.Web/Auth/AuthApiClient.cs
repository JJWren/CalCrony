using System.Net.Http.Json;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace CalCrony.Web.Auth;

/// <summary>Session bootstrap/teardown. Login itself is a top-level navigation to the API's
/// /auth/discord/start (no XHR involved); this client covers refresh-on-boot and logout.</summary>
public sealed class AuthApiClient(
    HttpClient http, ITokenStore tokenStore, JwtAuthenticationStateProvider authStateProvider)
{
    public WebSessionResponse? Session { get; private set; }

    /// <summary>URL that begins the Discord OAuth login dance, returning to returnUrl afterward.</summary>
    public string BuildLoginUrl(string returnUrl = "/app") =>
        $"{http.BaseAddress!.ToString().TrimEnd('/')}/auth/discord/start?returnUrl={Uri.EscapeDataString(returnUrl)}";

    /// <summary>Re-hydrates the in-memory session from the HttpOnly refresh cookie. Quietly
    /// no-ops when there is no session — the visitor is simply anonymous.</summary>
    public async Task<bool> TryRefreshAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        try
        {
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var session = await response.Content.ReadFromJsonAsync<WebSessionResponse>();
            if (session is null)
            {
                return false;
            }

            Session = session;
            await tokenStore.SetAccessTokenAsync(session.AccessToken);
            authStateProvider.NotifyAuthenticationChanged();
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>Revokes the refresh cookie server-side and clears local auth state.</summary>
    public async Task LogoutAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        try
        {
            using var _ = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            // Cookie cleanup failed server-side; local logout still proceeds.
        }

        Session = null;
        await tokenStore.ClearAsync();
        authStateProvider.NotifyAuthenticationChanged();
    }
}
