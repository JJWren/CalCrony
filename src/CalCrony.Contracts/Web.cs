namespace CalCrony.Contracts;

/// <summary>Returned by POST /auth/refresh — the browser session's access token plus display info.</summary>
/// <param name="AccessToken">The bearer token for API calls.</param>
/// <param name="AccessTokenExpiresUtc">When the access token expires.</param>
/// <param name="UserId">The Discord user id.</param>
/// <param name="Username">The display name to embed in the token.</param>
/// <param name="AvatarHash">The Discord avatar hash, when set.</param>
public record WebSessionResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresUtc,
    long UserId,
    string Username,
    string? AvatarHash);

/// <summary>A guild the signed-in web user shares with the bot; CanManage mirrors Discord ManageGuild.</summary>
/// <param name="Id">The guild id.</param>
/// <param name="Name">The guild name.</param>
/// <param name="IconHash">The Discord icon hash, when set.</param>
/// <param name="CanManage">Whether the user holds ManageGuild (or owns the guild).</param>
public record WebGuildDto(long Id, string Name, string? IconHash, bool CanManage);

/// <summary>The user's bot-shared guilds from their latest membership snapshot.</summary>
/// <param name="SnapshotAtUtc">When the guild snapshot was taken.</param>
/// <param name="Guilds">The guild rows.</param>
public record WebGuildListResponse(DateTimeOffset SnapshotAtUtc, IReadOnlyList<WebGuildDto> Guilds);
