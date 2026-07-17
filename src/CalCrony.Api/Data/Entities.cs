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
