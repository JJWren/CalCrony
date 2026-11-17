using CalCrony.Api.Services;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>When an event ends: its start plus its length, in both forms the API writes it — an
/// instant and a local date and time — and never past the last moment the calendar has.</summary>
public class EventTimesTests
{
    private static readonly Instant Start = Instant.FromUtc(2026, 7, 15, 18, 0);
    private static readonly LocalDateTime LocalStart = new(2026, 7, 15, 18, 0);

    [Fact]
    public void An_event_ends_its_length_after_its_start()
    {
        Assert.Equal(Instant.FromUtc(2026, 7, 15, 19, 30), EventTimes.End(Start, 90));
        Assert.Equal(new LocalDateTime(2026, 7, 15, 19, 30), EventTimes.End(LocalStart, 90));
    }

    [Fact]
    public void An_event_with_no_length_set_runs_an_hour()
    {
        Assert.Equal(60, EventTimes.DefaultLengthMinutes);
        Assert.Equal(Instant.FromUtc(2026, 7, 15, 19, 0), EventTimes.End(Start, null));
        Assert.Equal(new LocalDateTime(2026, 7, 15, 19, 0), EventTimes.End(LocalStart, null));
    }

    [Fact]
    public void The_longest_event_ends_four_weeks_after_its_start()
    {
        // 40,320 minutes, the longest an event may be.
        Assert.Equal(Instant.FromUtc(2026, 8, 12, 18, 0), EventTimes.End(Start, 40320));
        Assert.Equal(new LocalDateTime(2026, 8, 12, 18, 0), EventTimes.End(LocalStart, 40320));
    }

    // The calendar ends with the year 9999. The two tests below sit either side of its last
    // minute: an end that still fits is the plain sum, and one that does not is the last moment
    // there is.
    [Fact]
    public void An_end_that_reaches_the_calendars_last_minute_is_the_plain_sum()
    {
        var lastMinute = Instant.FromUtc(9999, 12, 31, 23, 59);
        Assert.Equal(lastMinute, EventTimes.End(lastMinute - Duration.FromMinutes(90), 90));

        var lastLocalMinute = new LocalDateTime(9999, 12, 31, 23, 59);
        Assert.Equal(lastLocalMinute, EventTimes.End(lastLocalMinute.PlusMinutes(-90), 90));
    }

    [Fact]
    public void An_end_past_the_calendars_last_moment_is_that_last_moment()
    {
        var lastMinute = Instant.FromUtc(9999, 12, 31, 23, 59);
        Assert.Equal(Instant.MaxValue, EventTimes.End(lastMinute, 90));
        Assert.Equal(Instant.MaxValue, EventTimes.End(lastMinute, null));
        Assert.Equal(Instant.MaxValue, EventTimes.End(Instant.MaxValue, 40320));

        var lastLocalMinute = new LocalDateTime(9999, 12, 31, 23, 59);
        Assert.Equal(LocalDateTime.MaxIsoValue, EventTimes.End(lastLocalMinute, 90));
        Assert.Equal(LocalDateTime.MaxIsoValue, EventTimes.End(lastLocalMinute, null));
        Assert.Equal(LocalDateTime.MaxIsoValue, EventTimes.End(LocalDateTime.MaxIsoValue, 40320));
    }
}
