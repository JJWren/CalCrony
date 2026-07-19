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

    /// <summary>Transition an event's mirrored Discord scheduled event to completed after the
    /// event ends (deletes it instead when it never went active).</summary>
    CompleteNativeEvent = 9,

    /// <summary>Give a user the event's attendee role after they RSVP "Going".</summary>
    GrantAttendeeRole = 10,

    /// <summary>Remove the event's attendee role from a user (RSVP switched away, un-RSVPed,
    /// or the event ended / was deleted / cancelled / re-roled).</summary>
    RevokeAttendeeRole = 11,
}

/// <summary>An outbox row the bot must post to Discord. PayloadJson deserializes per <see cref="Type"/>.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Type">The delivery type.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="PayloadJson">The serialized delivery payload.</param>
/// <param name="DueAtUtc">When the delivery becomes due.</param>
public record DeliveryDto(Guid Id, DeliveryType Type, long ChannelId, string PayloadJson, DateTimeOffset DueAtUtc);

/// <summary>Payload for a one-off /remind ping.</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="Text">The reminder text.</param>
public record ReminderPayload(long UserId, string Text);

/// <summary>Payload for the automatic event-start announcement. Guild/native ids are captured at
/// enqueue so the handler can also flip the mirrored scheduled event to active without a refetch.</summary>
/// <param name="EventId">The event id.</param>
/// <param name="Title">The event title.</param>
/// <param name="StartsAtUnix">Start time in Unix seconds.</param>
/// <param name="MessageId">The Discord message id.</param>
/// <param name="GuildId">The Discord guild id, when mirrored.</param>
/// <param name="NativeEventId">The mirrored scheduled-event id, when mirrored.</param>
public record EventStartPayload(
    Guid EventId, string Title, long StartsAtUnix, long? MessageId,
    long? GuildId = null, long? NativeEventId = null);

/// <summary>Payload for a scheduled pre-event notification ping.</summary>
/// <param name="EventId">The event id.</param>
/// <param name="Title">The event title.</param>
/// <param name="StartsAtUnix">Start time in Unix seconds.</param>
/// <param name="Message">Optional message text.</param>
/// <param name="Mentions">Optional mention text included in the ping.</param>
public record EventNotificationPayload(Guid EventId, string Title, long StartsAtUnix, string? Message, string? Mentions);

/// <summary>Payload asking the bot to re-render an event's posted embed after a web-side change.</summary>
/// <param name="EventId">The event id.</param>
public record SyncEventMessagePayload(Guid EventId);

/// <summary>Payload asking the bot to post the embed for an event created without an interaction context.</summary>
/// <param name="EventId">The event id.</param>
public record PostEventMessagePayload(Guid EventId);

/// <summary>Payload asking the bot to delete an event's embed and/or its mirrored scheduled event
/// (ids captured before the row died). MessageId is null when no embed was ever posted.</summary>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id, when an embed exists.</param>
/// <param name="GuildId">The Discord guild id, when mirrored.</param>
/// <param name="NativeEventId">The mirrored scheduled-event id, when mirrored.</param>
public record DeleteEventMessagePayload(
    long ChannelId, long? MessageId, long? GuildId = null, long? NativeEventId = null);

/// <summary>Payload transitioning an event's mirrored scheduled event to completed. Self-contained
/// so the handler works even if the event row is later deleted.</summary>
/// <param name="EventId">The event id.</param>
/// <param name="GuildId">The Discord guild id.</param>
/// <param name="NativeEventId">The mirrored scheduled-event id.</param>
public record CompleteNativeEventPayload(Guid EventId, long GuildId, long NativeEventId);

/// <summary>Payload granting or revoking an event attendee role. Self-contained so revokes survive
/// event deletion, and shared by Grant/RevokeAttendeeRole so identical targets serialize
/// identically — enqueue-time coalescing compares raw PayloadJson.</summary>
/// <param name="EventId">The event id.</param>
/// <param name="GuildId">The Discord guild id.</param>
/// <param name="RoleId">The Discord role id.</param>
/// <param name="UserId">The Discord user id.</param>
public record AttendeeRolePayload(Guid EventId, long GuildId, long RoleId, long UserId);

/// <summary>Payload asking the bot to re-render a poll's posted embed.</summary>
/// <param name="PollId">The poll id.</param>
public record SyncPollMessagePayload(Guid PollId);

/// <summary>Payload asking the bot to post the embed for a web-created poll.</summary>
/// <param name="PollId">The poll id.</param>
public record PostPollMessagePayload(Guid PollId);

/// <summary>Payload asking the bot to delete a poll's embed (ids captured before the row died).</summary>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id.</param>
public record DeletePollMessagePayload(long ChannelId, long MessageId);

/// <summary>Request for a one-off reminder; WhenText is natural language, parsed server-side.</summary>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="UserId">The Discord user id.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="WhenText">Natural-language start time.</param>
/// <param name="Text">The reminder text.</param>
public record CreateReminderRequest(long GuildId, long UserId, long ChannelId, string WhenText, string Text);

/// <summary>A created reminder and when it fires.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="Text">The reminder text.</param>
/// <param name="FireAtUtc">When the reminder fires.</param>
public record ReminderDto(Guid Id, long ChannelId, string Text, DateTimeOffset FireAtUtc)
{
    public long FireAtUnix => FireAtUtc.ToUnixTimeSeconds();
}

/// <summary>Scope is required when the event is the live occurrence of a non-ended series:
/// Series also records the notification on the series template so future occurrences get it.</summary>
/// <param name="MinutesBefore">How many minutes before start the ping fires.</param>
/// <param name="Message">Optional message text.</param>
/// <param name="Mentions">Optional mention text included in the ping.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="Scope">Whether the change applies to this occurrence or the whole series.</param>
public record CreateEventNotificationRequest(
    int MinutesBefore, string? Message = null, string? Mentions = null, long? ChannelId = null,
    EditScope? Scope = null);

/// <summary>A scheduled pre-event notification ping.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="EventId">The event id.</param>
/// <param name="MinutesBefore">How many minutes before start the ping fires.</param>
/// <param name="Message">Optional message text.</param>
/// <param name="Mentions">Optional mention text included in the ping.</param>
/// <param name="ChannelId">The Discord channel id.</param>
public record EventNotificationDto(
    Guid Id, Guid EventId, int MinutesBefore, string? Message, string? Mentions, long? ChannelId);

/// <summary>The guild's ICS feed token; the feed lives at <c>/feeds/{token}.ics</c>.</summary>
/// <param name="Token">The token value.</param>
/// <param name="Path">The relative feed URL path.</param>
public record FeedTokenDto(string Token, string Path);
