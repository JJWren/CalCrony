namespace CalCrony.Contracts;

public enum RecurrenceUnit
{
    Day = 0,
    Week = 1,
    Month = 2,
}

/// <summary>How monthly rules pick the day: the anchor's day-of-month (clamped to short months)
/// or the anchor's nth weekday ("3rd Friday"; a 5th weekday clamps to the month's last).</summary>
public enum MonthlyMode
{
    DayOfMonth = 0,
    NthWeekday = 1,
}

/// <summary>Whether a change to a series' live occurrence applies to just that occurrence
/// or to the whole series (template + schedule).</summary>
public enum EditScope
{
    Occurrence = 0,
    Series = 1,
}

public record RecurrenceRuleDto(
    RecurrenceUnit Unit,
    int Interval = 1,
    MonthlyMode MonthlyMode = MonthlyMode.DayOfMonth);

public record SeriesNotificationDto(
    Guid Id, int MinutesBefore, string? Message, string? Mentions, long? ChannelId);

/// <summary>A repeating event's schedule, template, and progress. AnchorDate/UntilDate are ISO
/// "yyyy-MM-dd" local dates in TimeZone; StartTime is "HH:mm". Summary is the human-readable
/// rule ("Repeats every 2 weeks on Friday · 3 of 10").</summary>
public record SeriesDto(
    Guid Id,
    long GuildId,
    long CreatorId,
    string Title,
    RecurrenceUnit Unit,
    int Interval,
    MonthlyMode MonthlyMode,
    string TimeZone,
    string AnchorDate,
    string StartTime,
    string? UntilDate,
    int? MaxOccurrences,
    int OccurrenceCount,
    bool Ended,
    Guid? LiveEventId,
    string? Description,
    int? DurationMinutes,
    long ChannelId,
    string? Location,
    string? ImageUrl,
    string Summary,
    IReadOnlyList<SeriesNotificationDto> NotificationSpecs);

/// <summary>Result of skipping a series' live occurrence. NextEvent is null when the skip
/// exhausted the series' end condition.</summary>
public record SkipOccurrenceResponse(EventDto? NextEvent, SeriesDto Series);
