using System.Text.Json.Serialization;

namespace CalCrony.Api.Services;

public sealed class DiscordAuthProvider(HttpClient http, IConfiguration configuration, ILogger<DiscordAuthProvider> logger)
    : IDiscordAuthProvider
{
    private const string AuthorizeUrl = "https://discord.com/oauth2/authorize";
    private const string ApiBase = "https://discord.com/api/v10";

    /// <summary>Discord permission bit for Manage Server.</summary>
    private const ulong ManageGuildBit = 0x20;

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        var clientId = configuration["Auth:Discord:ClientId"];
        // prompt=none skips the consent screen for users who already authorized this app,
        // making "re-sync servers" a near-instant bounce.
        return $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(clientId ?? "")}" +
               $"&response_type=code&scope={Uri.EscapeDataString("identify guilds")}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&state={Uri.EscapeDataString(state)}&prompt=none";
    }

    public async Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = configuration["Auth:Discord:ClientId"] ?? "",
            ["client_secret"] = configuration["Auth:Discord:ClientSecret"] ?? "",
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        });

        using var response = await http.PostAsync($"{ApiBase}/oauth2/token", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Discord token exchange failed ({Status}): {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Discord token exchange failed with {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponseBody>(cancellationToken);
        return payload?.AccessToken
            ?? throw new InvalidOperationException("Discord token exchange returned no access_token.");
    }

    public async Task<DiscordUserInfo> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        var user = await GetAsync<UserBody>("/users/@me", accessToken, cancellationToken);
        return new DiscordUserInfo(
            long.Parse(user.Id), user.Username, user.GlobalName, user.Avatar);
    }

    public async Task<IReadOnlyList<DiscordGuildInfo>> GetGuildsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var guilds = await GetAsync<List<GuildBody>>("/users/@me/guilds", accessToken, cancellationToken);
        return guilds
            .Select(g => new DiscordGuildInfo(
                long.Parse(g.Id),
                g.Name,
                g.Icon,
                g.Owner || (ulong.TryParse(g.Permissions, out var bits) && (bits & ManageGuildBit) != 0)))
            .ToList();
    }

    private async Task<T> GetAsync<T>(string path, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException($"Discord returned an empty body for {path}.");
    }

    private sealed record TokenResponseBody([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record UserBody(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("avatar")] string? Avatar);

    private sealed record GuildBody(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("owner")] bool Owner,
        [property: JsonPropertyName("permissions")] string? Permissions);
}
