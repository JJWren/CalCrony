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
}
