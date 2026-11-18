using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The month view's projection of a repeating series, on fixed dates. A weekly series
/// with a day set shows every selected weekday — the same occurrences the series goes on to post
/// and the ICS feed's BYDAY rule expands to — and the until date and the occurrence count end it
/// where they end the series. A posted occurrence that was moved later, or that runs long, holds
/// back the dates it passes: the series posts its next occurrence only once that one has ended.</summary>
public class PublicCalendarBuilderTests
{
    private const RecurrenceDays MonWedFri =
        RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday;

    private const RecurrenceDays TueThu = RecurrenceDays.Tuesday | RecurrenceDays.Thursday;

    private static readonly IReadOnlyDictionary<long, string> NoChannels = new Dictionary<long, string>();

    /// <summary>Noon on Saturday 2026-10-31: the whole of November 2026 lies ahead.</summary>
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 31, 12, 0);

    private static readonly LocalDate Monday = new(2026, 11, 2);

    [Fact]
    public void A_day_set_shows_every_selected_weekday_of_the_month()
    {
        var series = Series(MonWedFri, Monday);

        var month = November(series);

        // The first occurrence is the posted row; every later Monday, Wednesday and Friday is
        // projected — not the first occurrence's weekday alone.
        Assert.Equal([2], Days(month, projected: false));
        Assert.Equal([4, 6, 9, 11, 13, 16, 18, 20, 23, 25, 27, 30], Days(month, projected: true));
        Assert.All(month.Events, e => Assert.Equal(new TimeSpan(18, 0, 0), e.StartsAtLocal.TimeOfDay));
    }

    [Fact]
    public void A_day_set_with_an_until_date_stops_on_that_date()
    {
        var series = Series(MonWedFri, Monday);

        series.UntilDate = new LocalDate(2026, 11, 18); // a Wednesday, inclusive
        Assert.Equal([4, 6, 9, 11, 13, 16, 18], Days(November(series), projected: true));

        series.UntilDate = new LocalDate(2026, 11, 17); // a Tuesday: Monday the 16th is the last
        Assert.Equal([4, 6, 9, 11, 13, 16], Days(November(series), projected: true));
    }

    [Fact]
    public void A_day_set_with_a_count_shows_that_many_day_instances_and_no_more()
    {
        var series = Series(MonWedFri, Monday);
        series.MaxOccurrences = 6; // the posted first occurrence is 1 of 6

        var month = November(series);

        // Five more day-instances — the series ends on Friday the 13th, so nothing is shown in
        // the weeks after it.
        Assert.Equal([2], Days(month, projected: false));
        Assert.Equal([4, 6, 9, 11, 13], Days(month, projected: true));
    }

    [Fact]
    public void A_count_is_used_up_by_the_day_instances_before_the_month_shown()
    {
        var series = Series(MonWedFri, Monday);
        series.MaxOccurrences = 20;

        var december = Build(series, 2026, 12);

        // Thirteen of the twenty fall in November (the 2nd to the 30th); December has the last seven.
        Assert.Equal([2, 4, 7, 9, 11, 14, 16], Days(december, projected: true));
    }

    [Fact]
    public void A_first_occurrence_outside_the_day_set_is_not_repeated_on_its_weekday()
    {
        // Created on a Sunday to repeat on Tuesdays and Thursdays: only the first occurrence is a
        // Sunday.
        var series = Series(TueThu, new LocalDate(2026, 11, 1));

        var month = November(series);

        Assert.Equal([1], Days(month, projected: false));
        Assert.Equal([3, 5, 10, 12, 17, 19, 24, 26], Days(month, projected: true));
    }

    [Fact]
    public void A_day_set_every_second_week_shows_the_on_weeks_only()
    {
        var series = Series(TueThu, new LocalDate(2026, 11, 3), interval: 2);

        // On-weeks start on 2, 16 and 30 November and on 14 and 28 December.
        Assert.Equal([5, 17, 19], Days(November(series), projected: true));
        Assert.Equal([1, 3, 15, 17, 29, 31], Days(Build(series, 2026, 12), projected: true));
    }

    [Fact]
    public void On_weeks_run_monday_to_sunday_from_the_week_of_the_first_occurrence()
    {
        // Tue + Sun from Tuesday the 3rd: an on-week's Sunday is the one that ends it — the 8th
        // and the 22nd, not the 15th.
        var tueSun = Series(RecurrenceDays.Tuesday | RecurrenceDays.Sunday, new LocalDate(2026, 11, 3), interval: 2);
        Assert.Equal([8, 17, 22], Days(November(tueSun), projected: true));

        // Tue + Thu from Thursday the 5th: Tuesday the 10th is in an off-week, though it is less
        // than two weeks after the first occurrence.
        var fromThursday = Series(TueThu, new LocalDate(2026, 11, 5), interval: 2);
        Assert.Equal([17, 19], Days(November(fromThursday), projected: true));
    }

    [Fact]
    public void A_series_under_way_goes_on_from_its_posted_occurrence()
    {
        // Monday's occurrence has ended and Wednesday's is posted: the cursor is on Wednesday.
        var wednesday = new LocalDate(2026, 11, 4);
        var series = Series(MonWedFri, Monday);
        series.CurrentOccurrenceDate = wednesday;
        series.OccurrenceCount = 2;
        Event[] posted = [Occurrence(series, Monday, EventStatus.Ended), Occurrence(series, wednesday, EventStatus.Scheduled)];

        var month = PublicCalendarBuilder.Build(
            null, DateTimeZone.Utc, 2026, 11, posted, [series], NoChannels, Instant.FromUtc(2026, 11, 3, 12, 0),
            LiveOf(posted));

        Assert.Equal([2, 4], Days(month, projected: false));
        Assert.Equal([6, 9, 11, 13, 16, 18, 20, 23, 25, 27, 30], Days(month, projected: true));
    }

    [Fact]
    public void A_posted_occurrence_moved_onto_the_next_date_is_shown_once()
    {
        // Monday's occurrence was moved to Wednesday 18:00 by an edit of that occurrence alone, so
        // the series' cursor is still on Monday and Wednesday is its next date as well.
        var series = Series(MonWedFri, Monday);
        var moved = Occurrence(series, new LocalDate(2026, 11, 4), EventStatus.Scheduled);

        var month = PublicCalendarBuilder.Build(
            null, DateTimeZone.Utc, 2026, 11, [moved], [series], NoChannels, Now, LiveOf(moved));

        Assert.Equal([4], Days(month, projected: false));
        Assert.Equal([6, 9, 11, 13, 16, 18, 20, 23, 25, 27, 30], Days(month, projected: true));
    }

    [Fact]
    public void A_seven_day_set_reaches_the_furthest_month_the_calendar_serves()
    {
        // The densest day set steps like a daily series, and the step limit must still reach the
        // last day of the furthest month. A demanding case: seen from the first instant of a
        // month, with today's occurrence still to come, and a month of 31 days 24 months on —
        // 762 dates to step. (A clock change between the server's zone and the series' can add
        // one.)
        var series = Series(RecurrenceDays.All, new LocalDate(2026, 11, 30));
        var now = Instant.FromUtc(2026, 12, 1, 0, 0);
        Assert.True(PublicCalendarBuilder.IsMonthInRange(2028, 12, now.InUtc().Date));
        Assert.False(PublicCalendarBuilder.IsMonthInRange(2029, 1, now.InUtc().Date));

        var month = PublicCalendarBuilder.Build(null, DateTimeZone.Utc, 2028, 12, [], [series], NoChannels, now, []);

        Assert.Equal(Enumerable.Range(1, 31), Days(month, projected: true));
    }

    [Fact]
    public void A_day_set_keeps_its_local_time_across_a_clock_change()
    {
        // US clocks go back on Sunday 2026-11-01: 8pm stays 8pm, and the UTC start moves an hour.
        var series = Series(RecurrenceDays.Weekdays, new LocalDate(2026, 10, 26), zone: "America/Chicago");
        series.StartTime = new LocalTime(20, 0);
        var zone = DateTimeZoneProviders.Tzdb["America/Chicago"];
        var now = Instant.FromUtc(2026, 10, 25, 12, 0);

        var live = Live(series);
        var october = PublicCalendarBuilder.Build(null, zone, 2026, 10, [live], [series], NoChannels, now, LiveOf(live));
        var november = PublicCalendarBuilder.Build(null, zone, 2026, 11, [], [series], NoChannels, now, LiveOf(live));

        Assert.Equal([27, 28, 29, 30], Days(october, projected: true));
        Assert.Equal(
            [2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 16, 17, 18, 19, 20, 23, 24, 25, 26, 27, 30],
            Days(november, projected: true));
        Assert.All(
            october.Events.Concat(november.Events),
            e => Assert.Equal(new TimeSpan(20, 0, 0), e.StartsAtLocal.TimeOfDay));
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 1, 0, 0, TimeSpan.Zero), october.Events[^1].StartsAtUtc);  // CDT
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 2, 0, 0, TimeSpan.Zero), november.Events[0].StartsAtUtc);   // CST
    }

    [Fact]
    public void A_weekly_series_without_a_day_set_still_shows_its_one_weekday()
    {
        var series = Series(RecurrenceDays.None, Monday);

        Assert.Equal([9, 16, 23, 30], Days(November(series), projected: true));
    }

    // ---------- a posted occurrence moved later, or one that runs long ----------
    //
    // A series has one posted occurrence at a time. It posts the next one only once that one has
    // ended, and then takes the first of its dates that starts after that moment. An edit of the
    // posted occurrence alone moves it, or changes its length, without moving the series' place
    // in its schedule — so the dates it passes are never posted, and they use up none of a count.

    [Fact]
    public void A_posted_occurrence_moved_past_the_next_date_holds_that_date_back()
    {
        // Daily at 18:00 from Monday the 2nd; Monday's occurrence was moved to Tuesday 20:00. It
        // ends at 21:00, so Tuesday's own 18:00 is never posted: Wednesday's is next.
        var series = Daily(Monday);
        var moved = Posted(series, new LocalDateTime(2026, 11, 3, 20, 0));

        var month = View(series, moved, 2026, 11);

        Assert.Equal([3], Days(month, projected: false));
        Assert.Equal(Enumerable.Range(4, 27), Days(month, projected: true));
    }

    [Fact]
    public void A_posted_occurrence_moved_past_several_dates_holds_them_all_back()
    {
        // Moved to Thursday 20:00: Tuesday's, Wednesday's and Thursday's 18:00 are all passed.
        var series = Daily(Monday);
        var moved = Posted(series, new LocalDateTime(2026, 11, 5, 20, 0));

        var month = View(series, moved, 2026, 11);

        Assert.Equal([5], Days(month, projected: false));
        Assert.Equal(Enumerable.Range(6, 25), Days(month, projected: true));
    }

    [Fact]
    public void A_day_set_occurrence_moved_past_two_of_its_days_goes_on_from_the_day_after()
    {
        // Mon + Wed + Fri; Monday's occurrence was moved to Friday 20:00, past Wednesday's and
        // Friday's own 18:00.
        var series = Series(MonWedFri, Monday);
        var moved = Posted(series, new LocalDateTime(2026, 11, 6, 20, 0));

        var month = View(series, moved, 2026, 11);

        Assert.Equal([6], Days(month, projected: false));
        Assert.Equal([9, 11, 13, 16, 18, 20, 23, 25, 27, 30], Days(month, projected: true));
    }

    [Fact]
    public void Dates_a_moved_occurrence_passed_use_up_none_of_the_count()
    {
        // Mon + Wed + Fri, six in all; Monday's occurrence was moved onto Wednesday 18:00.
        // Wednesday's own occurrence is never posted, so five more follow the moved one: the last
        // is Monday the 16th, not Friday the 13th.
        var daySet = Series(MonWedFri, Monday);
        daySet.MaxOccurrences = 6;
        var month = View(daySet, Posted(daySet, new LocalDateTime(2026, 11, 4, 18, 0)), 2026, 11);
        Assert.Equal([4], Days(month, projected: false));
        Assert.Equal([6, 9, 11, 13, 16], Days(month, projected: true));

        // Daily, four in all; Monday's was moved to Tuesday 20:00: Wednesday, Thursday and Friday
        // follow.
        var daily = Daily(Monday);
        daily.MaxOccurrences = 4;
        month = View(daily, Posted(daily, new LocalDateTime(2026, 11, 3, 20, 0)), 2026, 11);
        Assert.Equal([3], Days(month, projected: false));
        Assert.Equal([4, 5, 6], Days(month, projected: true));
    }

    [Fact]
    public void An_until_date_ends_a_series_whose_occurrence_was_moved_where_it_ends_the_series()
    {
        // Daily until Friday the 6th; Monday's occurrence was moved to Thursday 20:00: only
        // Friday is left.
        var daily = Daily(Monday);
        var thursdayEvening = new LocalDateTime(2026, 11, 5, 20, 0);
        daily.UntilDate = new LocalDate(2026, 11, 6);
        Assert.Equal([6], Days(View(daily, Posted(daily, thursdayEvening), 2026, 11), projected: true));

        // Until Thursday the 5th: nothing is left to post.
        daily.UntilDate = new LocalDate(2026, 11, 5);
        Assert.Empty(Days(View(daily, Posted(daily, thursdayEvening), 2026, 11), projected: true));

        // Mon + Wed + Fri until Wednesday the 11th; Monday's was moved to Friday 20:00.
        var daySet = Series(MonWedFri, Monday);
        daySet.UntilDate = new LocalDate(2026, 11, 11);
        var month = View(daySet, Posted(daySet, new LocalDateTime(2026, 11, 6, 20, 0)), 2026, 11);
        Assert.Equal([9, 11], Days(month, projected: true));
    }

    [Fact]
    public void A_posted_occurrence_that_runs_long_holds_back_the_dates_it_runs_past()
    {
        // Daily; Monday's occurrence was given a length of two and a half days by an edit of that
        // occurrence alone, so it ends on Thursday at 06:00. Tuesday's and Wednesday's 18:00 are
        // never posted; Thursday's is.
        var series = Daily(Monday);
        var month = View(series, Posted(series, Monday.At(series.StartTime), minutes: 2 * 24 * 60 + 12 * 60), 2026, 11);
        Assert.Equal([2], Days(month, projected: false));
        Assert.Equal(Enumerable.Range(5, 26), Days(month, projected: true));

        // Three days exactly: it ends on Thursday at 18:00, the moment Thursday's occurrence would
        // start. The series takes the first date that starts after the end: Friday's.
        month = View(series, Posted(series, Monday.At(series.StartTime), minutes: 72 * 60), 2026, 11);
        Assert.Equal(Enumerable.Range(6, 25), Days(month, projected: true));
    }

    [Fact]
    public void An_occurrence_with_no_length_of_its_own_runs_an_hour()
    {
        // Daily; Monday's occurrence was moved to Tuesday 17:00 and has no length of its own, so
        // it runs the default hour, to 18:00 — the moment Tuesday's own occurrence would start,
        // which is therefore never posted.
        var series = Daily(Monday);

        var month = View(series, Posted(series, new LocalDateTime(2026, 11, 3, 17, 0)), 2026, 11);

        Assert.Equal([3], Days(month, projected: false));
        Assert.Equal(Enumerable.Range(4, 27), Days(month, projected: true));

        // Moved to 16:59 instead, it is over at 17:59, and Tuesday's 18:00 is posted: two entries
        // on Tuesday.
        month = View(series, Posted(series, new LocalDateTime(2026, 11, 3, 16, 59)), 2026, 11);

        Assert.Equal([3], Days(month, projected: false));
        Assert.Equal(Enumerable.Range(3, 28), Days(month, projected: true));
    }

    [Fact]
    public void A_posted_occurrence_in_another_month_still_holds_dates_back()
    {
        // Daily from Friday 30 October; that occurrence was moved to Tuesday 3 November 20:00.
        var series = Daily(new LocalDate(2026, 10, 30));
        var moved = Posted(series, new LocalDateTime(2026, 11, 3, 20, 0));
        var now = Instant.FromUtc(2026, 10, 29, 12, 0);

        // October: the posted occurrence is not in this month, and Saturday the 31st will not be
        // posted — there is nothing to show.
        Assert.Empty(View(series, moved, 2026, 10, now).Events);

        // November: the moved occurrence, then every day from the 4th.
        var november = View(series, moved, 2026, 11, now);
        Assert.Equal([3], Days(november, projected: false));
        Assert.Equal(Enumerable.Range(4, 27), Days(november, projected: true));
    }

    [Fact]
    public void A_posted_occurrence_moved_beyond_the_month_shown_leaves_that_month_empty()
    {
        // Daily from Monday 2 November; Monday's occurrence was moved to 15 December 20:00.
        var series = Daily(Monday);
        var moved = Posted(series, new LocalDateTime(2026, 12, 15, 20, 0));

        Assert.Empty(View(series, moved, 2026, 11).Events);

        var december = View(series, moved, 2026, 12);
        Assert.Equal([15], Days(december, projected: false));
        Assert.Equal(Enumerable.Range(16, 16), Days(december, projected: true));
    }

    [Fact]
    public void A_count_seen_from_a_later_month_allows_for_the_dates_that_were_passed()
    {
        // Daily, forty in all, from Monday 2 November; Monday's occurrence was moved to Tuesday
        // 20:00. The moved one, then the 4th of November to the 12th of December — a day later
        // than forty dates counted from the 2nd.
        var series = Daily(Monday);
        series.MaxOccurrences = 40;
        var moved = Posted(series, new LocalDateTime(2026, 11, 3, 20, 0));

        var december = View(series, moved, 2026, 12);

        Assert.Equal(Enumerable.Range(1, 12), Days(december, projected: true));
    }

    [Fact]
    public void A_long_occurrence_begun_in_the_month_before_holds_back_this_months_first_dates()
    {
        // Daily from Friday 30 October, and that occurrence lasts three days: it ends on Monday
        // 2 November at 18:00. Seen on the 31st, while it is under way.
        var series = Daily(new LocalDate(2026, 10, 30));
        var underWay = Posted(
            series, new LocalDateTime(2026, 10, 30, 18, 0), minutes: 72 * 60, status: EventStatus.Started);

        var november = View(series, underWay, 2026, 11);

        Assert.Empty(Days(november, projected: false));
        Assert.Equal(Enumerable.Range(3, 28), Days(november, projected: true));
    }

    [Fact]
    public void A_series_whose_own_length_reaches_its_next_date_shows_the_dates_it_will_post()
    {
        // Daily, four in all, each a day and a half long: Monday's ends on Wednesday at 06:00, so
        // the series posts Wednesday, then Friday, then Sunday — every second date.
        var series = Daily(Monday);
        series.DurationMinutes = 36 * 60;
        series.MaxOccurrences = 4;
        var first = Posted(series, Monday.At(series.StartTime), series.DurationMinutes);

        var month = View(series, first, 2026, 11);

        Assert.Equal([2], Days(month, projected: false));
        Assert.Equal([4, 6, 8], Days(month, projected: true));
    }

    [Fact]
    public void A_moved_occurrence_that_ends_before_the_next_date_begins_leaves_it_in_place()
    {
        // Daily; Monday's occurrence was moved to Tuesday 09:00. It is over by 10:00, so
        // Tuesday's own 18:00 is still posted: two entries on Tuesday.
        var series = Daily(Monday);

        var month = View(series, Posted(series, new LocalDateTime(2026, 11, 3, 9, 0)), 2026, 11);

        Assert.Equal([3], Days(month, projected: false));
        Assert.Equal(Enumerable.Range(3, 28), Days(month, projected: true));
    }

    [Fact]
    public void An_occurrence_moved_earlier_changes_nothing_after_it()
    {
        // Daily; Monday's occurrence was moved to Sunday the 1st, 10:00. The series' place in its
        // schedule is still Monday, so Tuesday's is the next one posted.
        var series = Daily(Monday);

        var month = View(series, Posted(series, new LocalDateTime(2026, 11, 1, 10, 0)), 2026, 11);

        Assert.Equal([1], Days(month, projected: false));
        Assert.Equal(Enumerable.Range(3, 28), Days(month, projected: true));
    }

    [Fact]
    public void An_occurrence_whose_time_has_run_out_holds_nothing_back()
    {
        // Monday's occurrence ran from 18:00 to 19:00. Seen at 19:30, before it has been marked
        // as ended, Tuesday's is next as usual.
        var series = Daily(Monday);
        var over = Posted(series, Monday.At(series.StartTime), status: EventStatus.Started);

        var month = View(series, over, 2026, 11, Instant.FromUtc(2026, 11, 2, 19, 30));

        Assert.Equal(Enumerable.Range(3, 28), Days(month, projected: true));

        // Still not marked as ended a day later, at Tuesday 19:30: the dates start from now, not
        // from its end, so Tuesday's 18:00, already past, is not shown.
        month = View(series, over, 2026, 11, Instant.FromUtc(2026, 11, 3, 19, 30));

        Assert.Equal(Enumerable.Range(4, 27), Days(month, projected: true));
    }

    // ---------- helpers ----------

    private static PublicCalendarDto November(EventSeries series) => Build(series, 2026, 11);

    /// <summary>The month view of one series whose first occurrence is posted and still ahead.</summary>
    private static PublicCalendarDto Build(EventSeries series, int year, int month) =>
        View(series, Live(series), year, month);

    /// <summary>The month view of one series and its posted occurrence, put together the way the
    /// route does it: the occurrence is among the month's events only when it starts in that
    /// month.</summary>
    private static PublicCalendarDto View(
        EventSeries series, Event posted, int year, int month, Instant? now = null)
    {
        var zone = DateTimeZoneProviders.Tzdb[series.TimeZone];
        var (start, end) = PublicCalendarBuilder.MonthWindow(zone, year, month);
        Event[] inMonth = posted.StartsAt >= start && posted.StartsAt < end ? [posted] : [];
        return PublicCalendarBuilder.Build(
            "Guild", zone, year, month, inMonth, [series], NoChannels, now ?? Now, LiveOf(posted));
    }

    /// <summary>What the route reads beside the month's events: the live (scheduled or started)
    /// occurrence of each running series, in whatever month it lies.</summary>
    private static LiveOccurrence[] LiveOf(params Event[] occurrences) =>
        [.. occurrences
            .Where(e => e.Status is EventStatus.Scheduled or EventStatus.Started)
            .Select(e => new LiveOccurrence(e.SeriesId!.Value, e.StartsAt, e.DurationMinutes))];

    /// <summary>Days of the month of the view's entries, in start order.</summary>
    private static int[] Days(PublicCalendarDto month, bool projected) =>
        [.. month.Events.Where(e => e.Projected == projected).Select(e => e.StartsAtLocal.Day)];

    /// <summary>The posted occurrence on the series' slot cursor.</summary>
    private static Event Live(EventSeries series) =>
        Occurrence(series, series.CurrentOccurrenceDate, EventStatus.Scheduled);

    /// <summary>An occurrence row of the series on a date, at the series' local time.</summary>
    private static Event Occurrence(EventSeries series, LocalDate date, EventStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Title = series.Title,
        SeriesId = series.Id,
        StartsAt = (date + series.StartTime).InZoneLeniently(DateTimeZoneProviders.Tzdb[series.TimeZone]).ToInstant(),
        TimeZone = series.TimeZone,
        Status = status,
    };

    /// <summary>The series' posted occurrence as an edit of that occurrence alone leaves it: its
    /// own start and length, and the series' place in its schedule untouched.</summary>
    private static Event Posted(
        EventSeries series, LocalDateTime start, int? minutes = null, EventStatus status = EventStatus.Scheduled) => new()
    {
        Id = Guid.NewGuid(),
        Title = series.Title,
        SeriesId = series.Id,
        StartsAt = start.InZoneLeniently(DateTimeZoneProviders.Tzdb[series.TimeZone]).ToInstant(),
        TimeZone = series.TimeZone,
        DurationMinutes = minutes,
        Status = status,
    };

    /// <summary>A series that repeats every day at 18:00 UTC, its first occurrence posted.</summary>
    private static EventSeries Daily(LocalDate anchor)
    {
        var series = Series(RecurrenceDays.None, anchor);
        series.Unit = RecurrenceUnit.Day;
        return series;
    }

    private static EventSeries Series(
        RecurrenceDays days, LocalDate anchor, int interval = 1, string zone = "UTC") => new()
    {
        Id = Guid.NewGuid(),
        Unit = RecurrenceUnit.Week,
        Interval = interval,
        DaysOfWeek = days,
        AnchorDate = anchor,
        StartTime = new LocalTime(18, 0),
        TimeZone = zone,
        CurrentOccurrenceDate = anchor,
        OccurrenceCount = 1,
        Title = "Class",
    };
}
