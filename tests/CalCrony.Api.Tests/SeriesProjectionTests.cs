using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>What a running series goes on to post, asked of the helper directly rather than
/// through a month view.</summary>
public class SeriesProjectionTests
{
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
