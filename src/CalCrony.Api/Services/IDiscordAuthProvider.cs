namespace CalCrony.Api.Services;

/// <summary>
/// Discord OAuth + identity abstraction for web login. Mirrors ICalendarProvider so tests can
/// swap in a fake. The user's Discord access token lives only inside a single login exchange —
/// it is never persisted.
/// </summary>
public interface IDiscordAuthProvider
{
    string BuildAuthorizationUrl(string redirectUri, string state);

    /// <summary>Exchanges an OAuth code for a short-lived user access token. Throws on failure —
    /// callers have a single failure path (redirect to the login page with an error code).</summary>
    Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken);

    Task<DiscordUserInfo> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordGuildInfo>> GetGuildsAsync(string accessToken, CancellationToken cancellationToken);
}

/// <summary>The authorizing Discord user's identity and display fields.</summary>
public sealed record DiscordUserInfo(long Id, string Username, string? GlobalName, string? AvatarHash);

/// <summary>One of the user's guilds with the computed can-manage flag.</summary>
public sealed record DiscordGuildInfo(long Id, string Name, string? IconHash, bool CanManage);
