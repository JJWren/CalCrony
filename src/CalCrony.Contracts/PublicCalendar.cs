namespace CalCrony.Contracts;

/// <summary>A server's public-calendar state: off (the default) or on with its slug link.</summary>
/// <param name="Enabled">Whether the public calendar is on.</param>
/// <param name="Slug">The unguessable URL slug while on; null while off.</param>
/// <param name="Path">The web-app path (<c>/c/{slug}</c>) while on; null while off.</param>
/// <param name="Url">The absolute link while on and the web origin is configured; otherwise null
/// (callers fall back to <paramref name="Path"/>).</param>
public record PublicCalendarSettingsDto(bool Enabled, string? Slug, string? Path, string? Url);

/// <summary>Turns a server's public calendar on or off; <paramref name="Regenerate"/> mints a new
/// slug while on, which revokes every previously shared link.</summary>
/// <param name="Enabled">Whether the public calendar should be on.</param>
/// <param name="Regenerate">Replace the slug (only meaningful when enabling an already-on calendar).</param>
public record PublicCalendarRequest(bool Enabled, bool Regenerate = false);

/// <summary>One month of a server's public calendar — the login-free, read-only view. Carries
/// nothing beyond what a passer-by with the link should see: no descriptions, RSVPs, member
/// names, or internal ids.</summary>
/// <param name="GuildName">The server-name snapshot, or null when none is stored (see ADR 0001).</param>
/// <param name="TimeZone">The server's IANA zone — the zone <see cref="PublicCalendarEventDto.StartsAtLocal"/> values are in.</param>
/// <param name="Year">The month's year.</param>
/// <param name="Month">The month (1-12).</param>
/// <param name="Events">The month's events in start order: concrete rows plus projected future occurrences of repeating series.</param>
/// <param name="EarliestMonth">First of the earliest month the calendar will serve (the view is bounded around the server's current month).</param>
/// <param name="LatestMonth">First of the latest month the calendar will serve.</param>
public record PublicCalendarDto(
    string? GuildName,
    string TimeZone,
    int Year,
    int Month,
    IReadOnlyList<PublicCalendarEventDto> Events,
    DateTime EarliestMonth,
    DateTime LatestMonth);

/// <summary>One public calendar entry.</summary>
/// <param name="Title">The event title.</param>
/// <param name="StartsAtUtc">The start instant.</param>
/// <param name="StartsAtLocal">The start wall time in the server's zone (unspecified kind) — what the grid places.</param>
/// <param name="DurationMinutes">The duration, when set.</param>
/// <param name="Location">The location, when set.</param>
/// <param name="ChannelName">The channel-name snapshot, or null when none is stored.</param>
/// <param name="DiscordUrl">Jump link to the event's Discord message, when it has been posted.</param>
/// <param name="Projected">True for a future occurrence projected from a repeat schedule (not yet posted, so no jump link).</param>
public record PublicCalendarEventDto(
    string Title,
    DateTimeOffset StartsAtUtc,
    DateTime StartsAtLocal,
    int? DurationMinutes,
    string? Location,
    string? ChannelName,
    string? DiscordUrl,
    bool Projected);
