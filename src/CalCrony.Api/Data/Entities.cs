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

    /// <summary>Whether the bot is currently in the guild. Maintained by the bot's join/leave
    /// events and its Ready-time sync; rows are kept when the bot leaves so guild settings and
    /// data survive a re-invite.</summary>
    public bool BotPresent { get; set; } = true;

    /// <summary>Name snapshot maintained by the bot (presence reports, Ready-time sync, and
    /// guild-update events) — the API never asks Discord. Null until the bot has reported one;
    /// consumers must degrade gracefully.</summary>
    public string? Name { get; set; }
}

/// <summary>Name snapshot for a Discord channel CalCrony references (an event, a series, or a
/// guild default channel). Rows are created only at reference points — the bot's embed post
/// sites and its Ready-time reconcile — so the table never grows beyond channels CalCrony
/// actually uses; renames update existing rows only.</summary>
public class Channel
{
    public long Id { get; set; }
    public long GuildId { get; set; }
    public required string Name { get; set; }
}

/// <summary>A Discord user's per-person preferences plus display fields captured at web login.</summary>
public class UserProfile
{
    public long Id { get; set; }
    public string? TimeZone { get; set; }
    public bool DmConfirmations { get; set; } = true;

    /// <summary>Web interface theme (a value from <see cref="InterfaceThemes.All"/>); null = the
    /// user never picked one and the web app uses its default. The dark/light face is a per-device
    /// choice and deliberately not stored.</summary>
    public string? Theme { get; set; }

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

    /// <summary>Existing Discord role granted to attending RSVPs and revoked at event end; null = feature off.</summary>
    public long? AttendeeRoleId { get; set; }

    /// <summary>Opt-in: open a discussion thread on the posted embed message.</summary>
    public bool WantsThread { get; set; }

    /// <summary>The Discord thread-channel id once the bot created the thread; null until then.</summary>
    public long? ThreadId { get; set; }

    /// <summary>Relative RSVP cutoff: minutes before start after which RSVPs reject changes.
    /// Tracks start-time edits automatically. Mutually exclusive with RsvpClosesAt.</summary>
    public int? RsvpCloseMinutesBefore { get; set; }

    /// <summary>Absolute RSVP cutoff instant. Mutually exclusive with RsvpCloseMinutesBefore.</summary>
    public Instant? RsvpClosesAt { get; set; }

    /// <summary>One-shot flag: the scheduler re-rendered the embed in its closed state (buttons
    /// disabled) after the cutoff passed. Reset when an edit moves the cutoff or the start.</summary>
    public bool RsvpCloseSynced { get; set; }

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
/// Schedule math is anchor-based (never chained), so monthly/yearly clamping can't drift.
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

    /// <summary>Weekly day set (bit flags), meaningful only when Unit == Week. None = the
    /// anchor's weekday only — the behaviour every pre-day-set series has, so the migration
    /// backfills 0 and nothing changes for existing rows.</summary>
    public RecurrenceDays DaysOfWeek { get; set; }

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

    /// <summary>Template field copied to spawned occurrences, like Title/Description.</summary>
    public long? AttendeeRoleId { get; set; }

    /// <summary>Template field: each spawned occurrence opens its own discussion thread.</summary>
    public bool WantsThread { get; set; }

    /// <summary>Template field: relative RSVP cutoff copied to spawned occurrences. Absolute
    /// cutoffs are occurrence-only (a fixed instant makes no sense across a schedule).</summary>
    public int? RsvpCloseMinutesBefore { get; set; }

    /// <summary>Template field: the RSVP option set (serialized RsvpOptionSpec list) spawned
    /// occurrences start with; null = the default Going/Not going/Maybe set. Written at create
    /// and by Series-scoped option/limit edits only — Occurrence-scoped option edits diverge and
    /// the next spawn reverts to this template, matching every other template field.</summary>
    public string? RsvpOptionsJson { get; set; }

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
/// are unique per guild case-insensitively (functional unique index on GuildId + lower(Name)).</summary>
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

    /// <summary>Weekly day set captured with the rule; null/None = the anchor's weekday only.</summary>
    public RecurrenceDays? RecurrenceDaysOfWeek { get; set; }

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

    /// <summary>Marks the option whose RSVPs count as attending — the flag that drives attendee
    /// roles, threads, availability, counts, and the waitlist. Exactly one per event.</summary>
    public bool IsAttending { get; set; }
}

/// <summary>A user's RSVP to one event (unique per user per event). CreatedAt doubles as the
/// waitlist queue position, so it only moves when the user changes option.</summary>
public class Rsvp
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public long UserId { get; set; }
    public Guid OptionId { get; set; }

    /// <summary>True while queued past the attending option's capacity: no seat, no role, no
    /// thread membership until promoted (in CreatedAt order) by a freed or raised capacity.</summary>
    public bool Waitlisted { get; set; }

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

/// <summary>A persistent upcoming-events embed the bot keeps current in one channel. One per
/// channel (unique index on ChannelId); removed via /livelist remove, or cleared by the bot when
/// it finds the message manually deleted — never reposted (no resurrect loop). Content changes
/// reach the bot as debounced SyncLiveList deliveries (see LiveListSync).</summary>
public class LiveList
{
    public Guid Id { get; set; }
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public long MessageId { get; set; }

    /// <summary>Maximum number of events the list shows (1-25).</summary>
    public int Limit { get; set; }

    public long CreatorId { get; set; }
    public Instant CreatedAt { get; set; }
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
