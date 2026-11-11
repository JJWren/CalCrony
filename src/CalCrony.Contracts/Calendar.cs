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

/// <summary>Limits for a free-slots lookup. The bot's option bounds and the API's validation both
/// read them here, so the two cannot drift.</summary>
public static class FreeSlotLimits
{
    /// <summary>Most members one lookup may cover — each linked one costs a provider query.</summary>
    public const int MaxUsers = 50;

    /// <summary>Days listed when the caller names none.</summary>
    public const int DefaultDays = 7;

    /// <summary>Most days one lookup may list.</summary>
    public const int MaxDays = 14;

    /// <summary>How far ahead the first day may lie, in days. A date typed without its year that
    /// has already passed rolls to next year, which is never further than this.</summary>
    public const int MaxDaysAhead = 366;

    /// <summary>Shortest slot listed, in minutes, when the caller names none.</summary>
    public const int DefaultMinimumMinutes = 60;

    /// <summary>Smallest "shortest slot" a caller may ask for, in minutes.</summary>
    public const int MinMinimumMinutes = 15;

    /// <summary>Largest "shortest slot" a caller may ask for, in minutes (a whole day).</summary>
    public const int MaxMinimumMinutes = 1440;
}

/// <summary>The available hours a member gets when they have saved none: every day from
/// <see cref="DefaultStartMinute"/> to <see cref="DefaultEndMinute"/>, read in the member's own
/// zone. A time of day is minutes from midnight.</summary>
public static class AvailableHoursLimits
{
    /// <summary>8:00 AM.</summary>
    public const int DefaultStartMinute = 480;

    /// <summary>11:00 PM.</summary>
    public const int DefaultEndMinute = 1380;
}

/// <summary>Free-slots lookup for a set of users over a run of whole days (BotOnly endpoint).</summary>
/// <param name="UserIds">The Discord user ids to look at (1 to <see cref="FreeSlotLimits.MaxUsers"/> distinct).</param>
/// <param name="RequesterId">The Discord user who asked; days start and end in their zone.</param>
/// <param name="GuildId">The server the command ran in; its timezone stands in for members who have set none.</param>
/// <param name="When">The first day in natural language ("saturday", "nov 12"); null or blank means today.</param>
/// <param name="Days">How many days to list (1 to <see cref="FreeSlotLimits.MaxDays"/>).</param>
/// <param name="MinimumMinutes">The shortest slot worth listing, in minutes.</param>
public record FreeSlotsRequest(
    IReadOnlyList<long> UserIds, long RequesterId, long GuildId, string? When, int Days, int MinimumMinutes);

/// <summary>How one member entered a free-slots answer: counted, and on what — or left out, and why.</summary>
public enum FreeSlotMemberState
{
    /// <summary>Counted on a linked calendar and the member's own saved hours. Not returned until
    /// members can save hours.</summary>
    Counted = 0,

    /// <summary>Counted on a linked calendar; the member has saved no hours, so the default hours
    /// (<see cref="AvailableHoursLimits"/>) were applied.</summary>
    DefaultHours = 1,

    /// <summary>Counted on saved hours alone, with no calendar linked. Not returned until members
    /// can save hours.</summary>
    HoursOnly = 2,

    /// <summary>Not counted: there is nothing to go on — no calendar linked and no hours saved.</summary>
    NotCounted = 3,

    /// <summary>Not counted: the linked calendar's refresh token was rejected by the provider — the
    /// member needs to run /calendar connect again.</summary>
    ReconnectRequired = 4,

    /// <summary>Not counted: the linked calendar could not be read right now (transient failure).</summary>
    Error = 5,
}

/// <summary>One member's part in a free-slots answer.</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="State">Whether the member was counted, and on what.</param>
public record FreeSlotMemberDto(long UserId, FreeSlotMemberState State);

/// <summary>A stretch of time, in UTC, in which every counted member is available.</summary>
/// <param name="StartUtc">Slot start (UTC).</param>
/// <param name="EndUtc">Slot end (UTC).</param>
public record FreeSlotDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public long StartUnix => StartUtc.ToUnixTimeSeconds();
    public long EndUnix => EndUtc.ToUnixTimeSeconds();
}

/// <summary>One requested day and the slots that start on it, in order. A slot that runs past
/// midnight stays with the day it starts on.</summary>
/// <param name="Date">The calendar day, in the zone the response names.</param>
/// <param name="Slots">The slots starting on that day; empty when nothing is free.</param>
public record FreeSlotDayDto(DateOnly Date, IReadOnlyList<FreeSlotDto> Slots);

/// <summary>Free slots for the requested days, plus how each requested member entered the answer.</summary>
/// <param name="TimeZone">The IANA zone the days were cut in: the requester's, else the server's, else UTC.</param>
/// <param name="FirstDay">The first day listed.</param>
/// <param name="LastDay">The last day listed.</param>
/// <param name="CountedMembers">How many members the slots rest on. Zero means nobody could be counted.</param>
/// <param name="Days">One entry per requested day, in order — or none at all when nobody was counted.</param>
/// <param name="Members">One entry per requested member, in request order.</param>
public record FreeSlotsResponse(
    string TimeZone,
    DateOnly FirstDay,
    DateOnly LastDay,
    int CountedMembers,
    IReadOnlyList<FreeSlotDayDto> Days,
    IReadOnlyList<FreeSlotMemberDto> Members);
