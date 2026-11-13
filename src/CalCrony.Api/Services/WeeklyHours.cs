using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalCrony.Contracts;
using NodaTime;
using NodaTime.Utility;

namespace CalCrony.Api.Services;

/// <summary>A member's available hours: at most one range per weekday, as minutes from midnight on
/// the member's own wall clock. An end of 1440 is the midnight that closes the day, so a range can
/// run to the end of a day but never past it. A weekday without a range is not available at all.
/// Immutable.</summary>
public sealed class WeeklyHours
{
    /// <summary>The most characters a stored week takes: the column's width. The longest week —
    /// seven days, each with two four-digit times — serializes to 239.</summary>
    public const int MaxStoredLength = 512;

    private const int DaysInWeek = 7;

    private readonly Dictionary<IsoDayOfWeek, (int StartMinute, int EndMinute)> ranges;

    /// <summary>Builds a week from its ranges. Only what makes a range meaningful is checked here —
    /// it lies inside one day and starts before it ends; what a member may enter (the step between
    /// selectable times) is the input surface's concern.</summary>
    /// <param name="ranges">The range of each available weekday; a weekday left out is unavailable.</param>
    /// <exception cref="ArgumentException">A range names no weekday, reaches outside 0–1440, or
    /// does not start before it ends.</exception>
    public WeeklyHours(IReadOnlyDictionary<IsoDayOfWeek, (int StartMinute, int EndMinute)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        foreach (var (day, (startMinute, endMinute)) in ranges)
        {
            if (day is < IsoDayOfWeek.Monday or > IsoDayOfWeek.Sunday)
            {
                throw new ArgumentException($"{day} is not a weekday.", nameof(ranges));
            }

            if (startMinute < 0 || endMinute > AvailableHoursLimits.MinutesPerDay || startMinute >= endMinute)
            {
                throw new ArgumentException(
                    $"{day}: {startMinute}–{endMinute} is not a range inside one day (0–{AvailableHoursLimits.MinutesPerDay}, start before end).",
                    nameof(ranges));
            }
        }

        this.ranges = new Dictionary<IsoDayOfWeek, (int StartMinute, int EndMinute)>(ranges);
    }

    /// <summary>The hours applied to a member who has saved none: the same range every day, from
    /// <see cref="AvailableHoursLimits"/>.</summary>
    public static WeeklyHours Default { get; } = new(
        Enumerable.Range((int)IsoDayOfWeek.Monday, 7).ToDictionary(
            day => (IsoDayOfWeek)day,
            _ => (AvailableHoursLimits.DefaultStartMinute, AvailableHoursLimits.DefaultEndMinute)));

    /// <summary>The range for a weekday, or null when the member is not available that day.</summary>
    /// <param name="day">The weekday.</param>
    /// <returns>Start and end as minutes from midnight, or null.</returns>
    public (int StartMinute, int EndMinute)? RangeFor(IsoDayOfWeek day) =>
        ranges.TryGetValue(day, out var range) ? range : null;

