using CalCrony.Api.Services;
using CalCrony.Contracts;
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
    public void Refuses_missing_text()
    {
        var ok = CreateParser().TryResolve(null, Chicago, out var result, out var error);

        Assert.False(ok);
        Assert.Equal(default, result);
        Assert.Equal("The date/time text is required.", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_or_blank_text_is_still_refused_as_not_understood(string text)
    {
        var parser = CreateParser();

        Assert.False(parser.TryResolve(text, Chicago, out _, out var error));
        Assert.Equal($"Could not understand \"{text}\" as a date/time.", error);

        Assert.False(parser.TryResolveDay(text, Chicago, out _, out error));
        Assert.StartsWith($"Could not understand \"{text}\" as a day.", error);
    }

    // Both length tests fill the same phrase out with dots, so the texts differ in length alone:
    // one character decides between "read" and "refused". The second phrase carries a zone
    // abbreviation, which is stripped before the recognizer runs — the limit counts the text as
    // typed, so it is refused all the same.
    [Theory]
    [InlineData("tomorrow 6pm")]
    [InlineData("tomorrow 6pm CST")]
    public void Reads_text_at_the_length_limit(string phrase)
    {
        var atLimit = phrase.PadRight(DateTextLimits.MaxLength, '.');

        var ok = CreateParser().TryResolve(atLimit, Chicago, out var result, out var error);

        Assert.True(ok, error);
        // Tomorrow (Jul 16) 18:00 CDT == 23:00 UTC.
        Assert.Equal(Instant.FromUtc(2026, 7, 16, 23, 0), result);
    }

    [Theory]
    [InlineData("tomorrow 6pm")]
    [InlineData("tomorrow 6pm CST")]
    public void Refuses_text_over_the_length_limit(string phrase)
    {
        var overLimit = phrase.PadRight(DateTextLimits.MaxLength + 1, '.');

        var ok = CreateParser().TryResolve(overLimit, Chicago, out var result, out var error);

        Assert.False(ok);
        Assert.Equal(default, result);
        // The refusal names the limit and, unlike the other errors, does not quote the text.
        Assert.Equal($"The date/time text must be at most {DateTextLimits.MaxLength} characters.", error);
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
    [InlineData("6pm GMT", 18)]
    [InlineData("6pm EST", 22)]
    [InlineData("6pm EDT", 22)]
    [InlineData("6pm CST", 23)]  // America/Chicago, CDT = UTC-5
    [InlineData("6pm CDT", 23)]
    [InlineData("6pm CT", 23)]
    [InlineData("6pm MST", 24)]  // America/Denver, MDT = UTC-6 → next day 00:00
    [InlineData("6pm MDT", 24)]
    [InlineData("6pm MT", 24)]
    [InlineData("6pm PST", 25)]
    [InlineData("6pm PT", 25)]
    [InlineData("6pm AKST", 26)] // America/Anchorage, AKDT = UTC-8 → next day 02:00
    [InlineData("6pm akdt", 26)]
    [InlineData("6pm Hst", 28)]  // Pacific/Honolulu, UTC-10 → next day 04:00
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

    [Theory]
    [InlineData("10am A\u212AST")]
    [InlineData("a\u212Adt 6pm")]
    [InlineData("A\u212AST")]
    [InlineData("10am A\u212AST CST")]
    [InlineData("sat 6pm A\u212AST")]
    public void Refuses_a_zone_abbreviation_it_has_no_zone_for(string text)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.False(ok);
        Assert.Equal(default, result);
        // The sentence for text that is not understood, with the text as typed.
        Assert.Equal($"Could not understand \"{text}\" as a date/time.", error);
    }

    [Fact]
    public void A_listed_abbreviation_that_comes_first_selects_the_zone_whatever_follows()
    {
        var ok = CreateParser().TryResolve("6pm CST A\u212AST", DateTimeZone.Utc, out var result, out var error);

        Assert.True(ok, error);
        // 18:00 in America/Chicago (CDT, UTC-5) == 23:00 UTC.
        Assert.Equal(Instant.FromUtc(2026, 7, 15, 23, 0), result);
    }

    [Fact]
    public void A_word_the_zone_pattern_does_not_take_is_left_to_the_recognizer()
    {
        var ok = CreateParser().TryResolve("6pm AEST", Chicago, out var result, out var error);

        Assert.True(ok, error);
        // Read in the caller's zone: 18:00 CDT == 23:00 UTC.
        Assert.Equal(Instant.FromUtc(2026, 7, 15, 23, 0), result);
    }

    [Fact]
    public void Day_resolution_looks_no_zone_up()
    {
        // A day is the caller's whatever zone the text names, so the day reader has no zone to refuse.
        var ok = CreateParser().TryResolveDay("tomorrow A\u212AST", Chicago, out var day, out var error);

        Assert.True(ok, error);
        Assert.Equal(new LocalDate(2026, 7, 16), day);
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
    public void Day_resolution_refuses_what_it_cannot_read(string text)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Could not understand", error);
        Assert.Contains(text, error);
    }

    [Fact]
    public void Day_resolution_refuses_missing_text()
    {
        var ok = CreateParser().TryResolveDay(null, Chicago, out var day, out var error);

        Assert.False(ok);
        Assert.Equal(default, day);
        Assert.Equal("The date/time text is required.", error);
    }

    // The day-level reader has the same length limit as TryResolve. As in its tests, both texts
    // are one phrase filled out with dots, so they differ in length alone.
    [Fact]
    public void Day_resolution_reads_text_at_the_length_limit()
    {
        var atLimit = "tomorrow".PadRight(DateTextLimits.MaxLength, '.');

        var ok = CreateParser().TryResolveDay(atLimit, Chicago, out var day, out var error);

        Assert.True(ok, error);
        Assert.Equal(new LocalDate(2026, 7, 16), day);
    }

    [Fact]
    public void Day_resolution_refuses_text_over_the_length_limit()
    {
        var overLimit = "tomorrow".PadRight(DateTextLimits.MaxLength + 1, '.');

        var ok = CreateParser().TryResolveDay(overLimit, Chicago, out var day, out var error);

        Assert.False(ok);
        Assert.Equal(default, day);
        // The same refusal as TryResolve's: it names the limit and does not quote the text.
        Assert.Equal($"The date/time text must be at most {DateTextLimits.MaxLength} characters.", error);
    }

    // ----- The short weekend days: "sat" and "sun" read as "saturday" and "sunday" do -----
    //
    // The recognizer reads mon–fri wherever they stand, but "sat" and "sun" only in some forms
    // ("next sat", "on sun"). Elsewhere it dropped them and read the rest, so "sat 6pm" was today
    // at 6 PM. The parser spells the two out before the recognizer is given the text.

    // Saturday the 18th at 10:00 and at 23:30, and Sunday the 19th at 10:00, all in Chicago.
    private static readonly Instant SaturdayMorning = Instant.FromUtc(2026, 7, 18, 15, 0);
    private static readonly Instant SaturdayLateEvening = Instant.FromUtc(2026, 7, 19, 4, 30);
    private static readonly Instant SundayMorning = Instant.FromUtc(2026, 7, 19, 15, 0);

    // A wall-clock hour in Chicago on a day of July 2026.
    private static Instant JulyInChicago(int day, int hour) =>
        new LocalDateTime(2026, 7, day, hour, 0).InZoneStrictly(Chicago).ToInstant();

    [Theory]
    [InlineData("sat", 18, 9)]              // a bare day is 09:00, as for every other day
    [InlineData("sun", 19, 9)]
    [InlineData("Sat", 18, 9)]
    [InlineData("SUN", 19, 9)]
    [InlineData("sat.", 18, 9)]
    [InlineData("sat 6pm", 18, 18)]         // was today at 6 PM: the day was dropped
    [InlineData("sun at noon", 19, 12)]
    [InlineData("6pm sat", 18, 18)]
    [InlineData("sat, 6pm", 18, 18)]
    [InlineData("sat morning", 18, 8)]
    [InlineData("sat or sun", 18, 9)]       // the earlier of the two…
    [InlineData("sun or sat", 18, 9)]       // …whichever comes first in the text: both are spelled out
    [InlineData("a week from sat", 25, 9)]
    [InlineData("on sat morning", 18, 8)]
    public void A_short_weekend_day_is_read_in_a_date_and_time(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    [Fact]
    public void A_short_weekend_day_is_read_beside_a_zone_abbreviation()
    {
        // The caller's zone is UTC; "CST" selects Chicago, where it is Wednesday at 10:00.
        var ok = CreateParser().TryResolve("sat 6pm CST", DateTimeZone.Utc, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(18, 18), result);
    }

    [Theory]
    [InlineData("sat", 18)]
    [InlineData("sun", 19)]
    [InlineData("sat 6pm", 18)]             // was today: only the time was read
    [InlineData("sun at noon", 19)]
    [InlineData("6pm sat", 18)]
    [InlineData("sat (6pm)", 18)]
    [InlineData("sat or sun", 18)]
    [InlineData("sun or sat", 18)]
    [InlineData("on sun night", 19)]
    public void A_short_weekend_day_is_read_as_a_day(string text, int julyDay)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out var day, out var error);

        Assert.True(ok, error);
        Assert.Equal(new LocalDate(2026, 7, julyDay), day);
    }

    [Theory]
    [InlineData("next sat", 25, 9)]
    [InlineData("this sun", 19, 9)]
    [InlineData("on sat", 18, 9)]
    [InlineData("on sat 6pm", 18, 18)]
    [InlineData("noon on sun", 19, 12)]
    [InlineData("sat jul 18", 18, 9)]
    [InlineData("sat next week", 25, 9)]
    public void Forms_the_recognizer_already_read_answer_as_before(string text, int julyDay, int hour)
    {
        var parser = CreateParser();

        Assert.True(parser.TryResolve(text, Chicago, out var result, out var error), error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
        Assert.True(parser.TryResolveDay(text, Chicago, out var day, out error), error);
        Assert.Equal(new LocalDate(2026, 7, julyDay), day);
    }

    [Fact]
    public void A_refusal_quotes_the_text_as_it_was_typed()
    {
        // Only the recognizer is given the spelled-out text.
        var parser = CreateParser();

        Assert.False(parser.TryResolve("last sat", Chicago, out _, out var error));
        Assert.Equal("\"last sat\" resolves to a time in the past.", error);
        Assert.False(parser.TryResolveDay("last sat", Chicago, out _, out error));
        Assert.Equal("\"last sat\" is a day that has already passed.", error);
    }

    // The other short weekdays, which the recognizer has always read, pinned for comparison.
    [Theory]
    [InlineData("mon", 20)]
    [InlineData("tue", 21)]
    [InlineData("tues", 21)]
    [InlineData("wed", 15)]         // it is Wednesday
    [InlineData("weds", 15)]
    [InlineData("thu", 16)]
    [InlineData("thur", 16)]
    [InlineData("thurs", 16)]
    [InlineData("fri", 17)]
    public void The_other_short_weekdays_read_as_days_as_before(string text, int julyDay)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out var day, out var error);

        Assert.True(ok, error);
        Assert.Equal(new LocalDate(2026, 7, julyDay), day);
    }

    [Theory]
    [InlineData("mon 6pm", 20, 18)]
    [InlineData("fri at noon", 17, 12)]
    public void The_other_short_weekdays_read_with_a_time_as_before(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    // The rule itself. "{day}" stands for the weekend day: once as "sat" or "sun", once as the
    // full name. Both readers must then give the same answer — the same instant or day, or the
    // same refusal — on a Wednesday, on the Saturday itself early and late, and on the Sunday.
    [Theory]
    [InlineData("{day}")]
    [InlineData("{day}.")]
    [InlineData("{day} 6pm")]
    [InlineData("{day} 9am")]
    [InlineData("{day} at noon")]
    [InlineData("6pm {day}")]
    [InlineData("{day}, 6pm")]
    [InlineData("{day} (6pm)")]
    [InlineData("{day} morning")]
    [InlineData("{day} night")]
    [InlineData("on {day}")]
    [InlineData("on {day} morning")]
    [InlineData("next {day}")]
    [InlineData("this {day}")]
    [InlineData("last {day}")]
    [InlineData("a week from {day}")]
    [InlineData("{day} next week")]
    [InlineData("{day} jul 25")]
    [InlineData("{day} or mon")]
    [InlineData("fri or {day}")]
    [InlineData("{day} down")]
    [InlineData("the {day}")]
    [InlineData("by {day}")]
    [InlineData("by {day} 6pm")]
    [InlineData("until {day} 6pm")]
    [InlineData("{day} by 6pm")]
    [InlineData("2 hours before {day} 6pm")]
    [InlineData("every {day} 8pm")]
    [InlineData("{day} 8pm every week")]
    [InlineData("{day}. 6pm")]
    [InlineData("{day} from 6pm")]
    [InlineData("{day} 6pm, ends 9pm")]
    [InlineData("mon to {day}")]
    public void A_short_weekend_day_reads_exactly_as_its_full_name(string phrase)
    {
        var parser = CreateParser();

        foreach (var (shortName, fullName) in new[] { ("sat", "saturday"), ("sun", "sunday") })
        {
            var typed = phrase.Replace("{day}", shortName, StringComparison.Ordinal);
            var spelledOut = phrase.Replace("{day}", fullName, StringComparison.Ordinal);

            foreach (var moment in new[] { Now, SaturdayMorning, SaturdayLateEvening, SundayMorning })
            {
                // The text and the moment ride along in both tuples, so a failure names its case.
                var read = parser.TryResolve(typed, Chicago, out var result, out var error, moment);
                var fullRead = parser.TryResolve(spelledOut, Chicago, out var fullResult, out var fullError, moment);
                Assert.Equal(
                    (typed, moment, fullRead, fullResult, QuotingTheTextAsTyped(fullError, spelledOut, typed)),
                    (typed, moment, read, result, error));

                var readDay = parser.TryResolveDay(typed, Chicago, out var day, out var dayError, moment);
                var fullReadDay = parser.TryResolveDay(spelledOut, Chicago, out var fullDay, out var fullDayError, moment);
                Assert.Equal(
                    (typed, moment, fullReadDay, fullDay, QuotingTheTextAsTyped(fullDayError, spelledOut, typed)),
                    (typed, moment, readDay, day, dayError));
            }
        }
    }

    // The refusal the full name gets, as it reads for the text as typed. Every refusal quotes the
    // text once, ahead of anything else it puts in quotation marks.
    private static string? QuotingTheTextAsTyped(string? error, string spelledOut, string typed)
    {
        if (error is null)
        {
            return null;
        }

        var quoted = $"\"{spelledOut}\"";
        var at = error.IndexOf(quoted, StringComparison.Ordinal);
        return at < 0 ? error : string.Concat(error.AsSpan(0, at), $"\"{typed}\"", error.AsSpan(at + quoted.Length));
    }

    // Reading as the full name does also means being refused where the full name is. Before, the
    // short form was dropped and the rest of the text was read: each of these was today at the
    // time it names. (A deadline, "by sat 6pm", and a repeat, "every sat 8pm", stood here until
    // they got answers of their own: see the deadlines and the repeats further down.)
    [Theory]
    [InlineData("2 hours before sat 6pm")]      // as "2 hours before saturday 6pm": a length of time before a deadline
    [InlineData("30 minutes before sun noon")]
    public void Where_the_full_name_is_not_understood_neither_is_the_short_form(string text)
    {
        var parser = CreateParser();

        Assert.False(parser.TryResolve(text, Chicago, out _, out var error));
        Assert.Equal($"Could not understand \"{text}\" as a date/time.", error);
        Assert.False(parser.TryResolveDay(text, Chicago, out _, out error));
        Assert.StartsWith($"Could not understand \"{text}\" as a day.", error);
    }

    [Fact]
    public void A_range_that_ends_on_a_short_weekend_day_is_read_as_a_range()
    {
        // On a Wednesday "mon to sat" is a range under way, as "monday to saturday" and "mon to
        // fri" are: no instant, because its start has passed, and today as a day. Before, "sat"
        // was dropped and "mon" was read alone: next Monday.
        var parser = CreateParser();

        Assert.False(parser.TryResolve("mon to sat", Chicago, out _, out var error));
        Assert.Equal("\"mon to sat\" resolves to a time in the past.", error);
        Assert.True(parser.TryResolveDay("mon to sat", Chicago, out var day, out error), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);
    }

    // The recognizer refuses these whether or not the letters in them are spelled out, so this
    // shows only that they are still refused. What holds the two word boundaries in place is
    // Only_a_word_of_its_own_is_spelled_out_and_takes_up_room, further down.
    [Theory]
    [InlineData("sunrise")]
    [InlineData("sunny")]
    [InlineData("sundown")]
    [InlineData("satisfy")]
    [InlineData("saturn")]
    [InlineData("sats")]
    [InlineData("suns")]
    [InlineData("sat6pm")]
    public void A_longer_word_that_holds_the_letters_is_still_refused(string text)
    {
        var parser = CreateParser();

        Assert.False(parser.TryResolve(text, Chicago, out _, out var error));
        Assert.Equal($"Could not understand \"{text}\" as a date/time.", error);
        Assert.False(parser.TryResolveDay(text, Chicago, out _, out error));
        Assert.StartsWith($"Could not understand \"{text}\" as a day.", error);
    }

    [Theory]
    [InlineData("sat down", 18)]
    [InlineData("sun up", 19)]
    [InlineData("the sun", 19)]
    [InlineData("sat-nav", 18)]
    [InlineData("mon down", 20)]        // for comparison: read as Monday before this change too
    public void A_short_weekend_day_counts_wherever_it_stands_as_a_word_of_its_own(string text, int julyDay)
    {
        // The recognizer skips the words it does not read, so "mon down" has always been Monday.
        // "sat" and "sun" follow that same rule, not a list of the words that may stand beside
        // them: the short form and the full name can then never read differently.
        var parser = CreateParser();

        Assert.True(parser.TryResolveDay(text, Chicago, out var day, out var error), error);
        Assert.Equal(new LocalDate(2026, 7, julyDay), day);
        Assert.True(parser.TryResolve(text, Chicago, out var result, out error), error);
        Assert.Equal(JulyInChicago(julyDay, 9), result);
    }

    [Fact]
    public void On_the_day_itself_the_short_form_reads_as_the_full_name_does()
    {
        var parser = CreateParser();

        Assert.True(parser.TryResolveDay("sat", Chicago, out var day, out var error, SaturdayMorning), error);
        Assert.Equal(new LocalDate(2026, 7, 18), day);
        Assert.True(parser.TryResolve("sat 6pm", Chicago, out var result, out error, SaturdayMorning), error);
        Assert.Equal(JulyInChicago(18, 18), result);

        // As a date and time a bare day is 09:00, which has passed, and for a weekday named on its
        // own day the recognizer offers last week's and today's. The reader adds next week's, so
        // "sat" is the Saturday after, as "saturday" is.
        Assert.True(parser.TryResolve("sat", Chicago, out result, out error, SaturdayMorning), error);
        Assert.Equal(JulyInChicago(25, 9), result);

        // The same for a time already behind. With the day dropped, this was tomorrow at 9 AM.
        Assert.True(parser.TryResolve("sat 9am", Chicago, out result, out error, SaturdayMorning), error);
        Assert.Equal(JulyInChicago(25, 9), result);

        // Late that evening 6 PM is behind too.
        Assert.True(parser.TryResolve("sat 6pm", Chicago, out result, out error, SaturdayLateEvening), error);
        Assert.Equal(JulyInChicago(25, 18), result);
    }

    // Spelling out lengthens the text: "sat" by five characters, "sun" by three. The recognizer is
    // still never given more than the limit: text that spelling out would take past it is read as
    // typed. As in the length tests above, the texts are one phrase filled out with dots.
    [Fact]
    public void A_short_weekend_day_is_spelled_out_only_while_the_text_stays_within_the_limit()
    {
        var parser = CreateParser();
        // Exactly at the limit once "sat" is spelled out…
        var fits = "sat 6pm".PadRight(DateTextLimits.MaxLength - 5, '.');
        // …and one character over it: read as typed, so the time alone, today.
        var doesNotFit = "sat 6pm".PadRight(DateTextLimits.MaxLength - 4, '.');

        Assert.True(parser.TryResolve(fits, Chicago, out var result, out var error), error);
        Assert.Equal(JulyInChicago(18, 18), result);
        Assert.True(parser.TryResolveDay(fits, Chicago, out var day, out error), error);
        Assert.Equal(new LocalDate(2026, 7, 18), day);

        Assert.True(parser.TryResolve(doesNotFit, Chicago, out result, out error), error);
        Assert.Equal(JulyInChicago(15, 18), result);
        Assert.True(parser.TryResolveDay(doesNotFit, Chicago, out day, out error), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);
    }

    [Fact]
    public void Text_with_two_short_weekend_days_is_spelled_out_in_full_or_not_at_all()
    {
        var parser = CreateParser();
        // Exactly at the limit with both spelled out: "sat" grows by five characters, "sun" by three…
        var fits = "sat or sun".PadRight(DateTextLimits.MaxLength - 8, '.');
        // …and one character over it. The one that would still fit is not spelled out on its
        // own: the text is read as typed, and as typed neither short form is read.
        var doesNotFit = "sat or sun".PadRight(DateTextLimits.MaxLength - 7, '.');

        Assert.True(parser.TryResolve(fits, Chicago, out var result, out var error), error);
        Assert.Equal(JulyInChicago(18, 9), result);
        Assert.True(parser.TryResolveDay(fits, Chicago, out var day, out error), error);
        Assert.Equal(new LocalDate(2026, 7, 18), day);

        Assert.False(parser.TryResolve(doesNotFit, Chicago, out _, out error));
        Assert.Equal($"Could not understand \"{doesNotFit}\" as a date/time.", error);
        Assert.False(parser.TryResolveDay(doesNotFit, Chicago, out _, out error));
        Assert.StartsWith($"Could not understand \"{doesNotFit}\" as a day.", error);
    }

    [Theory]
    [InlineData("landsat")]     // ends in the letters
    [InlineData("sunrise")]     // begins with them
    public void Only_a_word_of_its_own_is_spelled_out_and_takes_up_room(string longerWord)
    {
        // 95 characters. With "sat" spelled out the recognizer is given exactly the limit. Were
        // part of the longer word spelled out as well, that would be over it, and the text would
        // be read as typed: today at 6 PM.
        var text = "sat 6pm".PadRight(DateTextLimits.MaxLength - 5 - longerWord.Length, '.') + longerWord;

        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(18, 18), result);
    }

    [Fact]
    public void Text_over_the_length_limit_is_refused_with_a_short_weekend_day_in_it_too()
    {
        var parser = CreateParser();
        var overLimit = "sat 6pm".PadRight(DateTextLimits.MaxLength + 1, '.');
        var tooLong = $"The date/time text must be at most {DateTextLimits.MaxLength} characters.";

        Assert.False(parser.TryResolve(overLimit, Chicago, out _, out var error));
        Assert.Equal(tooLong, error);
        Assert.False(parser.TryResolveDay(overLimit, Chicago, out _, out error));
        Assert.Equal(tooLong, error);
    }

    // ----- Deadlines, repeats, a weekday on its own day, a time apart from its day -----
    //
    // The reader works on what the recognizer returns: one or more phrases, each with one or more
    // values. Four things it returned went unread or were read on the wrong day. Each has a rule
    // of its own in the parser and a group of tests here.

    // Friday the 17th at 10:00 in Chicago.
    private static readonly Instant FridayMorning = Instant.FromUtc(2026, 7, 17, 15, 0);

    // The fixture's Wednesday, the 15th, at another time of day in Chicago.
    private static Instant WednesdayAt(int hour, int minute) =>
        new LocalDateTime(2026, 7, 15, hour, minute).InZoneStrictly(Chicago).ToInstant();

    // A day of July 2026, on the hour, in Chicago.
    private static Instant JulyAt(int day, int hour) =>
        new LocalDateTime(2026, 7, day, hour, 0).InZoneStrictly(Chicago).ToInstant();

    // --- 1. A deadline: "by", "until" or "before" in front of a date ---
    //
    // The recognizer returns a range with an end and no start. The reader takes the end, as it has
    // always taken the start of "after friday 6pm": the text reads as the date and time it names.

    [Theory]
    [InlineData("by saturday 6pm", 18, 18)]
    [InlineData("by sat 6pm", 18, 18)]
    [InlineData("until sun 18:00", 19, 18)]
    [InlineData("before sat at noon", 18, 12)]
    [InlineData("by friday", 17, 9)]                // a bare day is 09:00, as "friday" is
    [InlineData("until friday", 17, 9)]
    [InlineData("till friday", 17, 9)]
    [InlineData("no later than friday", 17, 9)]
    [InlineData("on or before friday", 17, 9)]
    [InlineData("by 6pm", 15, 18)]                  // a time alone is today's…
    [InlineData("by 9am", 16, 9)]                   // …or tomorrow's once today's has passed, as "9am" is
    [InlineData("before noon", 15, 12)]
    [InlineData("by tomorrow 6pm", 16, 18)]
    [InlineData("by 2026-07-20 18:00", 20, 18)]
    [InlineData("by 6pm friday", 17, 18)]
    [InlineData("friday before 6pm", 17, 18)]
    [InlineData("friday by 6pm", 17, 18)]
    [InlineData("friday until 6pm", 17, 18)]
    [InlineData("by friday night", 17, 20)]         // as "friday night": a night begins at 8 PM
    [InlineData("rsvp by friday 6pm", 17, 18)]
    [InlineData("friday, by 6pm", 17, 18)]          // was Friday at 09:00: the time was dropped
    [InlineData("by friday (6pm)", 17, 18)]         // was today at 6 PM
    public void A_deadline_reads_as_the_date_and_time_it_names(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    [Fact]
    public void A_deadline_at_the_end_of_a_day_is_that_days_last_second()
    {
        var parser = CreateParser();

        // With no day beside it, "end of day" is today's.
        Assert.True(parser.TryResolve("by end of day", Chicago, out var result, out var error), error);
        Assert.Equal(new LocalDateTime(2026, 7, 15, 23, 59, 59).InZoneStrictly(Chicago).ToInstant(), result);

        Assert.True(parser.TryResolve("by the end of friday", Chicago, out result, out error), error);
        Assert.Equal(new LocalDateTime(2026, 7, 17, 23, 59, 59).InZoneStrictly(Chicago).ToInstant(), result);
    }

    // The rule itself: the word in front changes nothing. Each date is read with the word and
    // without it, and the two answers must be the same — the same instant, or the same refusal —
    // on the Wednesday early, mid-morning and late, and on the Saturday in the morning and late.
    // The one exception is a date with no year, typed on that very day: as a deadline it is
    // today's (see the deadlines that have passed, below), where the date alone also offers next
    // year's.
    [Theory]
    [InlineData("friday")]
    [InlineData("friday 6pm")]
    [InlineData("friday night")]
    [InlineData("saturday")]
    [InlineData("sat 6pm")]
    [InlineData("wednesday")]
    [InlineData("wed 9am")]
    [InlineData("next friday")]
    [InlineData("6pm")]
    [InlineData("noon")]
    [InlineData("today")]
    [InlineData("today 6pm")]
    [InlineData("tomorrow")]
    [InlineData("tomorrow 6pm")]
    [InlineData("yesterday")]
    [InlineData("nov 12")]
    [InlineData("nov 12 6pm")]
    [InlineData("2026-07-20 18:00")]
    public void The_word_in_front_of_a_deadline_changes_nothing(string date)
    {
        var parser = CreateParser();

        foreach (var word in new[] { "by", "until", "before" })
        {
            var deadline = $"{word} {date}";

            foreach (var moment in new[] { WednesdayAt(0, 30), Now, WednesdayAt(23, 30), SaturdayMorning, SaturdayLateEvening })
            {
                // The text and the moment ride along in both tuples, so a failure names its case.
                var read = parser.TryResolve(deadline, Chicago, out var result, out var error, moment);
                var plainRead = parser.TryResolve(date, Chicago, out var plainResult, out var plainError, moment);
                Assert.Equal(
                    (deadline, moment, plainRead, plainResult, QuotingTheTextAsTyped(plainError, date, deadline)),
                    (deadline, moment, read, result, error));
            }
        }
    }

    // At the edge of a whole period the recognizer's end is the period's first day, or the day
    // after its last. Read as a bare date that would be 09:00 on a day the text does not mean.
    // "late" and "early" make an edge of a single day, which the recognizer marks differently
    // from a plain "by".
    [Theory]
    [InlineData("by the weekend")]
    [InlineData("before next month")]
    [InlineData("by end of month")]
    [InlineData("until next week")]
    [InlineData("until 2027")]
    [InlineData("by late friday")]
    [InlineData("by early friday")]
    public void A_deadline_at_the_edge_of_a_period_is_still_not_understood(string text)
    {
        var parser = CreateParser();

        Assert.False(parser.TryResolve(text, Chicago, out _, out var error));
        Assert.Equal($"Could not understand \"{text}\" as a date/time.", error);
        Assert.False(parser.TryResolveDay(text, Chicago, out _, out error));
        Assert.StartsWith($"Could not understand \"{text}\" as a day.", error);
    }

    // Read by its end, each of these would be the time it ends on, with the length lost.
    [Theory]
    [InlineData("2 hours before friday 6pm")]
    [InlineData("30 minutes before 6pm")]
    [InlineData("an hour before noon")]
    public void A_deadline_beside_a_length_of_time_is_still_not_understood(string text)
    {
        var parser = CreateParser();

        Assert.False(parser.TryResolve(text, Chicago, out _, out var error));
        Assert.Equal($"Could not understand \"{text}\" as a date/time.", error);
        Assert.False(parser.TryResolveDay(text, Chicago, out _, out error));
        Assert.StartsWith($"Could not understand \"{text}\" as a day.", error);
    }

    [Fact]
    public void A_length_of_time_beside_a_date_that_is_not_a_deadline_changes_nothing()
    {
        var ok = CreateParser().TryResolve("friday 6pm for 2 hours", Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(17, 18), result);
    }

    [Theory]
    [InlineData("by today")]            // as "today" at 10:00: its 09:00 is behind
    [InlineData("by yesterday")]
    [InlineData("until last friday")]
    [InlineData("by jul 15")]           // today's date: a deadline that can mean today is today's…
    [InlineData("until jul 15")]        // …where "jul 15" alone is July 15 of next year, as before
    public void A_deadline_that_has_passed_is_refused_as_past(string text)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Equal($"\"{text}\" resolves to a time in the past.", error);
    }

    // The day reader's caller asks for a first day, and a deadline names a last one.
    [Theory]
    [InlineData("by friday")]
    [InlineData("until saturday")]
    [InlineData("by sat 6pm")]
    [InlineData("until sun 18:00")]
    [InlineData("before sat at noon")]
    [InlineData("friday before 6pm")]
    public void The_day_reader_does_not_take_a_deadline_for_a_first_day(string text)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.StartsWith($"Could not understand \"{text}\" as a day.", error);
    }

    // --- 2. A repeat ---
    //
    // The recognizer marks a repeat as a kind of its own, with no date in it. It is refused in
    // words that say so, by both readers, wherever it stands: beside a date that could be read
    // as well, where reading the rest would drop the repeat and say nothing.

    [Theory]
    [InlineData("every saturday 8pm")]
    [InlineData("every sat 8pm")]
    [InlineData("every friday")]
    [InlineData("each friday")]
    [InlineData("fridays at 8pm")]
    [InlineData("every day at 6pm")]
    [InlineData("every other friday")]
    [InlineData("every weekday")]
    [InlineData("every 2 weeks")]
    [InlineData("daily")]
    [InlineData("weekly")]
    [InlineData("monthly")]
    [InlineData("friday 8pm every week")]           // was Friday at 8 PM, once
    [InlineData("friday 8pm weekly")]
    [InlineData("weekly on friday")]                // was Friday at 09:00, once
    [InlineData("every friday starting july 24")]   // was July 24 at 09:00, once
    public void Text_that_repeats_is_refused_in_words_that_say_so(string text)
    {
        var parser = CreateParser();

        Assert.False(parser.TryResolve(text, Chicago, out var result, out var error));
        Assert.Equal(default, result);
        Assert.Equal(
            $"\"{text}\" repeats. Type one date and time here, for example \"saturday 8pm\"; to make an event repeat, use its repeat option.",
            error);
        Assert.False(parser.TryResolveDay(text, Chicago, out var day, out error));
        Assert.Equal(default, day);
        Assert.Equal($"\"{text}\" repeats. Type one day here, for example \"today\", \"saturday\" or \"nov 12\".", error);
    }

    [Fact]
    public void The_refusal_for_a_repeat_quotes_what_the_other_refusals_quote()
    {
        // TryResolve quotes the text without the zone abbreviation that chose the zone, as it does
        // in its other refusals; TryResolveDay quotes the text as typed.
        var parser = CreateParser();

        Assert.False(parser.TryResolve("every friday 8pm CST", DateTimeZone.Utc, out _, out var error));
        Assert.StartsWith("\"every friday 8pm\" repeats.", error);
        Assert.False(parser.TryResolveDay("every friday 8pm CST", DateTimeZone.Utc, out _, out error));
        Assert.StartsWith("\"every friday 8pm CST\" repeats.", error);
    }

    // --- 3. A weekday typed on its own day ---
    //
    // For a weekday with no week named the recognizer offers last week's and this week's. On the
    // day itself this week's is today, so once its time is behind — 09:00 when none is typed —
    // both have passed. The reader adds next week's: the text means the next time that weekday and
    // time come round, as a time alone ("9am" at 10:00) already means tomorrow's.

    [Theory]
    [InlineData("wed", 0, 30, 15, 9)]
    [InlineData("wed", 8, 59, 15, 9)]
    [InlineData("wed", 10, 0, 22, 9)]               // was refused as past, from 09:00 until midnight
    [InlineData("wed", 23, 30, 22, 9)]
    [InlineData("wednesday", 10, 0, 22, 9)]
    [InlineData("on wednesday", 10, 0, 22, 9)]
    [InlineData("wed 9am", 8, 59, 15, 9)]
    [InlineData("wed 9am", 10, 0, 22, 9)]
    [InlineData("wed 6pm", 10, 0, 15, 18)]          // still ahead: today, as before
    [InlineData("wed 6pm", 23, 30, 22, 18)]
    [InlineData("wednesday at 6pm", 23, 30, 22, 18)]
    [InlineData("wednesday night", 10, 0, 15, 20)]
    [InlineData("wednesday night", 23, 30, 22, 20)]
    [InlineData("wednesday morning", 10, 0, 22, 8)] // the morning began at 8
    [InlineData("wed 6pm-8pm", 23, 30, 22, 18)]     // a span of times on the day
    [InlineData("by wed 9am", 10, 0, 22, 9)]        // a deadline on the day follows the same rule
    [InlineData("next wednesday", 10, 0, 22, 9)]    // as before
    [InlineData("thu", 10, 0, 16, 9)]               // the day after: tomorrow, as before
    [InlineData("tue", 10, 0, 21, 9)]               // the day before: next week's, as before
    public void A_weekday_typed_on_its_own_day_is_next_weeks_once_its_time_is_behind(
        string text, int atHour, int atMinute, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error, WednesdayAt(atHour, atMinute));

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    [Theory]
    [InlineData("this wednesday")]      // names this week's
    [InlineData("today")]
    [InlineData("today 9am")]
    [InlineData("jul 15 9am")]          // a date, not a weekday
    [InlineData("mon to sat")]          // a span of days that has begun
    public void What_names_today_or_a_span_of_days_is_still_refused_as_past(string text)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Equal($"\"{text}\" resolves to a time in the past.", error);
    }

    [Fact]
    public void A_weekday_with_a_day_of_the_month_is_not_moved_a_week()
    {
        // "fri 17" is Friday the 17th, not any Friday. On that Friday the recognizer writes it as
        // that date, so its week is not left open: at 10:00 its 09:00 is behind, and it is not read
        // as Friday the 24th. The day after, it is the next Friday the 17th, in September of 2027.
        var parser = CreateParser();

        Assert.False(parser.TryResolve("fri 17", Chicago, out _, out var error, FridayMorning));
        Assert.Equal("\"fri 17\" resolves to a time in the past.", error);
        Assert.True(parser.TryResolve("fri 17", Chicago, out var result, out error, SaturdayMorning), error);
        Assert.Equal(new LocalDateTime(2027, 9, 17, 9, 0).InZoneStrictly(Chicago).ToInstant(), result);
    }

    [Fact]
    public void A_span_of_days_that_begins_today_is_not_moved_a_week()
    {
        // "fri-sun" typed on that Friday at 10:00: its start, Friday at 09:00, has passed. A span
        // of days is left as it was: refused as past here, and today from the day reader.
        var parser = CreateParser();

        Assert.False(parser.TryResolve("fri-sun", Chicago, out _, out var error, FridayMorning));
        Assert.Equal("\"fri-sun\" resolves to a time in the past.", error);
        Assert.True(parser.TryResolveDay("fri-sun", Chicago, out var day, out error, FridayMorning), error);
        Assert.Equal(new LocalDate(2026, 7, 17), day);

        // The same for a span written with times, from one weekday to another, once its start is
        // behind: it is not moved to next week.
        Assert.False(parser.TryResolve("fri 6pm to sun 6pm", Chicago, out _, out error, JulyAt(17, 19)));
        Assert.Equal("\"fri 6pm to sun 6pm\" resolves to a time in the past.", error);
        Assert.False(parser.TryResolve("sat 10am - sun 4pm", Chicago, out _, out error, JulyAt(18, 12)));
        Assert.Equal("\"sat 10am - sun 4pm\" resolves to a time in the past.", error);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(8, 59)]
    [InlineData(10, 0)]
    [InlineData(23, 30)]
    public void For_the_day_reader_a_weekday_typed_on_its_own_day_is_today_at_any_hour(int atHour, int atMinute)
    {
        var parser = CreateParser();
        var moment = WednesdayAt(atHour, atMinute);

        Assert.True(parser.TryResolveDay("wed", Chicago, out var day, out var error, moment), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);
        Assert.True(parser.TryResolveDay("wed 6pm", Chicago, out day, out error, moment), error);
        Assert.Equal(new LocalDate(2026, 7, 15), day);
    }

    [Fact]
    public void A_weekday_named_beside_a_later_date_is_the_earlier_of_the_two_on_its_own_day_too()
    {
        // On any other day "wednesday or aug 20" is the coming Wednesday. On the Wednesday itself,
        // after 09:00, it was August 20, for want of a Wednesday that was still ahead.
        var ok = CreateParser().TryResolve("wednesday or aug 20", Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(22, 9), result);
    }

    // --- 4. A time written apart from its day ---
    //
    // The recognizer joins a day and a time across a space, a comma, "at", " - ", "@" and
    // "around". Across anything else it returns two phrases, a day and a time. Read one by one
    // they were the day at 09:00 and the time today, and the earliest ahead was nearly always the
    // time, today. The day reader's rule holds here now: a day the text names outranks a today
    // that is only implied.

    [Theory]
    [InlineData("fri (6pm)", 17, 18)]
    [InlineData("fri. 6pm", 17, 18)]
    [InlineData("friday from 6pm", 17, 18)]
    [InlineData("friday: 6pm", 17, 18)]
    [InlineData("friday; 6pm", 17, 18)]
    [InlineData("friday / 6pm", 17, 18)]
    [InlineData("friday about 6pm", 17, 18)]
    [InlineData("friday starting at 6pm", 17, 18)]
    [InlineData("6pm (friday)", 17, 18)]
    [InlineData("6pm. friday", 17, 18)]
    [InlineData("fri (9am)", 17, 9)]                    // was tomorrow at 9 AM
    [InlineData("tomorrow (6pm)", 16, 18)]
    [InlineData("7/20 (6pm)", 20, 18)]
    [InlineData("sat july 18 (6pm)", 18, 18)]
    [InlineData("next week (6pm)", 20, 18)]             // a range is its first day
    [InlineData("jul 20 to jul 31 (6pm)", 20, 18)]      // a span the text spells out too
    [InlineData("friday (evening)", 17, 16)]
    [InlineData("friday at night", 17, 20)]
    [InlineData("friday (6pm-8pm)", 17, 18)]
    [InlineData("friday or saturday (6pm)", 17, 18)]    // the earlier of the days named
    [InlineData("friday (6pm or 8pm)", 17, 18)]         // the earlier of the times
    [InlineData("mon down at 6pm", 20, 18)]             // the words between do not matter
    public void A_time_written_apart_from_its_day_is_read_on_that_day(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    [Fact]
    public void A_time_apart_is_read_on_a_date_in_another_month_and_on_a_holiday()
    {
        var parser = CreateParser();

        Assert.True(parser.TryResolve("nov 12 (6pm)", Chicago, out var result, out var error), error);
        Assert.Equal(new LocalDateTime(2026, 11, 12, 18, 0).InZoneStrictly(Chicago).ToInstant(), result);

        // The recognizer never joins a holiday and a time. Easter Sunday of 2027 is March 28.
        Assert.True(parser.TryResolve("easter 6pm", Chicago, out result, out error), error);
        Assert.Equal(new LocalDateTime(2027, 3, 28, 18, 0).InZoneStrictly(Chicago).ToInstant(), result);
    }

    // "end of day" names no day either: the recognizer assumes today and writes today's last
    // second. Beside a day it is that day's last second. Each of these was today at 23:59:59,
    // except the two with "by", which were Friday at 09:00, and the last two, which are as before.
    [Theory]
    [InlineData("eod friday", 17)]
    [InlineData("friday eod", 17)]
    [InlineData("friday end of day", 17)]
    [InlineData("by eod friday", 17)]
    [InlineData("by end of day friday", 17)]
    [InlineData("tomorrow eod", 16)]
    [InlineData("eod", 15)]
    [InlineData("end of day", 15)]
    public void The_end_of_day_is_read_on_the_day_the_text_names(string text, int julyDay)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(new LocalDateTime(2026, 7, julyDay, 23, 59, 59).InZoneStrictly(Chicago).ToInstant(), result);
    }

    // Where the day already has its time, a further time is not read. As a time alone it would be
    // today's, and each of these was today at the further time.
    [Theory]
    [InlineData("friday 6pm, ends 9pm", 17, 18)]        // was today at 9 PM
    [InlineData("friday 6pm (doors 5:30)", 17, 18)]     // was today at 5:30 PM
    [InlineData("friday 6pm or 8pm", 17, 18)]           // was today at 8 PM
    [InlineData("friday 8pm or 6pm", 17, 20)]
    [InlineData("tomorrow 6pm or 8pm", 16, 18)]
    [InlineData("6pm or friday 8pm", 17, 20)]           // was today at 6 PM
    public void A_further_time_beside_a_day_that_has_its_own_is_not_read(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    // A part of the day ("friday night", "tonight") takes a time written beside it as its own, as
    // it does when the two are written together ("friday night 9pm").
    [Theory]
    [InlineData("friday night (9pm)", 17, 21)]          // was today at 9 PM
    [InlineData("saturday night @ 10pm", 18, 22)]       // was today at 10 PM
    [InlineData("tomorrow evening (7pm)", 16, 19)]      // was today at 7 PM
    [InlineData("by friday night (11pm)", 17, 23)]      // was today at 11 PM
    [InlineData("game night friday 7pm", 17, 19)]       // "night friday" is read as Friday night; was today at 7 PM
    [InlineData("tomorrow morning (7am)", 16, 7)]       // as before
    [InlineData("tonight (7pm)", 15, 19)]               // as before
    [InlineData("tonight (9pm)", 15, 21)]               // was today at 8 PM, when the night begins
    [InlineData("tonight, ends 11pm", 15, 23)]          // even when the time is an end: it was today at 8 PM
    public void A_part_of_the_day_takes_a_time_written_beside_it_as_its_own(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    [Theory]
    [InlineData("friday night", 17, 20)]                // a night begins at 8 PM
    [InlineData("tomorrow morning", 16, 8)]             // a morning at 8 AM
    [InlineData("tonight", 15, 20)]
    [InlineData("by friday night", 17, 20)]
    public void A_part_of_the_day_with_no_time_beside_it_begins_at_its_start(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    [Fact]
    public void A_further_time_in_another_zone_is_not_read_either()
    {
        // "est" chooses the zone and is taken out; "4pm pst" is a further time. Friday at 7 PM
        // Eastern is 23:00 UTC. It was today at 4 PM Eastern.
        var ok = CreateParser().TryResolve("friday 7pm est / 4pm pst", Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(Instant.FromUtc(2026, 7, 17, 23, 0), result);
    }

    [Fact]
    public void Two_times_and_a_day_apart_from_both_are_the_earlier_time_on_that_day()
    {
        // Which of the two was meant cannot be told; the day can.
        var parser = CreateParser();

        Assert.True(parser.TryResolve("friday. doors 6pm, start 7pm", Chicago, out var result, out var error), error);
        Assert.Equal(JulyInChicago(17, 18), result);

        // "ET" chooses the zone. 3 PM Eastern on the Friday is 19:00 UTC.
        Assert.True(parser.TryResolve("6pm ET / 3pm PT friday", Chicago, out result, out error), error);
        Assert.Equal(Instant.FromUtc(2026, 7, 17, 19, 0), result);
    }

    [Fact]
    public void Two_times_offered_as_a_choice_on_the_day_itself_are_the_first_of_them()
    {
        // On the Friday at 10:00, "friday 9am or 6pm" was today at 6 PM: the day's 9 AM had passed
        // and "6pm" alone was today's. The further time is not read now, so the text is
        // "friday 9am", and that is next week's. The same text with "today" is refused as past.
        var parser = CreateParser();

        Assert.True(parser.TryResolve("friday 9am or 6pm", Chicago, out var result, out var error, FridayMorning), error);
        Assert.Equal(JulyInChicago(24, 9), result);
        Assert.False(parser.TryResolve("today 9am or 6pm", Chicago, out _, out error));
        Assert.Equal("\"today 9am or 6pm\" resolves to a time in the past.", error);
    }

    // A range that has begun and not ended means the rest of it and names no day, as the day
    // reader has it: "this week" on a Wednesday, "tonight" once the night has begun. A time
    // beside it stays a time alone, today's or tomorrow's, as it was before.
    [Theory]
    [InlineData("this week 6pm", 10, 0, 15, 18)]
    [InlineData("this week (6pm)", 10, 0, 15, 18)]
    [InlineData("this week 6pm", 23, 30, 16, 18)]           // today's has passed: tomorrow's
    [InlineData("this month 6pm", 10, 0, 15, 18)]
    [InlineData("mon to sat 6pm", 10, 0, 15, 18)]
    [InlineData("tonight (9pm)", 20, 30, 15, 21)]           // the night began at 8
    [InlineData("this afternoon (3pm)", 13, 0, 15, 15)]     // the afternoon began at noon
    [InlineData("this morning (11am)", 10, 0, 15, 11)]
    [InlineData("wednesday night (9pm)", 22, 0, 16, 21)]    // the night began at 8 and 9 PM has passed: tomorrow's
    public void A_time_beside_a_range_that_is_under_way_is_a_time_alone_as_before(
        string text, int atHour, int atMinute, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error, WednesdayAt(atHour, atMinute));

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    [Fact]
    public void A_range_names_its_first_day_until_it_has_begun_and_no_day_once_it_has()
    {
        var parser = CreateParser();

        // On the Wednesday the weekend is ahead: Saturday. It was today at 6 PM.
        Assert.True(parser.TryResolve("this weekend 6pm", Chicago, out var result, out var error), error);
        Assert.Equal(JulyInChicago(18, 18), result);

        // On the Saturday, its first day, it has begun: today's 6 PM, or tomorrow's once that has
        // passed, as before. So has "this week" on the Monday.
        Assert.True(parser.TryResolve("this weekend 6pm", Chicago, out result, out error, JulyAt(18, 19)), error);
        Assert.Equal(JulyInChicago(19, 18), result);
        Assert.True(parser.TryResolve("this week 6pm", Chicago, out result, out error, JulyAt(20, 19)), error);
        Assert.Equal(JulyInChicago(21, 18), result);
        Assert.True(parser.TryResolve("mon to sat (6pm)", Chicago, out result, out error, JulyAt(20, 19)), error);
        Assert.Equal(JulyInChicago(21, 18), result);

        // On the Sunday it is under way: today, as before.
        Assert.True(parser.TryResolve("this weekend 6pm", Chicago, out result, out error, SundayMorning), error);
        Assert.Equal(JulyInChicago(19, 18), result);

        // A range that is over is not rescued by a time beside it. It was today at 6 PM.
        Assert.False(parser.TryResolve("last week (6pm)", Chicago, out _, out error));
        Assert.Equal("\"last week (6pm)\" resolves to a time in the past.", error);
    }

    // A whole period longer than a week names no single day, so a time beside it stays a time
    // alone, as before: in "6pm (may change)" the first of May is not the day meant. A week names
    // its first day ("next week (6pm)" above), and so does a span the text spells out.
    [Theory]
    [InlineData("6pm (may change)", 18)]
    [InlineData("noon (march)", 12)]
    [InlineData("6pm room 2027", 18)]
    [InlineData("6pm, table 1999", 18)]
    [InlineData("next month (6pm)", 18)]
    public void A_time_beside_a_whole_period_is_a_time_alone_as_before(string text, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(15, hour), result);
    }

    // The rule itself: a day and a time written apart read as the same two written together — the
    // same instant, or the same refusal — on the Wednesday at four times of day and on the
    // Saturday in the morning and late. "jul 15" is the Wednesday's own date: with a time read on
    // it, it is that day, although "jul 15" alone also offers next year's. The parts of the day
    // here have not begun at any of those moments; once one has, a time beside it is a time alone.
    [Theory]
    [InlineData("fri (6pm)", "fri 6pm")]
    [InlineData("fri. 9am", "fri 9am")]
    [InlineData("friday from 6pm", "friday 6pm")]
    [InlineData("6pm (friday)", "6pm friday")]
    [InlineData("wed (6pm)", "wed 6pm")]
    [InlineData("wed (9am)", "wed 9am")]
    [InlineData("sat (6pm)", "sat 6pm")]
    [InlineData("saturday: 9am", "saturday 9am")]
    [InlineData("tomorrow. noon", "tomorrow noon")]
    [InlineData("today (6pm)", "today 6pm")]
    [InlineData("today (9am)", "today 9am")]
    [InlineData("yesterday (6pm)", "yesterday 6pm")]
    [InlineData("jul 15 (9am)", "jul 15 9am")]
    [InlineData("jul 15 (6pm)", "jul 15 6pm")]
    [InlineData("jul 18 (6pm)", "jul 18 6pm")]
    [InlineData("nov 12 (6pm)", "nov 12 6pm")]
    [InlineData("next friday (6pm)", "next friday 6pm")]
    [InlineData("friday night (9pm)", "friday night 9pm")]
    [InlineData("tomorrow morning (7am)", "tomorrow morning 7am")]
    [InlineData("tomorrow evening (7pm)", "tomorrow evening 7pm")]
    public void A_day_and_a_time_written_apart_read_as_the_same_two_written_together(string apart, string together)
    {
        var parser = CreateParser();
        var moments = new[]
        {
            WednesdayAt(0, 30), WednesdayAt(8, 59), Now, WednesdayAt(23, 30), SaturdayMorning, SaturdayLateEvening,
        };

        foreach (var moment in moments)
        {
            // The text and the moment ride along in both tuples, so a failure names its case.
            var read = parser.TryResolve(apart, Chicago, out var result, out var error, moment);
            var togetherRead = parser.TryResolve(together, Chicago, out var togetherResult, out var togetherError, moment);
            Assert.Equal(
                (apart, moment, togetherRead, togetherResult, QuotingTheTextAsTyped(togetherError, together, apart)),
                (apart, moment, read, result, error));
        }
    }

    [Theory]
    [InlineData("fri (6pm)")]
    [InlineData("friday from 6pm")]
    [InlineData("friday at night")]
    [InlineData("tomorrow, after 6pm")]
    [InlineData("friday or saturday (6pm)")]
    [InlineData("friday 6pm, ends 9pm")]
    [InlineData("friday 6pm or 8pm")]
    [InlineData("nov 12 (6pm)")]
    [InlineData("next week (6pm)")]
    [InlineData("this week (6pm)")]             // a range under way: today, from both
    [InlineData("friday, this week (6pm)")]     // …which a day that is named outranks, in both
    public void The_two_readers_give_the_same_day_for_a_day_and_a_time_apart(string text)
    {
        var parser = CreateParser();

        Assert.True(parser.TryResolve(text, Chicago, out var result, out var error), error);
        Assert.True(parser.TryResolveDay(text, Chicago, out var day, out error), error);
        Assert.Equal(day, result.InZone(Chicago).Date);
    }

    // --- What none of the four rules touches ---

    // Each of these is one phrase to the recognizer, or several with no time apart from its day,
    // and was read before.
    [Theory]
    [InlineData("friday", 17, 9)]
    [InlineData("friday 6pm", 17, 18)]
    [InlineData("6pm friday", 17, 18)]
    [InlineData("friday at 6pm", 17, 18)]
    [InlineData("friday, 6pm", 17, 18)]
    [InlineData("friday - 6pm", 17, 18)]
    [InlineData("friday @ 6pm", 17, 18)]
    [InlineData("friday around 6pm", 17, 18)]
    [InlineData("friday 6pm-8pm", 17, 18)]
    [InlineData("after friday 6pm", 17, 18)]
    [InlineData("tomorrow", 16, 9)]
    [InlineData("tomorrow 6pm", 16, 18)]
    [InlineData("6pm", 15, 18)]
    [InlineData("noon", 15, 12)]
    [InlineData("9am", 16, 9)]
    [InlineData("next friday", 24, 9)]
    [InlineData("7/20 6pm", 20, 18)]
    [InlineData("in 3 days", 18, 9)]
    [InlineData("friday or saturday", 17, 9)]           // two days and no time: the earlier, at 09:00
    [InlineData("friday 6pm or saturday 2pm", 17, 18)]  // two days, each with its time: the earlier
    public void Dates_that_were_read_before_are_read_as_before(string text, int julyDay, int hour)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(JulyInChicago(julyDay, hour), result);
    }

    // The limit holds whatever the text holds: over it, text is refused in the limit's words; at it,
    // a deadline is read and a repeat refused as shorter text is. As in the length tests above, the
    // texts are one phrase filled out with dots.
    [Theory]
    [InlineData("by friday 6pm")]
    [InlineData("every friday 8pm")]
    [InlineData("fri (6pm)")]
    public void Text_over_the_length_limit_is_refused_in_the_limits_words_whatever_it_holds(string phrase)
    {
        var parser = CreateParser();
        var overLimit = phrase.PadRight(DateTextLimits.MaxLength + 1, '.');
        var tooLong = $"The date/time text must be at most {DateTextLimits.MaxLength} characters.";

        Assert.False(parser.TryResolve(overLimit, Chicago, out _, out var error));
        Assert.Equal(tooLong, error);
        Assert.False(parser.TryResolveDay(overLimit, Chicago, out _, out error));
        Assert.Equal(tooLong, error);
    }

    [Fact]
    public void A_deadline_and_a_repeat_at_the_length_limit_are_read_and_refused_as_shorter_ones_are()
    {
        var parser = CreateParser();
        var deadline = "by friday 6pm".PadRight(DateTextLimits.MaxLength, '.');
        var repeat = "every friday 8pm".PadRight(DateTextLimits.MaxLength, '.');

        Assert.True(parser.TryResolve(deadline, Chicago, out var result, out var error), error);
        Assert.Equal(JulyInChicago(17, 18), result);
        Assert.False(parser.TryResolve(repeat, Chicago, out _, out error));
        Assert.StartsWith($"\"{repeat}\" repeats.", error);
    }
}
