using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Data;

/// <summary>Discord server. Snowflake IDs are stored as signed 64-bit throughout.</summary>
public class Guild
{
    public long Id { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public long? DefaultChannelId { get; set; }
}

public class UserProfile
{
    public long Id { get; set; }
    public string? TimeZone { get; set; }
    public bool DmConfirmations { get; set; } = true;
}

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
    public string? Location { get; set; }
    public string? ImageUrl { get; set; }
    public EventStatus Status { get; set; }

    /// <summary>Reserved for recurring events; null for one-off events.</summary>
    public Guid? SeriesId { get; set; }

    public Instant CreatedAt { get; set; }

    public List<RsvpOption> Options { get; set; } = [];
    public List<Rsvp> Rsvps { get; set; } = [];
    public List<EventNotification> Notifications { get; set; } = [];
}

public class RsvpOption
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string Emote { get; set; }
    public required string Label { get; set; }
    public int SortOrder { get; set; }
    public int? Capacity { get; set; }
}

public class Rsvp
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
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
}

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
