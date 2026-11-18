using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>What a running series goes on to post, asked of the projection directly: the later
/// starts the public calendar projects (<see cref="SeriesProjection.Upcoming"/>), and the later
/// occurrences a free-slots lookup counts for the member who created it, the ones the scheduler
/// will create after the occurrence that exists (<see cref="SeriesProjection.LaterOccurrences"/>).
/// Unless a test says otherwise the series is weekly on Friday at 7 PM Chicago time and three
/// hours long, first held on October 30, 2026; its second occurrence, November 6, has been
/// created and is over; and "now" is noon on Tuesday, November 10.</summary>
public class SeriesProjectionTests
{
    private static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];

    private static readonly Instant Now = At(2026, 11, 10, 12);

    /// <summary>The series the live occurrences here belong to; the projection does not read it.</summary>
    private static readonly Guid SeriesId = Guid.Empty;

    [Fact]
    public void A_weekly_series_gives_the_slots_inside_the_window_each_as_long_as_the_series_says()
    {
        var later = SeriesProjection.LaterOccurrences(Weekly(), live: null, Now, At(2026, 11, 16, 0), At(2026, 11, 30, 0));

        // November 13 is over before the window starts; the two Fridays inside it are given.
        Assert.Equal(
            [(At(2026, 11, 20, 19), At(2026, 11, 20, 22)), (At(2026, 11, 27, 19), At(2026, 11, 27, 22))],
            later);
    }

    [Fact]
    public void A_series_with_a_day_set_gives_every_day_of_the_set()
    {
        var schedule = Weekly() with
        {
            DaysOfWeek = RecurrenceDays.Tuesday | RecurrenceDays.Thursday,
            AnchorDate = new LocalDate(2026, 11, 3),
            CurrentOccurrenceDate = new LocalDate(2026, 11, 5),
        };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 9, 0), At(2026, 11, 16, 0));

        // Tuesday and Thursday, not the anchor's weekday alone.
        Assert.Equal(
            [(At(2026, 11, 10, 19), At(2026, 11, 10, 22)), (At(2026, 11, 12, 19), At(2026, 11, 12, 22))],
            later);
    }

    [Fact]
    public void Nothing_falls_after_the_until_date_and_the_date_itself_still_counts()
    {
        var schedule = Weekly() with { UntilDate = new LocalDate(2026, 11, 20) };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 11, 0), At(2026, 12, 14, 0));

        Assert.Equal([At(2026, 11, 13, 19), At(2026, 11, 20, 19)], later.Select(occurrence => occurrence.Start));
    }

    [Fact]
    public void With_a_count_only_as_many_as_are_left_are_given()
    {
        // Two of four have been created, so two are left: November 13 and 20.
        var schedule = Weekly() with { MaxOccurrences = 4 };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 11, 0), At(2026, 12, 14, 0));

        Assert.Equal([At(2026, 11, 13, 19), At(2026, 11, 20, 19)], later.Select(occurrence => occurrence.Start));
    }

    [Fact]
    public void The_occurrences_before_the_window_use_up_the_count_as_well()
    {
        var schedule = Weekly() with { MaxOccurrences = 4 };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 16, 0), At(2026, 12, 14, 0));

        // November 13 is before the window and takes one of the two that are left.
        Assert.Equal([At(2026, 11, 20, 19)], later.Select(occurrence => occurrence.Start));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void A_count_that_is_used_up_gives_none(int maxOccurrences)
    {
        var schedule = Weekly() with { MaxOccurrences = maxOccurrences };

        Assert.Empty(SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 11, 0), At(2026, 12, 14, 0)));
    }

    [Fact]
    public void The_live_occurrence_is_not_given_again_and_the_next_is_a_slot_on()
    {
        // The occurrence of November 13 exists and is where it belongs. The window takes it in,
        // but it is an event and is counted as one: what comes from here starts a week on.
        var schedule = Weekly() with { CurrentOccurrenceDate = new LocalDate(2026, 11, 13), OccurrenceCount = 3 };

        var later = SeriesProjection.LaterOccurrences(
            schedule, new LiveOccurrence(SeriesId, At(2026, 11, 13, 19), 180), Now, At(2026, 11, 11, 0), At(2026, 12, 7, 0));

        Assert.Equal(
            [At(2026, 11, 20, 19), At(2026, 11, 27, 19), At(2026, 12, 4, 19)], later.Select(occurrence => occurrence.Start));
    }

    [Fact]
    public void A_live_occurrence_moved_past_later_slots_hides_the_slots_it_passed()
    {
        // The occurrence of November 13 was moved, on its own, to Saturday the 21st. The scheduler
        // creates the next one when that one ends, so the slot of November 20 will never exist.
        var schedule = Weekly() with { CurrentOccurrenceDate = new LocalDate(2026, 11, 13), OccurrenceCount = 3 };

        var later = SeriesProjection.LaterOccurrences(schedule, new LiveOccurrence(SeriesId, At(2026, 11, 21, 20), 180), Now, At(2026, 11, 16, 0), At(2026, 12, 7, 0));

        Assert.Equal([At(2026, 11, 27, 19), At(2026, 12, 4, 19)], later.Select(occurrence => occurrence.Start));
    }

    [Fact]
    public void The_next_one_waits_at_least_for_the_live_occurrence_to_start()
    {
        // The occurrence of November 13 was moved to the 20th, and its length is below zero —
        // which only a row from before lengths were checked can have — so its end comes before
        // its start. The scheduler ends an occurrence only once it has started, so nothing new
        // comes before that either: the slot of the 20th is not given.
        var schedule = Weekly() with { CurrentOccurrenceDate = new LocalDate(2026, 11, 13), OccurrenceCount = 3 };

        var later = SeriesProjection.LaterOccurrences(
            schedule, new LiveOccurrence(SeriesId, At(2026, 11, 20, 19), -60), Now, At(2026, 11, 16, 0), At(2026, 11, 30, 0));

        Assert.Equal([At(2026, 11, 27, 19)], later.Select(occurrence => occurrence.Start));
    }

    [Fact]
    public void A_series_whose_live_occurrence_starts_after_the_window_is_passed_over_before_its_rule_is_read()
    {
        // Its rule could not be stepped at all — an interval of zero, which creating or editing a
        // series refuses — so an answer at all shows the rule was never read.
        var schedule = Weekly() with { Interval = 0 };

        var later = SeriesProjection.LaterOccurrences(
            schedule, new LiveOccurrence(SeriesId, At(2026, 11, 30, 19), 180), Now, At(2026, 11, 11, 0), At(2026, 11, 30, 0));

        Assert.Empty(later);
    }

    [Fact]
    public void A_series_whose_last_created_date_lies_past_the_window_is_passed_over_before_its_rule_is_read()
    {
        // The same rule that cannot be stepped; the last occurrence created is on December 18.
        var schedule = Weekly() with { Interval = 0, CurrentOccurrenceDate = new LocalDate(2026, 12, 18), OccurrenceCount = 9 };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 11, 0), At(2026, 11, 30, 0));

        Assert.Empty(later);
    }

    [Fact]
    public void A_series_whose_live_occurrence_starts_as_the_window_ends_cannot_reach_it()
    {
        var windowEnd = At(2026, 11, 16, 0);

        Assert.True(SeriesProjection.CanReachWindow(
            Weekly(), new LiveOccurrence(SeriesId, windowEnd - Duration.FromMinutes(1), 60), windowEnd));
        Assert.False(SeriesProjection.CanReachWindow(Weekly(), new LiveOccurrence(SeriesId, windowEnd, 60), windowEnd));
    }

    [Fact]
    public void A_series_whose_first_date_is_two_days_past_the_window_cannot_reach_it_and_one_day_past_can()
    {
        // The window ends as November 16 begins, so the date it ends on is the 16th.
        var windowEnd = At(2026, 11, 16, 0);

        Assert.True(SeriesProjection.CanReachWindow(Weekly() with { AnchorDate = new LocalDate(2026, 11, 17) }, null, windowEnd));
        Assert.False(SeriesProjection.CanReachWindow(Weekly() with { AnchorDate = new LocalDate(2026, 11, 18) }, null, windowEnd));
    }

    [Fact]
    public void A_series_whose_last_created_date_is_past_the_window_s_last_day_cannot_reach_it()
    {
        var windowEnd = At(2026, 11, 16, 0);

        Assert.True(SeriesProjection.CanReachWindow(
            Weekly() with { CurrentOccurrenceDate = new LocalDate(2026, 11, 16) }, null, windowEnd));
        Assert.False(SeriesProjection.CanReachWindow(
            Weekly() with { CurrentOccurrenceDate = new LocalDate(2026, 11, 17) }, null, windowEnd));
    }

    [Fact]
    public void A_series_whose_first_date_was_moved_into_the_window_starts_there()
    {
        // The first date was set later than the last occurrence created, to November 27: the next
        // is that date, and nothing comes before it.
        var schedule = Weekly() with { AnchorDate = new LocalDate(2026, 11, 27) };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 11, 0), At(2026, 12, 7, 0));

        Assert.Equal([At(2026, 11, 27, 19), At(2026, 12, 4, 19)], later.Select(occurrence => occurrence.Start));
    }

    [Fact]
    public void A_live_occurrence_that_is_already_over_holds_nothing_back()
    {
        var withNone = SeriesProjection.LaterOccurrences(Weekly(), live: null, Now, At(2026, 11, 11, 0), At(2026, 11, 30, 0));
        var withOneThatEnded = SeriesProjection.LaterOccurrences(
            Weekly(), new LiveOccurrence(SeriesId, At(2026, 11, 6, 19), 180), Now, At(2026, 11, 11, 0), At(2026, 11, 30, 0));

        Assert.Equal(withNone, withOneThatEnded);
        Assert.Equal(3, withNone.Count);
    }

    [Fact]
    public void An_occurrence_that_outlasts_the_gap_to_the_next_slot_takes_that_slot_with_it()
    {
        // Daily at 8 PM and 36 hours long. The live one runs from the 9th to the morning of the
        // 11th, so the slot of the 10th is passed, and every second evening after it as well.
        var schedule = new SeriesSchedule(
            RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth, RecurrenceDays.None,
            AnchorDate: new LocalDate(2026, 11, 9), StartTime: new LocalTime(20, 0), TimeZone: "America/Chicago",
            UntilDate: null, MaxOccurrences: null,
            CurrentOccurrenceDate: new LocalDate(2026, 11, 9), OccurrenceCount: 1, DurationMinutes: 36 * 60);

        var later = SeriesProjection.LaterOccurrences(
            schedule, new LiveOccurrence(SeriesId, At(2026, 11, 9, 20), 36 * 60), At(2026, 11, 10, 9), At(2026, 11, 11, 0), At(2026, 11, 16, 0));

        Assert.Equal(
            [
                (At(2026, 11, 11, 20), At(2026, 11, 13, 8)),
                (At(2026, 11, 13, 20), At(2026, 11, 15, 8)),
                (At(2026, 11, 15, 20), At(2026, 11, 17, 8)),
            ],
            later);
    }

    [Fact]
    public void One_that_starts_before_the_window_and_reaches_into_it_is_given_and_one_that_ends_as_it_starts_is_not()
    {
        // Friday 10 PM to Saturday 2 AM.
        var schedule = Weekly() with { StartTime = new LocalTime(22, 0), DurationMinutes = 240 };

        var reachesIn = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 14, 0), At(2026, 11, 15, 0));
        var endsAsItStarts = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 14, 2), At(2026, 11, 15, 0));

        Assert.Equal([(At(2026, 11, 13, 22), At(2026, 11, 14, 2))], reachesIn);
        Assert.Empty(endsAsItStarts);
    }

    [Fact]
    public void One_that_starts_as_the_window_ends_is_not_given()
    {
        var later = SeriesProjection.LaterOccurrences(Weekly(), live: null, Now, At(2026, 11, 16, 0), At(2026, 11, 20, 19));

        Assert.Empty(later);
    }

    [Fact]
    public void A_series_with_no_length_set_counts_sixty_minutes()
    {
        var schedule = Weekly() with { DurationMinutes = null };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 11, 0), At(2026, 11, 15, 0));

        Assert.Equal([(At(2026, 11, 13, 19), At(2026, 11, 13, 20))], later);
    }

    [Fact]
    public void The_start_keeps_its_wall_clock_time_when_the_clocks_change()
    {
        // Wednesdays at 7 PM; Chicago leaves daylight time on Sunday, November 1, 2026.
        var schedule = Weekly() with
        {
            AnchorDate = new LocalDate(2026, 10, 21),
            CurrentOccurrenceDate = new LocalDate(2026, 10, 21),
            OccurrenceCount = 1,
        };

        var later = SeriesProjection.LaterOccurrences(
            schedule, live: null, At(2026, 10, 22, 12), At(2026, 10, 26, 0), At(2026, 11, 9, 0));

        Assert.Equal([At(2026, 10, 28, 19), At(2026, 11, 4, 19)], later.Select(occurrence => occurrence.Start));
        Assert.Equal(Duration.FromDays(7) + Duration.FromHours(1), later[1].Start - later[0].Start);
    }

    [Fact]
    public void A_zone_that_is_not_known_is_read_as_utc()
    {
        var schedule = Weekly() with { TimeZone = "Nowhere/At_All" };

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2026, 11, 11, 0), At(2026, 11, 15, 0));

        Assert.Equal([Instant.FromUtc(2026, 11, 13, 19, 0)], later.Select(occurrence => occurrence.Start));
    }

    [Fact]
    public void A_daily_series_looked_up_a_year_ahead_reaches_the_last_day_inside_the_step_bound()
    {
        // The furthest a lookup reaches: a first day 366 days ahead, fourteen days, and a day more.
        var schedule = new SeriesSchedule(
            RecurrenceUnit.Day, 1, MonthlyMode.DayOfMonth, RecurrenceDays.None,
            AnchorDate: new LocalDate(2026, 11, 1), StartTime: new LocalTime(18, 0), TimeZone: "America/Chicago",
            UntilDate: null, MaxOccurrences: null,
            CurrentOccurrenceDate: new LocalDate(2026, 11, 9), OccurrenceCount: 9, DurationMinutes: 60);

        var later = SeriesProjection.LaterOccurrences(schedule, live: null, Now, At(2027, 11, 11, 0), At(2027, 11, 26, 0));

        // One step a day from today to the window's end is 381 steps, well inside the bound.
        Assert.Equal(15, later.Count);
        Assert.Equal((At(2027, 11, 11, 18), At(2027, 11, 11, 19)), later[0]);
        Assert.Equal((At(2027, 11, 25, 18), At(2027, 11, 25, 19)), later[^1]);
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

    /// <summary>The series of the class summary: weekly on Friday at 7 PM Chicago time, three hours
    /// long, with its second occurrence, November 6, the last one created.</summary>
    private static SeriesSchedule Weekly() => new(
        RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, RecurrenceDays.None,
        AnchorDate: new LocalDate(2026, 10, 30), StartTime: new LocalTime(19, 0), TimeZone: "America/Chicago",
        UntilDate: null, MaxOccurrences: null,
        CurrentOccurrenceDate: new LocalDate(2026, 11, 6), OccurrenceCount: 2, DurationMinutes: 180);

    private static Instant At(int year, int month, int day, int hour, int minute = 0) =>
        new LocalDateTime(year, month, day, hour, minute).InZoneStrictly(Chicago).ToInstant();
}
