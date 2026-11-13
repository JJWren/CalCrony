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

    /// <summary>Fewest days one lookup may list.</summary>
    public const int MinDays = 1;

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

/// <summary>What a member's available hours may be, and what applies when they have saved none:
/// every day from <see cref="DefaultStartMinute"/> to <see cref="DefaultEndMinute"/>, read in the
/// member's own zone. A time of day is minutes from midnight. The bot's commands, the web card and
/// the API's validation all read these here, so they cannot drift.</summary>
public static class AvailableHoursLimits
{
    /// <summary>Minutes in a day — the latest a time of day can be, where it stands for the
    /// midnight that ends the day.</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>The step between the times a member can pick, in minutes.</summary>
    public const int StepMinutes = 30;

    /// <summary>8:00 AM.</summary>
    public const int DefaultStartMinute = 480;

    /// <summary>11:00 PM.</summary>
    public const int DefaultEndMinute = 1380;
}

/// <summary>How a time of day — minutes from midnight, the form available hours take on the wire —
/// is written for a member. One place, so Discord and the web app write the same time the same
/// way.</summary>
public static class TimeOfDay
{
    private const int MinutesPerHour = 60;

    /// <summary>Formats a time of day on the 12-hour clock: "8 AM", "6:30 PM", "12 PM" for noon,
    /// "12 AM" for the start of a day. <see cref="AvailableHoursLimits.MinutesPerDay"/> — the end
    /// of a day — is "midnight".</summary>
    /// <param name="minutes">Minutes from midnight, 0 to <see cref="AvailableHoursLimits.MinutesPerDay"/>.</param>
    /// <returns>The display text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The time lies outside one day.</exception>
    public static string Format(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, AvailableHoursLimits.MinutesPerDay);

        if (minutes == AvailableHoursLimits.MinutesPerDay)
        {
            return "midnight";
        }

        var hour = minutes / MinutesPerHour;
        var minute = minutes % MinutesPerHour;
        var hourOnDial = hour % 12 == 0 ? 12 : hour % 12;
        var suffix = hour < 12 ? "AM" : "PM";
        return minute == 0 ? $"{hourOnDial} {suffix}" : $"{hourOnDial}:{minute:00} {suffix}";
    }
}

/// <summary>One weekday of a member's available hours: a single range on that member's own wall
/// clock.</summary>
/// <param name="Day">The weekday.</param>
/// <param name="StartMinute">Start, in minutes from midnight (0 to 1410), on the
/// <see cref="AvailableHoursLimits.StepMinutes"/> step.</param>
/// <param name="EndMinute">End, in minutes from midnight (30 to
/// <see cref="AvailableHoursLimits.MinutesPerDay"/>, the midnight that closes the day), after the start.</param>
public record AvailableHoursDayDto(DayOfWeek Day, int StartMinute, int EndMinute);

/// <summary>A member's available hours as both surfaces show them.</summary>
/// <param name="IsSet">Whether the member has saved hours. Saving them is also what lets other
/// members list that member's free slots by name.</param>
/// <param name="Days">The saved week — a weekday left out is not available — or, when nothing
/// is saved, the default week that applies instead.</param>
public record AvailableHoursDto(bool IsSet, IReadOnlyList<AvailableHoursDayDto> Days);

/// <summary>Saves a member's available hours, replacing the whole week.</summary>
/// <param name="Days">One entry per available weekday, at least one; a weekday left out is not
/// available.</param>
public record SetAvailableHoursRequest(IReadOnlyList<AvailableHoursDayDto> Days);

/// <summary>Free-slots lookup for a set of users over a run of whole days (BotOnly endpoint).</summary>
/// <param name="UserIds">The Discord user ids to look at (1 to <see cref="FreeSlotLimits.MaxUsers"/> distinct).</param>
/// <param name="RequesterId">The Discord user who asked; days start and end in their zone.</param>
/// <param name="GuildId">The server the command ran in; its timezone stands in for members who have set none.</param>
/// <param name="When">The first day in natural language ("saturday", "nov 12"), at most
/// <see cref="DateTextLimits.MaxLength"/> characters; null or blank means today.</param>
/// <param name="Days">How many days to list (<see cref="FreeSlotLimits.MinDays"/> to <see cref="FreeSlotLimits.MaxDays"/>).</param>
/// <param name="MinimumMinutes">The shortest slot worth listing, in minutes.</param>
/// <param name="ByName">True when the caller named one member rather than a role. A member other
/// than the requester is then listed only if they have saved their own available hours; a role
/// lookup, a role one member holds included, leaves this false.</param>
public record FreeSlotsRequest(
    IReadOnlyList<long> UserIds,
    long RequesterId,
    long GuildId,
    string? When,
    int Days,
    int MinimumMinutes,
    bool ByName = false);

/// <summary>How one member entered a free-slots answer: counted, and on what — or left out, and why.</summary>
public enum FreeSlotMemberState
{
    /// <summary>Counted on a linked calendar and the member's own saved hours.</summary>
    Counted = 0,

    /// <summary>Counted on a linked calendar; the member has saved no hours, so the default hours
    /// (<see cref="AvailableHoursLimits"/>) were applied.</summary>
    DefaultHours = 1,

    /// <summary>Counted on saved hours alone, with no calendar linked.</summary>
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
/// midnight stays with the day it starts on, so the day after can begin inside it.</summary>
/// <param name="Date">The calendar day, in the zone the response names.</param>
/// <param name="Slots">The slots starting on that day; empty when none does.</param>
/// <param name="OpenUntilUtc">When the day begins inside a slot that started on an earlier day, the
/// instant that slot ends: the day is free until then, although the slot is listed under the day
/// it started on. Null when the day begins outside any slot.</param>
public record FreeSlotDayDto(DateOnly Date, IReadOnlyList<FreeSlotDto> Slots, DateTimeOffset? OpenUntilUtc = null);

/// <summary>Free slots for the requested days, plus how each requested member entered the answer.</summary>
/// <param name="TimeZone">The IANA zone the days were cut in: the requester's, else the server's, else UTC.</param>
/// <param name="FirstDay">The first day listed.</param>
/// <param name="LastDay">The last day listed.</param>
/// <param name="CountedMembers">How many members the slots rest on. Zero means nobody could be counted.</param>
/// <param name="Days">One entry per requested day, in order — or none at all when nobody was counted.</param>
/// <param name="Members">One entry per requested member, in request order.</param>
/// <param name="ByName">True when the lookup was made by name and answered under that rule
/// (<see cref="FreeSlotsRequest.ByName"/>). The bot shows a list about a member other than the
/// caller only when this is set, so an API that does not know the rule cannot be asked around it.</param>
public record FreeSlotsResponse(
    string TimeZone,
    DateOnly FirstDay,
    DateOnly LastDay,
    int CountedMembers,
    IReadOnlyList<FreeSlotDayDto> Days,
    IReadOnlyList<FreeSlotMemberDto> Members,
    bool ByName = false);
