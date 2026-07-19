namespace CalCrony.Contracts;

/// <summary>Event lifecycle. The Scheduled/Started numeric values are pinned by the partial
/// unique index IX_Events_SeriesId_Live ("Status" IN (0, 1)) — renumbering breaks the
/// live-occurrence guard.</summary>
public enum EventStatus
{
    Scheduled = 0,
    Started = 1,
    Ended = 2,
    Cancelled = 3,
}

/// <summary>Request to create an event. Datetimes arrive as natural-language text and are parsed
/// server-side. Recurrence: RepeatUntilText and RepeatCount are mutually exclusive and require
/// a Recurrence rule; the rule anchors on the first occurrence.</summary>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="Title">The event title.</param>
/// <param name="WhenText">Natural-language start time.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Recurrence">The repeat rule, when the event should recur.</param>
/// <param name="RepeatUntilText">Natural-language last repeat date.</param>
/// <param name="RepeatCount">Total occurrences including the first.</param>
/// <param name="TemplateId">Template to apply: explicit request fields win, the template fills
/// gaps, and its notification specs are always copied onto the created event.</param>
/// <param name="NoRecurrence">Explicitly suppresses a template's repeat rule (unset does not —
/// a template rule applies when no explicit rule is sent). Conflicts with Recurrence.</param>
/// <param name="AttendeeRoleId">Existing Discord role granted to "Going" RSVPs and revoked when
/// the event ends. Bot callers only — the web can't enumerate roles, so it is ignored there.</param>
public record CreateEventRequest(
    long CreatorId,
    string Title,
    string WhenText,
    long ChannelId,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null,
    RecurrenceRuleDto? Recurrence = null,
    string? RepeatUntilText = null,
    int? RepeatCount = null,
    Guid? TemplateId = null,
    bool NoRecurrence = false,
    long? AttendeeRoleId = null);

/// <summary>Partial update; null fields are left unchanged. Scope is required when the target is
/// the live occurrence of a non-ended series and ignored otherwise.</summary>
/// <param name="EditorId">The editing user's Discord id (ignored for web callers).</param>
/// <param name="Title">The event title.</param>
/// <param name="WhenText">Natural-language start time.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="Scope">Whether the change applies to this occurrence or the whole series.</param>
/// <param name="AttendeeRoleId">Replaces the attendee role (bot callers only; existing grants are
/// re-synced to the new role). Null leaves it unchanged — clear with ClearAttendeeRole.</param>
/// <param name="ClearAttendeeRole">Removes the attendee role (existing grants are revoked).
/// Conflicts with AttendeeRoleId.</param>
public record UpdateEventRequest(
    long EditorId,
    string? Title = null,
    string? WhenText = null,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null,
    EventStatus? Status = null,
    EditScope? Scope = null,
    long? AttendeeRoleId = null,
    bool ClearAttendeeRole = false);

/// <summary>One RSVP choice on an event (emote + label, optional capacity).</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Emote">The option emoji.</param>
/// <param name="Label">The display label.</param>
/// <param name="SortOrder">Display ordering index.</param>
/// <param name="Capacity">Optional attendee cap.</param>
public record RsvpOptionDto(Guid Id, string Emote, string Label, int SortOrder, int? Capacity);

/// <summary>A user's RSVP: which option they picked.</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="OptionId">The RSVP/poll option id.</param>
public record RsvpDto(long UserId, Guid OptionId);

/// <summary>An event with its RSVP options and current RSVPs. RecurrenceSummary is the human-readable repeat rule, null for one-offs and ended series.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="Title">The event title.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="StartsAtUtc">The start instant (UTC).</param>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="Options">The RSVP options.</param>
/// <param name="Rsvps">The RSVP rows.</param>
/// <param name="SeriesId">The series id.</param>
/// <param name="RecurrenceSummary">Human-readable repeat rule; null for one-offs and ended series.</param>
/// <param name="NativeEventId">The mirrored Discord scheduled-event id, when mirrored.</param>
/// <param name="AttendeeRoleId">The Discord role granted to "Going" RSVPs, when set.</param>
public record EventDto(
    Guid Id,
    long GuildId,
    long CreatorId,
    string Title,
    string? Description,
    DateTimeOffset StartsAtUtc,
    string TimeZone,
    int? DurationMinutes,
    long ChannelId,
    long? MessageId,
    string? Location,
    string? ImageUrl,
    EventStatus Status,
    IReadOnlyList<RsvpOptionDto> Options,
    IReadOnlyList<RsvpDto> Rsvps,
    Guid? SeriesId = null,
    string? RecurrenceSummary = null,
    long? NativeEventId = null,
    long? AttendeeRoleId = null)
{
    /// <summary>Unix seconds of the start time, for Discord &lt;t:...&gt; timestamps.</summary>
    public long StartsAtUnix => StartsAtUtc.ToUnixTimeSeconds();
}

/// <summary>Records where the bot posted an event's embed (bot-only; only the bot knows message ids).</summary>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id.</param>
public record SetEventMessageRequest(long ChannelId, long MessageId);

/// <summary>Records the Discord Guild Scheduled Event mirroring an event (bot-only). Null clears
/// a stale id after the native event was deleted Discord-side.</summary>
/// <param name="NativeEventId">The Discord scheduled-event id, or null to clear.</param>
public record SetNativeEventRequest(long? NativeEventId);

/// <summary>Sets or replaces the calling user's RSVP to the given option.</summary>
/// <param name="OptionId">The RSVP/poll option id.</param>
public record RsvpRequest(Guid OptionId);

/// <summary>A guild's timezone, the default channel web-created embeds post to, and whether
/// events mirror into Discord's native scheduled events.</summary>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="DefaultChannelId">The channel web-created embeds post to, when set.</param>
/// <param name="MirrorNativeEvents">When true, new events mirror into the server's Events tab
/// (requires the bot to hold Manage Events).</param>
public record GuildSettingsDto(string TimeZone, long? DefaultChannelId, bool MirrorNativeEvents = false);

/// <summary>A user's personal timezone (null = use the server's) and DM-confirmation preference.</summary>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="DmConfirmations">Whether the bot may DM confirmations.</param>
public record UserSettingsDto(string? TimeZone, bool DmConfirmations);

/// <summary>TimeZone (IANA id), when set, overrides the user/guild zone resolution — used where
/// the caller must preview in a specific zone, e.g. a series' stored zone for schedule edits.</summary>
/// <param name="Text">The text to parse.</param>
/// <param name="UserId">The Discord user id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
public record ParseDateTimeRequest(string Text, long? UserId = null, long? GuildId = null, string? TimeZone = null);

/// <summary>A parsed datetime: the UTC instant, its Unix seconds, and the zone it was resolved in.</summary>
/// <param name="Utc">The UTC instant.</param>
/// <param name="Unix">Unix seconds of the instant.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
public record ParseDateTimeResponse(DateTimeOffset Utc, long Unix, string TimeZone);

/// <summary>A selectable timezone: canonical IANA id + display label with the current UTC offset,
/// e.g. "America/Chicago — UTC-05:00".</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Label">The display label.</param>
public record TimeZoneOptionDto(string Id, string Label);

/// <summary>Uniform error body every non-2xx JSON response carries.</summary>
/// <param name="Error">The user-facing error text.</param>
public record ErrorResponse(string Error);
