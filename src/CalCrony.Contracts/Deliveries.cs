namespace CalCrony.Contracts;

/// <summary>Outbox row kinds the bot poller knows how to post; each maps to a payload record below.</summary>
public enum DeliveryType
{
    Reminder = 0,
    EventNotification = 1,
    EventStart = 2,

    /// <summary>Re-render an event's posted Discord embed (a web action changed its data).</summary>
    SyncEventMessage = 3,

    /// <summary>Post the embed for a web-created event to the guild's default channel.</summary>
    PostEventMessage = 4,

    /// <summary>Delete a web-deleted event's posted embed (ids captured before the row died).</summary>
    DeleteEventMessage = 5,

    /// <summary>Re-render a poll's posted Discord embed (votes/options/status changed).</summary>
    SyncPollMessage = 6,

    /// <summary>Post the embed for a web-created poll.</summary>
    PostPollMessage = 7,

    /// <summary>Delete a web-deleted poll's posted embed (ids captured before the row died).</summary>
    DeletePollMessage = 8,
}

/// <summary>An outbox row the bot must post to Discord. PayloadJson deserializes per <see cref="Type"/>.</summary>
public record DeliveryDto(Guid Id, DeliveryType Type, long ChannelId, string PayloadJson, DateTimeOffset DueAtUtc);

/// <summary>Payload for a one-off /remind ping.</summary>
public record ReminderPayload(long UserId, string Text);

/// <summary>Payload for the automatic event-start announcement.</summary>
public record EventStartPayload(Guid EventId, string Title, long StartsAtUnix, long? MessageId);

/// <summary>Payload for a scheduled pre-event notification ping.</summary>
public record EventNotificationPayload(Guid EventId, string Title, long StartsAtUnix, string? Message, string? Mentions);

/// <summary>Payload asking the bot to re-render an event's posted embed after a web-side change.</summary>
public record SyncEventMessagePayload(Guid EventId);

/// <summary>Payload asking the bot to post the embed for an event created without an interaction context.</summary>
public record PostEventMessagePayload(Guid EventId);

/// <summary>Payload asking the bot to delete an event's embed (ids captured before the row died).</summary>
public record DeleteEventMessagePayload(long ChannelId, long MessageId);

/// <summary>Payload asking the bot to re-render a poll's posted embed.</summary>
public record SyncPollMessagePayload(Guid PollId);

/// <summary>Payload asking the bot to post the embed for a web-created poll.</summary>
public record PostPollMessagePayload(Guid PollId);

/// <summary>Payload asking the bot to delete a poll's embed (ids captured before the row died).</summary>
public record DeletePollMessagePayload(long ChannelId, long MessageId);

/// <summary>Request for a one-off reminder; WhenText is natural language, parsed server-side.</summary>
public record CreateReminderRequest(long GuildId, long UserId, long ChannelId, string WhenText, string Text);

/// <summary>A created reminder and when it fires.</summary>
public record ReminderDto(Guid Id, long ChannelId, string Text, DateTimeOffset FireAtUtc)
{
    public long FireAtUnix => FireAtUtc.ToUnixTimeSeconds();
}

/// <summary>Scope is required when the event is the live occurrence of a non-ended series:
/// Series also records the notification on the series template so future occurrences get it.</summary>
public record CreateEventNotificationRequest(
    int MinutesBefore, string? Message = null, string? Mentions = null, long? ChannelId = null,
    EditScope? Scope = null);

/// <summary>A scheduled pre-event notification ping.</summary>
public record EventNotificationDto(
    Guid Id, Guid EventId, int MinutesBefore, string? Message, string? Mentions, long? ChannelId);

/// <summary>The guild's ICS feed token; the feed lives at <c>/feeds/{token}.ics</c>.</summary>
public record FeedTokenDto(string Token, string Path);
