using CalCrony.Api.Services;

namespace CalCrony.Api.Tests;

/// <summary>In-memory Discord OAuth double. Configure a user + guild list per "code"; the login
/// endpoints then behave exactly as with real Discord, minus the network.</summary>
public sealed class FakeDiscordAuthProvider : IDiscordAuthProvider
{
    public const string InvalidCode = "invalid-code";

    /// <summary>code → (user, guilds). The fake access token is "token-{code}".</summary>
    public Dictionary<string, (DiscordUserInfo User, List<DiscordGuildInfo> Guilds)> Logins { get; } = [];

    public string BuildAuthorizationUrl(string redirectUri, string state) =>
        $"https://discord.com/oauth2/authorize?client_id=fake&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&response_type=code&scope=identify+guilds&state={state}&prompt=none";

    public Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        if (code == InvalidCode || !Logins.ContainsKey(code))
        {
            throw new InvalidOperationException("Fake: Discord rejected the code.");
        }

        return Task.FromResult($"token-{code}");
    }

    public Task<DiscordUserInfo> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Logins[CodeFromToken(accessToken)].User);

    public Task<IReadOnlyList<DiscordGuildInfo>> GetGuildsAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscordGuildInfo>>(Logins[CodeFromToken(accessToken)].Guilds);

    private static string CodeFromToken(string accessToken) => accessToken["token-".Length..];
}
