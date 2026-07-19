using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Data;

/// <summary>Discord server. Snowflake IDs are stored as signed 64-bit throughout.</summary>
public class Guild
{
    public long Id { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public long? DefaultChannelId { get; set; }

    /// <summary>Opt-in: mirror events into Discord's native scheduled events. Gates creation only —
    /// events that already have a native twin keep syncing regardless.</summary>
    public bool MirrorNativeEvents { get; set; }
}

/// <summary>A Discord user's per-person preferences plus display fields captured at web login.</summary>
public class UserProfile
{
    public long Id { get; set; }
    public string? TimeZone { get; set; }
    public bool DmConfirmations { get; set; } = true;

    /// <summary>Display name captured at web login (global name, falling back to username);
    /// null until the user has signed in to the web app at least once.</summary>
    public string? Username { get; set; }

    public string? AvatarHash { get; set; }
}

/// <summary>A scheduled happening in a guild: one row per occurrence, linked to a series when recurring.</summary>
public class Event
{
    public Guid Id { get; set; }
    public long GuildId { get; set; }
    public long CreatorId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public Instant StartsAt { get; set; }

    /// <summary>IANA zone the event was created in; used for display and future recurrence math.</summary>
    public string TimeZone { get; set; } = "UTC";

    public int? DurationMinutes { get; set; }
    public long ChannelId { get; set; }
    public long? MessageId { get; set; }

    /// <summary>Discord Guild Scheduled Event id when mirrored; null when never mirrored.</summary>
    public long? NativeEventId { get; set; }

    public string? Location { get; set; }
    public string? ImageUrl { get; set; }
    public EventStatus Status { get; set; }

    /// <summary>Links occurrences of a recurring event to their series; null for one-off events.</summary>
    public Guid? SeriesId { get; set; }

    public Instant CreatedAt { get; set; }

    public EventSeries? Series { get; set; }
    public List<RsvpOption> Options { get; set; } = [];
    public List<Rsvp> Rsvps { get; set; } = [];
    public List<EventNotification> Notifications { get; set; } = [];
}

/// <summary>A repeating event's schedule + content template. Exactly one live (Scheduled/Started)
/// occurrence exists per non-ended series, enforced by the partial unique index
/// IX_Events_SeriesId_Live; the scheduler materializes the next occurrence when the slot frees.
/// Schedule math is anchor-based (never chained), so monthly clamping can't drift.
/// Series rows are never deleted — an ended series stays as history.</summary>
public class EventSeries
{
    public Guid Id { get; set; }
    public long GuildId { get; set; }
    public long CreatorId { get; set; }
    public RecurrenceUnit Unit { get; set; }

    /// <summary>Every N units, 1..12.</summary>
    public int Interval { get; set; }

    /// <summary>Meaningful only when Unit == Month.</summary>
    public MonthlyMode MonthlyMode { get; set; }

    /// <summary>Local date of the first occurrence; re-set by whole-series time edits.</summary>
    public LocalDate AnchorDate { get; set; }

    public LocalTime StartTime { get; set; }

    /// <summary>IANA zone all schedule math resolves in (8pm stays 8pm across DST).</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Inclusive last local date the series may occur on; mutually exclusive with MaxOccurrences.</summary>
    public LocalDate? UntilDate { get; set; }

    public int? MaxOccurrences { get; set; }

    /// <summary>Slot cursor: the schedule-slot date of the last-materialized occurrence. Only ever
    /// advances, which makes same-slot re-spawn impossible and keeps one-off time edits schedule-neutral.</summary>
    public LocalDate CurrentOccurrenceDate { get; set; }

    /// <summary>Occurrences actually materialized (the first counts as 1; downtime-missed slots don't).</summary>
    public int OccurrenceCount { get; set; }

    public bool Ended { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }
    public int? DurationMinutes { get; set; }
    public long ChannelId { get; set; }
    public string? Location { get; set; }
    public string? ImageUrl { get; set; }
    public Instant CreatedAt { get; set; }

    public List<SeriesNotification> NotificationSpecs { get; set; } = [];
}

/// <summary>Template notification cloned onto each materialized occurrence.</summary>
public class SeriesNotification
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public int MinutesBefore { get; set; }
    public string? Message { get; set; }
    public string? Mentions { get; set; }
    public long? ChannelId { get; set; }
}

/// <summary>A reusable event shape saved from an existing event: content + notification specs +
/// an optional repeat rule. Fully denormalized — the source event can be deleted freely. Names
/// are unique per guild (the API rejects case-insensitive duplicates; the index is exact-case).</summary>
public class EventTemplate
{
    public Guid Id { get; set; }
    public long GuildId { get; set; }
    public long CreatorId { get; set; }
    public required string Name { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public int? DurationMinutes { get; set; }
    public string? Location { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>Null = no repeat rule; the interval/mode fields are meaningful only when set.</summary>
    public RecurrenceUnit? RecurrenceUnit { get; set; }

    public int? RecurrenceInterval { get; set; }
    public MonthlyMode? RecurrenceMonthlyMode { get; set; }
    public Instant CreatedAt { get; set; }
    public List<EventTemplateNotification> Notifications { get; set; } = [];
}

/// <summary>One notification spec carried by a template, applied to events created from it.</summary>
public class EventTemplateNotification
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public int MinutesBefore { get; set; }
    public string? Message { get; set; }
    public string? Mentions { get; set; }
    public long? ChannelId { get; set; }
}

/// <summary>One RSVP choice on an event (emote + label, optional capacity).</summary>
public class RsvpOption
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string Emote { get; set; }
    public required string Label { get; set; }
    public int SortOrder { get; set; }
    public int? Capacity { get; set; }
}

