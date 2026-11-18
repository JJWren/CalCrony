using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>When an occurrence ends and what a running series goes on to post, asked of the helper
/// directly rather than through a month view.</summary>
public class SeriesProjectionTests
{
    [Theory]
    [InlineData(null, 2, 19, 0)]
    [InlineData(1, 2, 18, 1)]
    [InlineData(90, 2, 19, 30)]
    [InlineData(40320, 30, 18, 0)]
    public void EndOf_is_where_EventTimes_says_an_event_ends(
        int? durationMinutes, int day, int hour, int minute)
    {
        // An occurrence that starts at 18:00 UTC on Monday 2 November 2026. With no length set it
        // runs an hour (EventTimes.DefaultLengthMinutes); 40,320 minutes, the longest an event
        // may last, is four weeks.
        var start = Instant.FromUtc(2026, 11, 2, 18, 0);

        var end = SeriesProjection.EndOf(start, durationMinutes);

        Assert.Equal(Instant.FromUtc(2026, 11, day, hour, minute), end);
        Assert.Equal(EventTimes.End(start, durationMinutes), end);
    }

    [Fact]
    public void Upcoming_lists_no_more_occurrences_than_the_steps_it_is_given()
    {
        // Daily at 18:00 UTC with no until date and no count, so only the limit ends the list.
        var series = new EventSeries
        {
            Id = Guid.NewGuid(),
            Unit = RecurrenceUnit.Day,
            Interval = 1,
            AnchorDate = new LocalDate(2026, 11, 2),
            StartTime = new LocalTime(18, 0),
            TimeZone = "UTC",
            CurrentOccurrenceDate = new LocalDate(2026, 11, 2),
            OccurrenceCount = 1,
            Title = "Class",
        };
        var freeFrom = Instant.FromUtc(2026, 11, 2, 19, 0);

        Assert.Equal(
            [Instant.FromUtc(2026, 11, 3, 18, 0), Instant.FromUtc(2026, 11, 4, 18, 0), Instant.FromUtc(2026, 11, 5, 18, 0)],
            SeriesProjection.Upcoming(series, freeFrom, maxSteps: 3));
        Assert.Empty(SeriesProjection.Upcoming(series, freeFrom, maxSteps: 0));
    }
}
