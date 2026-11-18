using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;
using NodaTime.Testing;

namespace CalCrony.Api.Tests;

/// <summary>How far ahead a date typed in words may lie
/// (<see cref="DateTextLimits.MaxYearsAhead"/> years). The last day allowed is that many years
/// from today in the zone the text is read in; anything on a later day is refused.</summary>
public class DateAheadLimitTests
{
    // Wednesday 2026-07-15 15:00 UTC == 10:00 in Chicago (CDT, UTC-5), so the last day allowed
    // there is 2036-07-15. In Kiritimati (UTC+14) it is already the 16th, and so is the last day.
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 15, 15, 0);
    private static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];
    private static readonly DateTimeZone Kiritimati = DateTimeZoneProviders.Tzdb["Pacific/Kiritimati"];

    private static NaturalDateTimeParser CreateParser() => new(new FakeClock(Now));

    private static string TooFarAhead(string text) =>
        $"\"{text}\" is more than {DateTextLimits.MaxYearsAhead} years away; dates can be set up to {DateTextLimits.MaxYearsAhead} years ahead.";

    /// <summary>The instant of a wall-clock hour in a zone, by that zone's own rules — so a
    /// result expected years ahead does not rest on an offset written out here.</summary>
    private static Instant At(DateTimeZone zone, int year, int month, int day, int hour) =>
        new LocalDateTime(year, month, day, hour, 0).InZoneLeniently(zone).ToInstant();

    [Fact]
    public void The_limit_is_ten_years()
    {
        // The dates below are written out for a ten-year limit.
        Assert.Equal(10, DateTextLimits.MaxYearsAhead);
    }

    // The two tests below read the same text one day apart, so a single day decides between
    // "read" and "refused".
    [Fact]
    public void Reads_a_time_on_the_last_day_allowed()
    {
        var ok = CreateParser().TryResolve("7/15/2036 6pm", Chicago, out var result, out var error);

        Assert.True(ok, error);
        // Late on the last day, which counts as a whole.
        Assert.Equal(At(Chicago, 2036, 7, 15, 18), result);
    }

    [Fact]
    public void Refuses_the_day_after_the_last_day_allowed()
    {
        var ok = CreateParser().TryResolve("7/16/2036 6pm", Chicago, out var result, out var error);

        Assert.False(ok);
        Assert.Equal(default, result);
        Assert.Equal(TooFarAhead("7/16/2036 6pm"), error);
    }

    [Fact]
    public void Reads_relative_text_that_lands_on_the_last_day_allowed()
    {
        var ok = CreateParser().TryResolve("in 10 years", Chicago, out var result, out var error);

        Assert.True(ok, error);
        // A date with no time is 9:00 local.
        Assert.Equal(At(Chicago, 2036, 7, 15, 9), result);
    }

    [Fact]
    public void Ten_years_after_a_feb_29_the_last_day_allowed_is_feb_28()
    {
        // Noon on a leap day in Chicago. 2038 has no Feb 29, so the last day allowed is the 28th.
        var parser = new NaturalDateTimeParser(new FakeClock(At(Chicago, 2028, 2, 29, 12)));

        Assert.True(parser.TryResolve("2/28/2038 6pm", Chicago, out var result, out var error), error);
        Assert.Equal(At(Chicago, 2038, 2, 28, 18), result);

        Assert.False(parser.TryResolve("3/1/2038 6pm", Chicago, out _, out error));
        Assert.Equal(TooFarAhead("3/1/2038 6pm"), error);
    }

    [Theory]
    [InlineData("in 11 years")]
    [InlineData("in 121 months")]
    [InlineData("in 600 weeks")]
    [InlineData("in 4000 days")]
    [InlineData("january 1 2040")]
    [InlineData("1/1/2040 9am")]
    public void Refuses_a_date_past_the_limit_however_it_is_written(string text)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.False(ok);
        Assert.Equal(default, result);
        Assert.Equal(TooFarAhead(text), error);
    }

    // "sat" and "sun" are spelled out for the recognizer only. The date they stand beside is held
    // to the limit like any other, and the refusal quotes the text as it was typed.
    [Theory]
    [InlineData("sat 1/5/2041 6pm")]
    [InlineData("sun 1/6/2041 6pm")]
    public void Refuses_a_date_past_the_limit_written_with_a_short_weekend_day(string text)
    {
        var ok = CreateParser().TryResolve(text, Chicago, out var result, out var error);

        Assert.False(ok);
        Assert.Equal(default, result);
        Assert.Equal(TooFarAhead(text), error);
    }

    [Fact]
    public void The_limit_counts_from_today_in_the_zone_the_text_is_read_in()
    {
        // The day Chicago refuses above is the last day allowed where it is already the 16th.
        var ok = CreateParser().TryResolve("7/16/2036 6pm", Kiritimati, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(At(Kiritimati, 2036, 7, 16, 18), result);
    }

    [Fact]
    public void A_zone_abbreviation_in_the_text_decides_which_today_the_limit_counts_from()
    {
        // The caller's zone would allow the 16th; the text says Central time, where the last day
        // allowed is the 15th.
        var ok = CreateParser().TryResolve("7/16/2036 6pm CST", Kiritimati, out _, out var error);

        Assert.False(ok);
        Assert.Equal(TooFarAhead("7/16/2036 6pm"), error);
    }

    [Fact]
    public void A_date_inside_the_limit_is_read_although_the_text_also_names_one_past_it()
    {
        // The earliest reading that is still ahead wins, as it always has; one past the limit is
        // never the earliest.
        var ok = CreateParser().TryResolve("tomorrow 6pm or 1/1/2040 9am", Chicago, out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal(At(Chicago, 2026, 7, 16, 18), result);
    }

    [Fact]
    public void A_date_past_the_limit_beside_one_that_has_passed_is_refused_as_too_far_ahead()
    {
        // Nothing in the text is both ahead and inside the limit. The reason given is the date
        // past the limit, not the one that has passed.
        const string text = "1/1/2020 9am or 1/1/2040 9am";

        var ok = CreateParser().TryResolve(text, Chicago, out _, out var error);

        Assert.False(ok);
        Assert.Equal(TooFarAhead(text), error);
    }

    // A date on the calendar's last day is refused like any other past the limit. The number of
    // days is counted from today in each zone, so the text names that day in every one of them.
    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Chicago")]
    [InlineData("Pacific/Kiritimati")]
    public void Refuses_a_time_on_the_calendars_last_day_as_too_far_ahead(string zoneId)
    {
        var zone = DateTimeZoneProviders.Tzdb[zoneId];
        var days = Period.DaysBetween(Now.InZone(zone).Date, LocalDate.MaxIsoValue);
        var text = $"in {days} days at 6pm";

        var ok = CreateParser().TryResolve(text, zone, out var result, out var error);

        Assert.False(ok);
        Assert.Equal(default, result);
        Assert.Equal(TooFarAhead(text), error);
    }

    // A time that was stored earlier and is used again is held to the same last day, without
    // being read from text. One minute decides, and the zone decides which day is the last.
    [Fact]
    public void A_stored_time_is_inside_the_limit_through_the_end_of_the_last_day_allowed()
    {
        // In Chicago the last day allowed is Jul 15 2036; it ends where the 16th begins there.
        var endOfLastDay = new LocalDate(2036, 7, 16).AtStartOfDayInZone(Chicago).ToInstant();

        Assert.True(NaturalDateTimeParser.IsWithinAheadLimit(Now.Plus(Duration.FromHours(1)), Chicago, Now));
        Assert.True(NaturalDateTimeParser.IsWithinAheadLimit(endOfLastDay.Minus(Duration.FromMinutes(1)), Chicago, Now));
        Assert.False(NaturalDateTimeParser.IsWithinAheadLimit(endOfLastDay, Chicago, Now));

        // Where it is already the 16th, that instant is still on the last day allowed.
        Assert.True(NaturalDateTimeParser.IsWithinAheadLimit(endOfLastDay, Kiritimati, Now));
    }

    [Fact]
    public void A_day_that_a_zone_skips_whole_ends_the_last_day_allowed_like_any_other()
    {
        // Samoa had no 30 December 2011: its clocks went from the end of the 29th to the start of
        // the 31st. Ten years earlier the last day allowed is that 29th, and it ends where the
        // 31st begins.
        var apia = DateTimeZoneProviders.Tzdb["Pacific/Apia"];
        var now = At(apia, 2001, 12, 29, 12);
        var endOfLastDay = new LocalDate(2011, 12, 31).AtStartOfDayInZone(apia).ToInstant();

        Assert.True(NaturalDateTimeParser.IsWithinAheadLimit(endOfLastDay.Minus(Duration.FromMinutes(1)), apia, now));
        Assert.False(NaturalDateTimeParser.IsWithinAheadLimit(endOfLastDay, apia, now));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Chicago")]
    [InlineData("Pacific/Kiritimati")]
    public void The_last_instant_there_is_lies_outside_the_limit_in_every_zone(string zoneId)
    {
        Assert.False(NaturalDateTimeParser.IsWithinAheadLimit(Instant.MaxValue, DateTimeZoneProviders.Tzdb[zoneId], Now));
    }
}
