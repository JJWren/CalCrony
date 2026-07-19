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
/// <param name="Token">The token value.</param>
/// <param name="StartUrl">The URL that begins the OAuth consent flow.</param>
public record CalendarLinkTokenDto(string Token, string StartUrl);

/// <summary>Whether (and which) external calendar a user has linked, and when it last refreshed.</summary>
/// <param name="Connected">Whether a calendar is linked.</param>
/// <param name="Provider">The linked calendar provider.</param>
/// <param name="ConnectedAtUtc">When the calendar was linked.</param>
public record CalendarConnectionStatusDto(bool Connected, CalendarProvider? Provider, DateTimeOffset? ConnectedAtUtc);

/// <summary>A busy interval from a linked calendar, in UTC.</summary>
/// <param name="StartUtc">Window start (UTC).</param>
/// <param name="EndUtc">Window end (UTC).</param>
public record BusyBlockDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public long StartUnix => StartUtc.ToUnixTimeSeconds();
    public long EndUnix => EndUtc.ToUnixTimeSeconds();
}

/// <summary>One user's availability over the requested window: status plus busy blocks when connected.</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="Status">The availability status.</param>
/// <param name="BusyBlocks">The busy intervals.</param>
public record UserAvailabilityDto(long UserId, CalendarAvailabilityStatus Status, IReadOnlyList<BusyBlockDto> BusyBlocks);

/// <summary>Free/busy check over a set of users and a UTC window (BotOnly endpoint).</summary>
/// <param name="UserIds">The Discord user ids to check.</param>
/// <param name="StartsAtUtc">The start instant (UTC).</param>
/// <param name="EndsAtUtc">The end instant (UTC).</param>
public record AvailabilityRequest(IReadOnlyList<long> UserIds, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>Availability results for the requested window, one row per requested user.</summary>
/// <param name="StartsAtUtc">The start instant (UTC).</param>
/// <param name="EndsAtUtc">The end instant (UTC).</param>
/// <param name="Results">The per-user availability results.</param>
public record AvailabilityResponse(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, IReadOnlyList<UserAvailabilityDto> Results);
