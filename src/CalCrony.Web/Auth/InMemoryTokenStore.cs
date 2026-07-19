namespace CalCrony.Web.Auth;

/// <summary>Kept in memory (never local/sessionStorage) so injected script can't read a
/// persisted token; a page reload re-hydrates from the HttpOnly refresh cookie at startup.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private string? accessToken;

    /// <summary>Returns the current access token, if any.</summary>
    /// <returns>The stored token, or null.</returns>
    public Task<string?> GetAccessTokenAsync() => Task.FromResult(accessToken);

    /// <summary>Stores a freshly issued access token.</summary>
    /// <param name="token">The token value.</param>
    public Task SetAccessTokenAsync(string token)
    {
        accessToken = token;
        return Task.CompletedTask;
    }

    /// <summary>Drops the stored token (logout or refresh failure).</summary>
    public Task ClearAsync()
    {
        accessToken = null;
        return Task.CompletedTask;
    }
}