/// <summary>A user's RSVP to one event (unique per user per event).</summary>
public class Rsvp
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public long UserId { get; set; }
    public Guid OptionId { get; set; }
    public Instant CreatedAt { get; set; }
}

/// <summary>A poll: standard (free-text options) or time poll (options are candidate slots).</summary>
public class Poll
{
    public Guid Id { get; set; }
    public long GuildId { get; set; }
    public long CreatorId { get; set; }
    public required string Question { get; set; }
    public bool IsTimePoll { get; set; }
    public bool SingleVote { get; set; }
    public bool Anonymous { get; set; }
    public bool AllowUserOptions { get; set; }
    public long ChannelId { get; set; }
    public long? MessageId { get; set; }
    public Contracts.PollStatus Status { get; set; }
    public Instant? ClosesAt { get; set; }
    public Instant? ClosedAt { get; set; }

    /// <summary>Creator's zone at creation; later-added time slots parse in it (mirrors Event.TimeZone).</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Set once when a time poll's winner becomes an event — the convert-idempotency guard.</summary>
    public Guid? ConvertedEventId { get; set; }

    public Instant CreatedAt { get; set; }
    public List<PollOption> Options { get; set; } = [];
    public List<PollVote> Votes { get; set; } = [];
}

/// <summary>One poll choice; SlotAt set for time polls, AddedByUserId for voter-added options.</summary>
public class PollOption
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public required string Text { get; set; }

    /// <summary>Time polls only: the resolved slot this option represents.</summary>
    public Instant? SlotAt { get; set; }

    /// <summary>Null when supplied at creation; set for voter-added options.</summary>
    public long? AddedByUserId { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>Row-per-option: multi-vote polls have several rows per user; single-vote is
/// enforced in handler logic, not schema.</summary>
public class PollVote
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public long UserId { get; set; }
    public Guid OptionId { get; set; }
    public Instant CreatedAt { get; set; }
}

/// <summary>A scheduled ping relative to an event's start; fire time is recomputed from the
/// event's current StartsAt each scheduler sweep, so edits to the event shift pings automatically.</summary>
public class EventNotification
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public int MinutesBefore { get; set; }
    public string? Message { get; set; }
    public string? Mentions { get; set; }

    /// <summary>Target channel; null means the event's channel.</summary>
    public long? ChannelId { get; set; }

    public bool Enqueued { get; set; }

    /// <summary>Lineage to the series template spec this was cloned from; null for one-off events
    /// and for notifications added with Occurrence scope (diverged rows).</summary>
    public Guid? SeriesNotificationId { get; set; }
}

/// <summary>Outbox row lifecycle: pending until the bot acks, failed after repeated attempts.</summary>
public enum DeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
}

/// <summary>Unguessable token embedded in a guild's public ICS feed URL.</summary>
public class IcsFeedToken
{
    public Guid Id { get; set; }
    public long GuildId { get; set; }
    public required string Token { get; set; }
    public Instant CreatedAt { get; set; }
}

/// <summary>Outbox row. The bot polls pending due rows, posts to Discord, and acks.</summary>
public class Delivery
{
    public Guid Id { get; set; }
    public Contracts.DeliveryType Type { get; set; }
    public long ChannelId { get; set; }
    public required string PayloadJson { get; set; }
    public Instant DueAt { get; set; }
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public Instant CreatedAt { get; set; }
}

/// <summary>A Discord user's linked external calendar. Tokens are Data-Protection-encrypted at rest;
/// raw tokens never leave CalCrony.Api (never exposed via CalCrony.Contracts).</summary>
public class CalendarConnection
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public CalendarProvider Provider { get; set; }
    public required string EncryptedAccessToken { get; set; }
    public required string EncryptedRefreshToken { get; set; }
    public Instant AccessTokenExpiresAt { get; set; }
    public Instant ConnectedAt { get; set; }
    public Instant? LastRefreshedAt { get; set; }
}

/// <summary>Short-lived, single-use token binding a Discord user to one in-flight OAuth linking
/// attempt; also serves as the OAuth `state` value (see OAuthEndpoints) since there is no browser
/// session to bind CSRF protection to.</summary>
public class CalendarLinkToken
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public CalendarProvider Provider { get; set; }
    public required string Token { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant ExpiresAt { get; set; }
    public Instant? ConsumedAt { get; set; }
}

/// <summary>Single-use CSRF state for an in-flight Discord web login. Unlike CalendarLinkToken
/// there is no UserId — identity is unknown until Discord's callback.</summary>
public class WebLoginState
{
    public Guid Id { get; set; }
    public required string Token { get; set; }
    public string? ReturnUrl { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant ExpiresAt { get; set; }
    public Instant? ConsumedAt { get; set; }
}

/// <summary>Rotate-on-use web session refresh token; only the SHA-256 hash is stored. The raw
/// value lives in the browser's HttpOnly cookie.</summary>
public class WebRefreshToken
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public required string TokenHash { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant ExpiresAt { get; set; }
    public Instant? RevokedAt { get; set; }
}

/// <summary>Login-time snapshot of one Discord guild a web user belongs to. Stores ALL the
/// user's guilds; the bot-present intersection (join against Guilds) happens at query time so a
/// guild that adds the bot later appears without re-login. Refreshed wholesale on each login.</summary>
public class UserGuildMembership
{
    public long UserId { get; set; }
    public long GuildId { get; set; }
    public required string GuildName { get; set; }
    public string? IconHash { get; set; }

    /// <summary>User has ManageGuild permission (or owns the guild) — drives admin parity.</summary>
    public bool CanManage { get; set; }

    public Instant SnapshotAt { get; set; }
}
