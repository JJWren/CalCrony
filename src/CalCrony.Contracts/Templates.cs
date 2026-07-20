namespace CalCrony.Contracts;

/// <summary>One notification spec carried by a template (no id — specs are edited as a whole
/// set, never individually).</summary>
/// <param name="MinutesBefore">How many minutes before start the ping fires.</param>
/// <param name="Message">Optional message text.</param>
/// <param name="Mentions">Optional mention text included in the ping.</param>
/// <param name="ChannelId">Target channel; null means the event's channel.</param>
public record TemplateNotificationDto(int MinutesBefore, string? Message, string? Mentions, long? ChannelId);

/// <summary>A reusable event template: content + notification specs + an optional repeat rule.
/// Fully denormalized from the source event at save time — deleting the source changes nothing.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="CreatorId">The saving user's Discord id.</param>
/// <param name="Name">The template name (unique per guild, case-insensitive).</param>
/// <param name="Title">The event title.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Recurrence">The repeat rule, when the source was a running series.</param>
/// <param name="Notifications">The captured notification specs.</param>
/// <param name="CreatedAtUtc">When the template was saved.</param>
public record EventTemplateDto(
    Guid Id,
    long GuildId,
    long CreatorId,
    string Name,
    string Title,
    string? Description,
    int? DurationMinutes,
    string? Location,
    string? ImageUrl,
    RecurrenceRuleDto? Recurrence,
    IReadOnlyList<TemplateNotificationDto> Notifications,
    DateTimeOffset CreatedAtUtc);

/// <summary>Saves a template captured from an existing event's current content, notifications,
/// and (when it is the live occurrence of a non-ended series) its repeat rule.</summary>
/// <param name="CreatorId">The saving user's Discord id (ignored for web callers).</param>
/// <param name="Name">The template name (1-64 chars, unique per guild).</param>
/// <param name="EventId">The event to capture.</param>
public record SaveTemplateRequest(long CreatorId, string Name, Guid EventId);

/// <summary>Partial template update (creator or manager); null fields are left unchanged.
/// Editing a template never touches events already created from it — templates stay fully
/// denormalized in both directions.</summary>
/// <param name="EditorId">The editing user's Discord id (ignored for web callers).</param>
/// <param name="Name">The template name (1-64 chars, unique per guild).</param>
/// <param name="Title">The event title.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Recurrence">Replaces the repeat rule. Null leaves it unchanged — clear with
/// ClearRecurrence.</param>
/// <param name="ClearRecurrence">Removes the repeat rule. Conflicts with Recurrence.</param>
/// <param name="Notifications">Non-null replaces the whole notification-spec set (max 5).</param>
public record UpdateTemplateRequest(
    long EditorId,
    string? Name = null,
    string? Title = null,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null,
    RecurrenceRuleDto? Recurrence = null,
    bool ClearRecurrence = false,
    IReadOnlyList<TemplateNotificationDto>? Notifications = null);
