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
/// <param name="Id">The Discord snowflake id.</param>
/// <param name="Username">The display name to embed in the token.</param>
/// <param name="GlobalName">The Discord global display name, when set.</param>
/// <param name="AvatarHash">The Discord avatar hash, when set.</param>
public sealed record DiscordUserInfo(long Id, string Username, string? GlobalName, string? AvatarHash);

/// <summary>One of the user's guilds with the computed can-manage flag.</summary>
/// <param name="Id">The Discord snowflake id.</param>
/// <param name="Name">The guild name.</param>
/// <param name="IconHash">The Discord icon hash, when set.</param>
/// <param name="CanManage">Whether the user holds ManageGuild (or owns the guild).</param>
public sealed record DiscordGuildInfo(long Id, string Name, string? IconHash, bool CanManage);
