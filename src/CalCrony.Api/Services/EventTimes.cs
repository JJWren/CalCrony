using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>When an event ends, worked out from its start and its length so that an answer always
/// exists: where the sum would lie past the last moment the calendar can represent, the end is
/// that last moment. Also the API's one place for the length of an event that has none set.</summary>
public static class EventTimes
{
    /// <summary>The length, in minutes, of an event that has no length set.</summary>
    public const int DefaultLengthMinutes = 60;

    /// <summary>The instant an event ends: its start plus its length, or the last instant there is
    /// when that sum lies past it.</summary>
    /// <param name="startsAt">When the event starts.</param>
    /// <param name="durationMinutes">Its length in minutes; null means
    /// <see cref="DefaultLengthMinutes"/>.</param>
    /// <returns>The end instant.</returns>
    public static Instant End(Instant startsAt, int? durationMinutes)
    {
        var length = Duration.FromMinutes(durationMinutes ?? DefaultLengthMinutes);

        // What is left of the calendar after the start. This difference always exists: a Duration
        // spans far more than the whole range of Instant.
        var room = Instant.MaxValue - startsAt;
        return length > room ? Instant.MaxValue : startsAt + length;
    }

    /// <summary>The local date and time an event ends, for output written in a zone's wall time:
    /// its local start plus its length, or the last local date and time there is when that sum
    /// lies past it.</summary>
    /// <param name="startsAt">When the event starts, in local time.</param>
    /// <param name="durationMinutes">Its length in minutes; null means
    /// <see cref="DefaultLengthMinutes"/>.</param>
    /// <returns>The local end.</returns>
    public static LocalDateTime End(LocalDateTime startsAt, int? durationMinutes)
    {
        var minutes = durationMinutes ?? DefaultLengthMinutes;

        // Whole minutes left of the calendar after the start.
        var room = Period.Between(startsAt, LocalDateTime.MaxIsoValue, PeriodUnits.Minutes).Minutes;
        return minutes > room ? LocalDateTime.MaxIsoValue : startsAt.PlusMinutes(minutes);
    }
}
