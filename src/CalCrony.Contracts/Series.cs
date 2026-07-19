namespace CalCrony.Contracts;

/// <summary>Units a recurrence rule can step in.</summary>
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

/// <summary>A repeat rule: every Interval units, anchored on the first occurrence; MonthlyMode applies when Unit is Month.</summary>
/// <param name="Unit">The recurrence unit.</param>
/// <param name="Interval">Every N units (1-12).</param>
/// <param name="MonthlyMode">The monthly mode.</param>
public record RecurrenceRuleDto(
    RecurrenceUnit Unit,
    int Interval = 1,
    MonthlyMode MonthlyMode = MonthlyMode.DayOfMonth);

/// <summary>A series-template notification spec, cloned onto each materialized occurrence.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="MinutesBefore">How many minutes before start the ping fires.</param>
/// <param name="Message">Optional message text.</param>
/// <param name="Mentions">Optional mention text included in the ping.</param>
/// <param name="ChannelId">The Discord channel id.</param>
public record SeriesNotificationDto(
    Guid Id, int MinutesBefore, string? Message, string? Mentions, long? ChannelId);

/// <summary>A repeating event's schedule, template, and progress. AnchorDate/UntilDate are ISO
/// "yyyy-MM-dd" local dates in TimeZone; StartTime is "HH:mm". Summary is the human-readable
/// rule ("Repeats every 2 weeks on Friday · 3 of 10").</summary>
/// <param name="Id">The unique id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="Title">The event title.</param>
/// <param name="Unit">The recurrence unit.</param>
/// <param name="Interval">Every N units (1-12).</param>
/// <param name="MonthlyMode">The monthly mode.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="AnchorDate">ISO local date of the schedule anchor.</param>
/// <param name="StartTime">The local start time of each occurrence.</param>
/// <param name="UntilDate">Inclusive last allowed local date, when set.</param>
/// <param name="MaxOccurrences">Total allowed occurrences, when count-limited.</param>
/// <param name="OccurrenceCount">Occurrences materialized so far.</param>
/// <param name="Ended">Whether the series has ended.</param>
/// <param name="LiveEventId">The live occurrence's event id, when one exists.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Summary">The human-readable rule text.</param>
/// <param name="NotificationSpecs">The template notification specs.</param>
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
/// <param name="NextEvent">The replacement occurrence, or null when the series ended.</param>
/// <param name="Series">The updated series.</param>
public record SkipOccurrenceResponse(EventDto? NextEvent, SeriesDto Series);

/// <summary>End-condition intent for a series edit. Keep leaves the current end condition
/// untouched; the others replace it (Never clears both UntilDate and MaxOccurrences).</summary>
public enum SeriesEndChoice
{
    Keep = 0,
    Never = 1,
    Until = 2,
    Count = 3,
}

/// <summary>Schedule-rule edit for a series: null rule fields keep current values (note a Month
/// unit without MonthlyMode keeps the stored mode). A successful update always leaves the series
/// active — reviving an ended one — with at least one future occurrence; edits that would leave
/// none are rejected. Never moves the live occurrence's start time.</summary>
/// <param name="Unit">The recurrence unit.</param>
/// <param name="Interval">Every N units (1-12).</param>
/// <param name="MonthlyMode">The monthly mode.</param>
/// <param name="End">The end-condition intent.</param>
/// <param name="RepeatUntilText">Natural-language last repeat date.</param>
/// <param name="RepeatCount">Total occurrences including the first.</param>
public record UpdateSeriesRequest(
    RecurrenceUnit? Unit = null,
    int? Interval = null,
    MonthlyMode? MonthlyMode = null,
    SeriesEndChoice End = SeriesEndChoice.Keep,
    string? RepeatUntilText = null,
    int? RepeatCount = null);
