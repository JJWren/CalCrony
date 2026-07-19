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
    int? RepeatCount = null);

/// <summary>Partial update; null fields are left unchanged. Scope is required when the target is
/// the live occurrence of a non-ended series and ignored otherwise.</summary>
public record UpdateEventRequest(
    long EditorId,
    string? Title = null,
    string? WhenText = null,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null,
    EventStatus? Status = null,
    EditScope? Scope = null);

/// <summary>One RSVP choice on an event (emote + label, optional capacity).</summary>
public record RsvpOptionDto(Guid Id, string Emote, string Label, int SortOrder, int? Capacity);

/// <summary>A user's RSVP: which option they picked.</summary>
public record RsvpDto(long UserId, Guid OptionId);

/// <summary>An event with its RSVP options and current RSVPs. RecurrenceSummary is the human-readable repeat rule, null for one-offs and ended series.</summary>
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
    string? RecurrenceSummary = null)
{
    /// <summary>Unix seconds of the start time, for Discord &lt;t:...&gt; timestamps.</summary>
    public long StartsAtUnix => StartsAtUtc.ToUnixTimeSeconds();
}

/// <summary>Records where the bot posted an event's embed (bot-only; only the bot knows message ids).</summary>
public record SetEventMessageRequest(long ChannelId, long MessageId);

/// <summary>Sets or replaces the calling user's RSVP to the given option.</summary>
public record RsvpRequest(Guid OptionId);

/// <summary>A guild's timezone and the default channel web-created embeds post to.</summary>
public record GuildSettingsDto(string TimeZone, long? DefaultChannelId);

/// <summary>A user's personal timezone (null = use the server's) and DM-confirmation preference.</summary>
public record UserSettingsDto(string? TimeZone, bool DmConfirmations);

/// <summary>TimeZone (IANA id), when set, overrides the user/guild zone resolution — used where
/// the caller must preview in a specific zone, e.g. a series' stored zone for schedule edits.</summary>
public record ParseDateTimeRequest(string Text, long? UserId = null, long? GuildId = null, string? TimeZone = null);

/// <summary>A parsed datetime: the UTC instant, its Unix seconds, and the zone it was resolved in.</summary>
public record ParseDateTimeResponse(DateTimeOffset Utc, long Unix, string TimeZone);

/// <summary>A selectable timezone: canonical IANA id + display label with the current UTC offset,
/// e.g. "America/Chicago — UTC-05:00".</summary>
public record TimeZoneOptionDto(string Id, string Label);

/// <summary>Uniform error body every non-2xx JSON response carries.</summary>
public record ErrorResponse(string Error);
