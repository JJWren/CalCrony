namespace CalCrony.Contracts;

/// <summary>Returned by POST /auth/refresh — the browser session's access token plus display info.</summary>
public record WebSessionResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresUtc,
    long UserId,
    string Username,
    string? AvatarHash);

/// <summary>A guild the signed-in web user shares with the bot; CanManage mirrors Discord ManageGuild.</summary>
public record WebGuildDto(long Id, string Name, string? IconHash, bool CanManage);

/// <summary>The user's bot-shared guilds from their latest membership snapshot.</summary>
public record WebGuildListResponse(DateTimeOffset SnapshotAtUtc, IReadOnlyList<WebGuildDto> Guilds);
