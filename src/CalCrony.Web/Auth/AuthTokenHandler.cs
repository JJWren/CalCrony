using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace CalCrony.Web.Auth;

/// <summary>Attaches the bearer token to API calls and performs one serialized silent
/// refresh + retry on 401. Ported from FairShare.Web.</summary>
/// <param name="tokenStore">The in-memory access-token store.</param>
/// <param name="authStateProvider">The auth-state notifier.</param>
public sealed class AuthTokenHandler(ITokenStore tokenStore, JwtAuthenticationStateProvider authStateProvider)
    : DelegatingHandler
{
    // Shared across handler instances so concurrent 401s serialize onto a single refresh
    // attempt instead of racing the API's rotate-on-use refresh token.
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    /// <summary>Attaches the bearer token to outgoing API calls and transparently refreshes-and-retries once on a 401.</summary>
    /// <param name="request">The outgoing API request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The (possibly retried) API response.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await tokenStore.GetAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            return response;
        }

        // A 401 from the anonymous auth endpoints is a real auth failure, not an expired
        // access token — don't let it trigger refresh+retry.
        if (uri.AbsolutePath.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        var refreshed = await RefreshAccessTokenAsync(new Uri(uri, "/auth/refresh"), accessToken, cancellationToken);
        if (refreshed is null)
        {
            return response;
        }

        response.Dispose();
        var retry = await CloneRequestAsync(request);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        return await base.SendAsync(retry, cancellationToken);
    }

    /// <summary>Exchanges the HttpOnly refresh cookie for a new access token, deduplicating concurrent refreshes.</summary>
    /// <param name="refreshUri">The absolute refresh endpoint.</param>
    /// <param name="failedToken">The token that just got a 401, to avoid re-sending it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new access token, or null when the session is gone.</returns>
    private async Task<string?> RefreshAccessTokenAsync(Uri refreshUri, string? failedToken, CancellationToken cancellationToken)
    {
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have refreshed while we waited on the lock.
            var current = await tokenStore.GetAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(current) && current != failedToken)
            {
                return current;
            }

            using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, refreshUri);
            refreshRequest.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            using var refreshResponse = await base.SendAsync(refreshRequest, cancellationToken);
            if (!refreshResponse.IsSuccessStatusCode)
            {
                await tokenStore.ClearAsync();
                authStateProvider.NotifyAuthenticationChanged();
                return null;
            }

            var session = await refreshResponse.Content.ReadFromJsonAsync<WebSessionResponse>(cancellationToken: cancellationToken);
            if (session is null)
            {
                await tokenStore.ClearAsync();
                authStateProvider.NotifyAuthenticationChanged();
                return null;
            }

            await tokenStore.SetAccessTokenAsync(session.AccessToken);
            authStateProvider.NotifyAuthenticationChanged();
            return session.AccessToken;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    /// <summary>Clones a request (headers + buffered content) so it can be resent after a refresh.</summary>
    /// <param name="original">The request to clone.</param>
    /// <returns>The resendable clone.</returns>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Blazor stores per-request settings (e.g. BrowserRequestCredentials) in Options;
        // losing them on retry would silently drop "include cookies".
        foreach (var option in original.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        return clone;
    }
}
