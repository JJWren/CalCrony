namespace CalCrony.Contracts;

/// <summary>Returned by POST /auth/refresh — the browser session's access token plus display info.</summary>
public record WebSessionResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresUtc,
    long UserId,
    string Username,
    string? AvatarHash);

public record WebGuildDto(long Id, string Name, string? IconHash, bool CanManage);

public record WebGuildListResponse(DateTimeOffset SnapshotAtUtc, IReadOnlyList<WebGuildDto> Guilds);
