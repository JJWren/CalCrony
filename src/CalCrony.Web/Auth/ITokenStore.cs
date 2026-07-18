namespace CalCrony.Web.Auth;

public interface ITokenStore
{
    Task<string?> GetAccessTokenAsync();

    Task SetAccessTokenAsync(string accessToken);

    Task ClearAsync();
}
