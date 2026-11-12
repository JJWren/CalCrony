using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>A member's available hours: at most one range per weekday, as minutes from midnight on
/// the member's own wall clock. An end of 1440 is the midnight that closes the day, so a range can
/// run to the end of a day but never past it. A weekday without a range is not available at all.
/// Immutable.</summary>
public sealed class WeeklyHours
{
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
}
