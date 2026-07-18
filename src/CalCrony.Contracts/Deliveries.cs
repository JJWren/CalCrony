namespace CalCrony.Contracts;

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
}

/// <summary>An outbox row the bot must post to Discord. PayloadJson deserializes per <see cref="Type"/>.</summary>
public record DeliveryDto(Guid Id, DeliveryType Type, long ChannelId, string PayloadJson, DateTimeOffset DueAtUtc);

public record ReminderPayload(long UserId, string Text);

public record EventStartPayload(Guid EventId, string Title, long StartsAtUnix, long? MessageId);

public record EventNotificationPayload(Guid EventId, string Title, long StartsAtUnix, string? Message, string? Mentions);

public record SyncEventMessagePayload(Guid EventId);

public record PostEventMessagePayload(Guid EventId);

public record DeleteEventMessagePayload(long ChannelId, long MessageId);

public record CreateReminderRequest(long GuildId, long UserId, long ChannelId, string WhenText, string Text);

public record ReminderDto(Guid Id, long ChannelId, string Text, DateTimeOffset FireAtUtc)
{
    public long FireAtUnix => FireAtUtc.ToUnixTimeSeconds();
}

public record CreateEventNotificationRequest(
    int MinutesBefore, string? Message = null, string? Mentions = null, long? ChannelId = null);

public record EventNotificationDto(
    Guid Id, Guid EventId, int MinutesBefore, string? Message, string? Mentions, long? ChannelId);

/// <summary>The guild's ICS feed token; the feed lives at <c>/feeds/{token}.ics</c>.</summary>
public record FeedTokenDto(string Token, string Path);
