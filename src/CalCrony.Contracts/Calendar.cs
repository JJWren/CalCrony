namespace CalCrony.Contracts;

public enum CalendarProvider
{
    Google = 0,
}

public enum CalendarAvailabilityStatus
{
    NotConnected = 0,
    Free = 1,
    Busy = 2,

    /// <summary>The stored connection's refresh token was rejected by the provider (e.g. Google's
    /// 7-day Testing-mode expiry) — the user needs to run /calendar connect again.</summary>
    ReconnectRequired = 3,

    /// <summary>Transient failure (timeout, network, unexpected provider error).</summary>
    Error = 4,
}

public record CalendarLinkTokenDto(string Token, string StartUrl);

public record CalendarConnectionStatusDto(bool Connected, CalendarProvider? Provider, DateTimeOffset? ConnectedAtUtc);

public record BusyBlockDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public long StartUnix => StartUtc.ToUnixTimeSeconds();
    public long EndUnix => EndUtc.ToUnixTimeSeconds();
}

public record UserAvailabilityDto(long UserId, CalendarAvailabilityStatus Status, IReadOnlyList<BusyBlockDto> BusyBlocks);

public record AvailabilityRequest(IReadOnlyList<long> UserIds, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

public record AvailabilityResponse(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, IReadOnlyList<UserAvailabilityDto> Results);