    /// <summary>Checks a week a member submitted and builds it. On top of what makes a range
    /// meaningful, this applies what a member may enter: at least one day, each weekday once, and
    /// times on the <see cref="AvailableHoursLimits.StepMinutes"/> step. The first failing check,
    /// entry by entry, gives the message.</summary>
    /// <param name="days">The submitted days; null is read as none.</param>
    /// <param name="hours">The week, when every check passed.</param>
    /// <param name="error">The user-facing reason, when one did not.</param>
    /// <returns>Whether the week can be saved.</returns>
    public static bool TryFromDto(
        IReadOnlyList<AvailableHoursDayDto>? days,
        [NotNullWhen(true)] out WeeklyHours? hours,
        [NotNullWhen(false)] out string? error)
    {
        hours = null;
        if (days is null || days.Count == 0)
        {
            // A week with no day would mean "never available"; the default hours are what a member
            // goes back to by resetting, not by saving nothing.
            error = "Pick at least one day, or reset to go back to the default hours.";
            return false;
        }

        if (days.Count > DaysInWeek)
        {
            error = "A week has seven days.";
            return false;
        }

        var submitted = new Dictionary<IsoDayOfWeek, (int StartMinute, int EndMinute)>();
        foreach (var day in days)
        {
            if (day is null || !Enum.IsDefined(day.Day))
            {
                // A weekday crosses the wire as its number, so that is what is quoted back.
                var sent = day is null ? "null" : ((int)day.Day).ToString(CultureInfo.InvariantCulture);
                error = $"{sent} is not a day of the week.";
                return false;
            }

            var weekday = BclConversions.ToIsoDayOfWeek(day.Day);
            if (submitted.ContainsKey(weekday))
            {
                error = $"{day.Day} is listed twice.";
                return false;
            }

            if (day.StartMinute is < 0 or > AvailableHoursLimits.MinutesPerDay
                || day.EndMinute is < 0 or > AvailableHoursLimits.MinutesPerDay)
            {
                error = $"{day.Day}: a time is 0 to {AvailableHoursLimits.MinutesPerDay} minutes after midnight.";
                return false;
            }

            if (day.StartMinute % AvailableHoursLimits.StepMinutes != 0 || day.EndMinute % AvailableHoursLimits.StepMinutes != 0)
            {
                error = $"{day.Day}: times are in {AvailableHoursLimits.StepMinutes}-minute steps.";
                return false;
            }

            if (day.StartMinute >= day.EndMinute)
            {
                error = $"{day.Day}: the start has to be before the end.";
                return false;
            }

            submitted[weekday] = (day.StartMinute, day.EndMinute);
        }

        hours = new WeeklyHours(submitted);
        error = null;
        return true;
    }

    /// <summary>Reads a stored week. Anything that is not one — text that is not the stored form,
    /// an unknown weekday, a weekday twice, a range outside one day or not start-before-end, or no
    /// day at all — reads as "nothing saved": this never throws, so one bad row cannot fail a
    /// lookup. More than seven entries need no check of their own: an eighth has to repeat a
    /// weekday or name none. The step between times is not checked here; it is a rule for what
    /// may be entered, and a stored week stays readable if that rule changes — though saving it
    /// again, even for one day, applies the rule then in force to every day in it.</summary>
    /// <param name="json">The stored text, or null when nothing is stored.</param>
    /// <returns>The week, or null when nothing readable is stored.</returns>
    public static WeeklyHours? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        List<StoredDay?>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<List<StoredDay?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (stored is null || stored.Count == 0)
        {
            return null;
        }

        var read = new Dictionary<IsoDayOfWeek, (int StartMinute, int EndMinute)>();
        foreach (var day in stored)
        {
            if (day is null
                || day.Day is < (int)IsoDayOfWeek.Monday or > (int)IsoDayOfWeek.Sunday
                || day.Start < 0
                || day.End > AvailableHoursLimits.MinutesPerDay
                || day.Start >= day.End
                || !read.TryAdd((IsoDayOfWeek)day.Day, (day.Start, day.End)))
            {
                return null;
            }
        }

        return new WeeklyHours(read);
    }

    /// <summary>The week as it is stored: its available days in order, Monday first, each as
    /// <c>{"day":1,"start":480,"end":1380}</c> with the ISO weekday number. The names are fixed
    /// here, apart from any DTO, so renaming a wire property cannot change what is on disk.</summary>
    /// <returns>The stored text.</returns>
    public string Serialize() => JsonSerializer.Serialize(
        ranges.OrderBy(range => range.Key)
            .Select(range => new StoredDay((int)range.Key, range.Value.StartMinute, range.Value.EndMinute)));

    /// <summary>The week on the wire: its available days in order, Monday first. A weekday
    /// without a range is left out.</summary>
    /// <returns>One entry per available weekday.</returns>
    public IReadOnlyList<AvailableHoursDayDto> ToDto() =>
        [.. ranges.OrderBy(range => range.Key)
            .Select(range => new AvailableHoursDayDto(
                BclConversions.ToDayOfWeek(range.Key), range.Value.StartMinute, range.Value.EndMinute))];

    /// <summary>One stored weekday.</summary>
    /// <param name="Day">The ISO weekday number, Monday 1 to Sunday 7.</param>
    /// <param name="Start">Start, in minutes from midnight.</param>
    /// <param name="End">End, in minutes from midnight.</param>
    private sealed record StoredDay(
        [property: JsonPropertyName("day")] int Day,
        [property: JsonPropertyName("start")] int Start,
        [property: JsonPropertyName("end")] int End);
}
