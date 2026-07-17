namespace CalCrony.Contracts;

public enum EventStatus
{
    Scheduled = 0,
    Started = 1,
    Ended = 2,
    Cancelled = 3,
}

/// <summary>Request to create an event. Datetimes arrive as natural-language text and are parsed server-side.</summary>
public record CreateEventRequest(
    long CreatorId,
    string Title,
    string WhenText,
    long ChannelId,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null);

/// <summary>Partial update; null fields are left unchanged.</summary>
public record UpdateEventRequest(
    long EditorId,
    string? Title = null,
    string? WhenText = null,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null,
    EventStatus? Status = null);

public record RsvpOptionDto(Guid Id, string Emote, string Label, int SortOrder, int? Capacity);

public record RsvpDto(long UserId, Guid OptionId);

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
    IReadOnlyList<RsvpDto> Rsvps)
{
    /// <summary>Unix seconds of the start time, for Discord &lt;t:...&gt; timestamps.</summary>
    public long StartsAtUnix => StartsAtUtc.ToUnixTimeSeconds();
}

public record SetEventMessageRequest(long ChannelId, long MessageId);

public record RsvpRequest(Guid OptionId);

public record GuildSettingsDto(string TimeZone, long? DefaultChannelId);

public record UserSettingsDto(string? TimeZone, bool DmConfirmations);

public record ParseDateTimeRequest(string Text, long? UserId = null, long? GuildId = null);

public record ParseDateTimeResponse(DateTimeOffset Utc, long Unix, string TimeZone);

public record ErrorResponse(string Error);
