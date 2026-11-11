using CalCrony.Api.Services;
using NodaTime;
using NodaTime.Testing;

namespace CalCrony.Api.Tests;

public class NaturalDateTimeParserTests
{
    // Wednesday 2026-07-15 15:00 UTC == 10:00 in Chicago (CDT, UTC-5).
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 15, 15, 0);
    private static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];

    private static NaturalDateTimeParser CreateParser() => new(new FakeClock(Now));

    [Fact]
    public void Parses_relative_offsets()
    {
        var ok = CreateParser().TryResolve("in 2 hours", Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(Now.Plus(Duration.FromHours(2)), result);
    }

    [Fact]
    public void Explicit_reference_overrides_the_clock_and_keeps_multi_text_requests_consistent()
    {
        // The parser's own clock says Now; the caller pins a different reference — the
        // clock must not leak into the result.
        var parser = CreateParser();
        var reference = Now.Plus(Duration.FromMinutes(90));

        var ok = parser.TryResolve("in 2 hours", Chicago, out var anchored, out var error, reference);
        Assert.True(ok, error);
        Assert.Equal(reference.Plus(Duration.FromHours(2)), anchored);

        // Equivalent relative texts against ONE reference resolve identically — the guarantee
        // CreatePoll's duplicate-slot 409 relies on (per-call clock reads could straddle a
        // minute boundary and make these differ).
        Assert.True(parser.TryResolve("in 120 minutes", Chicago, out var sameSlot, out _, reference));
        Assert.Equal(anchored, sameSlot);
    }

    [Fact]
    public void Parses_tomorrow_with_time_in_zone()
    {
        var ok = CreateParser().TryResolve("tomorrow 6pm", Chicago, out var result, out var error);

        Assert.True(ok, error);
        // Tomorrow (Jul 16) 18:00 CDT == 23:00 UTC.
        Assert.Equal(Instant.FromUtc(2026, 7, 16, 23, 0), result);
    }

    [Fact]
    public void Bare_time_already_past_today_rolls_to_tomorrow()
    {
        // 9am Chicago has passed (it is 10:00 local).
        var ok = CreateParser().TryResolve("9am", Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(Instant.FromUtc(2026, 7, 16, 14, 0), result);
    }

    [Fact]
    public void Weekday_with_time_prefers_future_occurrence()
    {
        var ok = CreateParser().TryResolve("6 PM on Wednesday", Chicago, out var result, out var error);

        Assert.True(ok, error);
        // Today is Wednesday and 18:00 local is still ahead — same day.
        Assert.Equal(Instant.FromUtc(2026, 7, 15, 23, 0), result);
    }

    [Fact]
    public void Rejects_unparseable_text()
    {
        var ok = CreateParser().TryResolve("banana hammock", Chicago, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_explicitly_past_dates()
    {
        var ok = CreateParser().TryResolve("january 1 2020", Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Contains("past", error);
    }

    [Fact]
    public void Zone_abbreviation_overrides_caller_zone()
    {
        // Joshua's live repro: server tz unset (UTC), user types "…10:00 AM CST" 11 minutes
        // before 10:00 Central. Without the override this parsed as 10:00 UTC = past.
        var ok = CreateParser().TryResolve("7/15/2026 10:15 AM CST", DateTimeZone.Utc, out var result, out var error);

        Assert.True(ok, error);
        // "CST" maps to America/Chicago, which in July observes CDT (UTC-5) — the wall clock the
        // user meant, not the strict standard-time offset.
        Assert.Equal(Instant.FromUtc(2026, 7, 15, 15, 15), result);
    }

    [Theory]
    [InlineData("6pm ET", 22)]   // America/New_York, EDT = UTC-4
    [InlineData("6pm PDT", 25)]  // America/Los_Angeles, UTC-7 → next day 01:00
    [InlineData("6pm UTC", 18)]
    public void Zone_abbreviations_cover_common_us_zones(string text, int expectedUtcHour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        var expected = expectedUtcHour < 24
            ? Instant.FromUtc(2026, 7, 15, expectedUtcHour, 0)
            : Instant.FromUtc(2026, 7, 16, expectedUtcHour - 24, 0);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Text_without_abbreviation_still_uses_caller_zone()
    {
        var ok = CreateParser().TryResolve("tomorrow 6pm", DateTimeZone.Utc, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(Instant.FromUtc(2026, 7, 16, 18, 0), result);
    }

    // ----- Day-level resolution (/availability slots): whole days, today valid until it ends -----

    [Theory]
    [InlineData("today", 2026, 7, 15)]
    [InlineData("now", 2026, 7, 15)]
    [InlineData("tonight", 2026, 7, 15)]
    [InlineData("wednesday", 2026, 7, 15)]      // it is Wednesday: last week's and today's are offered
    [InlineData("tomorrow", 2026, 7, 16)]
    [InlineData("saturday", 2026, 7, 18)]
    [InlineData("jul 20", 2026, 7, 20)]
    [InlineData("2026-07-20", 2026, 7, 20)]
    [InlineData("in 3 days", 2026, 7, 18)]
    [InlineData("jul 20 8am", 2026, 7, 20)]     // a time of day is ignored
    [InlineData("6pm", 2026, 7, 15)]            // a time with no date names no day: today
    [InlineData("9am", 2026, 7, 15)]            // …even one that has passed (TryResolve rolls it to tomorrow)
    [InlineData("next week", 2026, 7, 20)]      // a range that hasn't begun: its first day
    [InlineData("next month", 2026, 8, 1)]
    [InlineData("jul 1", 2027, 7, 1)]           // a bare date already behind us rolls a year, as in TryResolve
    public void Day_resolution_reduces_text_to_a_day(string text, int year, int month, int dayOfMonth)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out var day, out var error);

        Assert.True(ok, error);
        Assert.Equal(new LocalDate(year, month, dayOfMonth), day);
    }

    [Theory]
    [InlineData("friday at night", 2026, 7, 17)]        // "at night" alone would be tonight
    [InlineData("friday (6pm)", 2026, 7, 17)]
    [InlineData("saturday from 6pm", 2026, 7, 18)]
    [InlineData("tomorrow, after 6pm", 2026, 7, 16)]
    [InlineData("friday, this week", 2026, 7, 17)]      // "this week" alone would be today
    [InlineData("jul 20 this year", 2026, 7, 20)]
    public void A_day_the_text_names_outranks_a_today_that_is_only_implied(string text, int year, int month, int dayOfMonth)
    {
        // The recognizer returns the day and the detached time (or the range under way) as separate
        // readings. Taking the earliest of them would answer with today for every one of these.
        var ok = CreateParser().TryResolveDay(text, Chicago, out var day, out var error);

        Assert.True(ok, error);
        Assert.Equal(new LocalDate(year, month, dayOfMonth), day);
    }

    [Fact]
    public void A_time_beside_a_day_that_has_passed_does_not_rescue_it()
    {
        var ok = CreateParser().TryResolveDay("yesterday (6pm)", Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Contains("already passed", error);
    }

    [Theory]
    [InlineData("monday to friday")]
    [InlineData("jul 15 to jul 17")]
    [InlineData("jul 15-17")]
    [InlineData("yesterday to today")]
    public void A_spelled_out_range_still_counts_on_the_last_day_it_names(string text)
    {
        // Friday the 17th, 10:00 in Chicago. The recognizer hands back the 17th as the range's end;
        // read as exclusive — right for "this week", wrong here — the range would be over, and
        // "jul 15 to jul 17" would roll to next year.
        var friday = Instant.FromUtc(2026, 7, 17, 15, 0);

        var ok = CreateParser().TryResolveDay(text, Chicago, out var day, out var error, friday);

        Assert.True(ok, error);
        Assert.Equal(new LocalDate(2026, 7, 17), day);
    }

    [Fact]
    public void A_calendar_unit_ends_the_day_before_the_recognizers_exclusive_end()
    {
        // "this week" is Monday the 13th up to, not including, Monday the 20th.
        var parser = CreateParser();
        var sunday = Instant.FromUtc(2026, 7, 19, 15, 0);
        var nextMonday = Instant.FromUtc(2026, 7, 20, 15, 0);

        Assert.True(parser.TryResolveDay("this week", Chicago, out var lastDayOfTheWeek, out var error, sunday), error);
        Assert.Equal(new LocalDate(2026, 7, 19), lastDayOfTheWeek);

        // Asked for from the next Monday, last week is over.
        Assert.False(parser.TryResolveDay("last week", Chicago, out _, out error, nextMonday));
        Assert.Contains("already passed", error);
    }

    [Fact]
    public void A_quarter_is_a_calendar_unit_although_it_is_written_like_a_spelled_out_range()
    {
        // The recognizer gives a quarter a timex in parentheses, as it does "jul 15 to jul 17" —
        // but counted in months, and ending, exclusively, on the first day of the next quarter.
        var parser = CreateParser();
        var firstOfOctober = Instant.FromUtc(2026, 10, 1, 15, 0);

        // Mid-quarter it is under way, so it means today.
        Assert.True(parser.TryResolveDay("this quarter", Chicago, out var day, out var error), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);

        // On October 1 the quarter that ended the day before is over, not "ending today"…
        Assert.False(parser.TryResolveDay("last quarter", Chicago, out _, out error, firstOfOctober));
        Assert.Contains("already passed", error);

        // …and the one that begins that day is that day.
        Assert.True(parser.TryResolveDay("this quarter", Chicago, out day, out error, firstOfOctober), error);
        Assert.Equal(new LocalDate(2026, 10, 1), day);
    }

    [Theory]
    [InlineData("this week")]       // began on Monday the 13th
    [InlineData("this month")]      // began on the 1st
    [InlineData("july")]            // the month by name: this year's, not last year's or next
    public void A_range_already_under_way_means_from_today(string text)
    {
        var parser = CreateParser();

        Assert.True(parser.TryResolveDay(text, Chicago, out var day, out var error), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);

        // The instant-level resolver takes the range's start and refuses it as past.
        Assert.False(parser.TryResolve(text, Chicago, out _, out _));
    }

    [Fact]
    public void Today_stays_a_valid_day_until_it_ends()
    {
        // 23:30 in Chicago. TryResolve puts a bare date at 09:00, which is long past.
        var lateEvening = Instant.FromUtc(2026, 7, 16, 4, 30);
        var parser = CreateParser();

        Assert.True(parser.TryResolveDay("today", Chicago, out var day, out var error, lateEvening), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);
        Assert.False(parser.TryResolve("today", Chicago, out _, out _, lateEvening));
    }

    [Fact]
    public void A_day_is_the_callers_day_whatever_zone_the_text_names()
    {
        // 23:30 on the 15th in Chicago is already the 16th in UTC.
        var lateEvening = Instant.FromUtc(2026, 7, 16, 4, 30);
        var parser = CreateParser();

        // A zone abbreviation moves an instant (TryResolve); it does not move a day.
        Assert.True(parser.TryResolveDay("today UTC", Chicago, out var day, out var error, lateEvening), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);
        Assert.True(parser.TryResolveDay("jul 20 CST", DateTimeZone.Utc, out var named, out error, lateEvening), error);
        Assert.Equal(new LocalDate(2026, 7, 20), named);

        // The same text for a caller whose zone is UTC is that caller's today.
        Assert.True(parser.TryResolveDay("today", DateTimeZone.Utc, out var utcDay, out error, lateEvening), error);
        Assert.Equal(new LocalDate(2026, 7, 16), utcDay);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("last friday")]
    [InlineData("last week")]
    [InlineData("last 2 weeks")]    // counted in weeks, so a calendar unit: it ended before today
    [InlineData("july 1 2026")]
    public void Day_resolution_refuses_a_day_that_has_passed(string text)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Contains("already passed", error);
    }

    [Theory]
    [InlineData("banana hammock")]
    [InlineData("sat")]             // the recognizer reads mon–fri as weekdays, but not sat or sun
    public void Day_resolution_refuses_what_it_cannot_read(string text)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Could not understand", error);
        Assert.Contains(text, error);
    }
}
