using System.Text.Json.Serialization;

namespace CalCrony.Api.Services;

/// <summary>Real Discord OAuth client: consent URL, code exchange, and identity/guild reads.</summary>
/// <param name="http">The configured HTTP client.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
public sealed class DiscordAuthProvider(HttpClient http, IConfiguration configuration, ILogger<DiscordAuthProvider> logger)
    : IDiscordAuthProvider
{
    private const string AuthorizeUrl = "https://discord.com/oauth2/authorize";
    private const string ApiBase = "https://discord.com/api/v10";

    /// <summary>Discord permission bit for Manage Server.</summary>
    private const ulong ManageGuildBit = 0x20;

    /// <summary>Consent-page URL with the identify+guilds scopes and CSRF state.</summary>
    /// <param name="redirectUri">The OAuth redirect URI registered with the provider.</param>
    /// <param name="state">The CSRF state value.</param>
    /// <returns>The consent-page URL.</returns>
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

    /// <summary>Exchanges the authorization code for a user access token (never persisted).</summary>
    /// <param name="code">The authorization code from the provider callback.</param>
    /// <param name="redirectUri">The OAuth redirect URI registered with the provider.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The user access token.</returns>
    /// <exception cref="InvalidOperationException">When the exchange fails or returns no access token.</exception>
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

    /// <summary>Reads the authorizing user's id and display fields.</summary>
    /// <param name="accessToken">The provider access token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The authorizing user's identity.</returns>
    /// <exception cref="HttpRequestException">When Discord rejects the call.</exception>
    /// <exception cref="InvalidOperationException">When Discord returns an empty body.</exception>
    public async Task<DiscordUserInfo> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        var user = await GetAsync<UserBody>("/users/@me", accessToken, cancellationToken);
        return new DiscordUserInfo(
            long.Parse(user.Id), user.Username, user.GlobalName, user.Avatar);
    }

    /// <summary>Reads the user's guilds with a computed can-manage flag (ManageGuild bit or ownership).</summary>
    /// <param name="accessToken">The provider access token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The user's guilds with can-manage flags.</returns>
    /// <exception cref="HttpRequestException">When Discord rejects the call.</exception>
    /// <exception cref="InvalidOperationException">When Discord returns an empty body.</exception>
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

    /// <summary>Discord token response shape.</summary>
    /// <param name="AccessToken">The provider access token.</param>
    private sealed record TokenResponseBody([property: JsonPropertyName("access_token")] string? AccessToken);

    /// <summary>Discord /users/@me response shape.</summary>
    /// <param name="Id">The Discord snowflake id.</param>
    /// <param name="Username">The display name to embed in the token.</param>
    /// <param name="GlobalName">The Discord global display name, when set.</param>
    /// <param name="Avatar">The avatar hash, when set.</param>
    private sealed record UserBody(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("avatar")] string? Avatar);

    /// <summary>Discord guild list item shape.</summary>
    /// <param name="Id">The Discord snowflake id.</param>
    /// <param name="Name">The guild name.</param>
    /// <param name="Icon">The guild icon hash, when set.</param>
    /// <param name="Owner">True when the user owns the guild.</param>
    /// <param name="Permissions">The permission bitfield string.</param>
    private sealed record GuildBody(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("owner")] bool Owner,
        [property: JsonPropertyName("permissions")] string? Permissions);
}
