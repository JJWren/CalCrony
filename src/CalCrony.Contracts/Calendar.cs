namespace CalCrony.Contracts;

/// <summary>External calendar providers CalCrony can link. Google is the only implementation; Apple has no calendar OAuth.</summary>
public enum CalendarProvider
{
    Google = 0,
}

/// <summary>One user's free/busy outcome for an availability check.</summary>
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

/// <summary>Single-use token binding a Discord user to an in-flight calendar OAuth link; StartUrl begins the browser dance.</summary>
public record CalendarLinkTokenDto(string Token, string StartUrl);

/// <summary>Whether (and which) external calendar a user has linked, and when it last refreshed.</summary>
public record CalendarConnectionStatusDto(bool Connected, CalendarProvider? Provider, DateTimeOffset? ConnectedAtUtc);

/// <summary>A busy interval from a linked calendar, in UTC.</summary>
public record BusyBlockDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public long StartUnix => StartUtc.ToUnixTimeSeconds();
    public long EndUnix => EndUtc.ToUnixTimeSeconds();
}

/// <summary>One user's availability over the requested window: status plus busy blocks when connected.</summary>
public record UserAvailabilityDto(long UserId, CalendarAvailabilityStatus Status, IReadOnlyList<BusyBlockDto> BusyBlocks);

/// <summary>Free/busy check over a set of users and a UTC window (BotOnly endpoint).</summary>
public record AvailabilityRequest(IReadOnlyList<long> UserIds, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>Availability results for the requested window, one row per requested user.</summary>
public record AvailabilityResponse(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, IReadOnlyList<UserAvailabilityDto> Results);
