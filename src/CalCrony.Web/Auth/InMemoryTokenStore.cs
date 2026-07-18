namespace CalCrony.Web.Auth;

/// <summary>Kept in memory (never local/sessionStorage) so injected script can't read a
/// persisted token; a page reload re-hydrates from the HttpOnly refresh cookie at startup.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private string? accessToken;

    public Task<string?> GetAccessTokenAsync() => Task.FromResult(accessToken);

    public Task SetAccessTokenAsync(string token)
    {
        accessToken = token;
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        accessToken = null;
        return Task.CompletedTask;
    }
}
