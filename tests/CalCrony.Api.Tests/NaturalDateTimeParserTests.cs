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
    public void Day_resolution_refuses_what_it_cannot_read(string text)
    {
        var ok = CreateParser().TryResolveDay(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Could not understand", error);
        Assert.Contains(text, error);
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
    [InlineData("every {day} 8pm")]
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
    // time it names.
    [Theory]
    [InlineData("by sat 6pm")]              // as "by saturday 6pm" and "by fri 6pm": an end with no start
    [InlineData("until sun 18:00")]
    [InlineData("before sat at noon")]
    [InlineData("every sat 8pm")]           // as "every saturday 8pm": a repeat, not a date
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
        // own day the recognizer offers last week's and today's, not next week's: refused, as
        // "saturday" is, in the words that were typed.
        Assert.False(parser.TryResolve("sat", Chicago, out _, out error, SaturdayMorning));
        Assert.Equal("\"sat\" resolves to a time in the past.", error);

        // The same for a time already behind. With the day dropped, this was tomorrow at 9 AM.
        Assert.False(parser.TryResolve("sat 9am", Chicago, out _, out error, SaturdayMorning));
        Assert.Equal("\"sat 9am\" resolves to a time in the past.", error);
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
}
