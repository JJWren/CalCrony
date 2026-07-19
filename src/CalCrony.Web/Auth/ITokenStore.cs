namespace CalCrony.Web.Auth;

/// <summary>Holds the current access token. Memory-only by design; tokens never touch browser storage.</summary>
public interface ITokenStore
{
    Task<string?> GetAccessTokenAsync();

    Task SetAccessTokenAsync(string accessToken);

    Task ClearAsync();
}
