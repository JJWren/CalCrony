using CalCrony.Api.Services;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The pure free-slot math (issue: free slots for /availability). A slot is a stretch in
/// which every member is inside their own available hours with no busy interval; days are the
/// caller's, hours are each member's own wall clock. Unless a test says otherwise the members are
/// on the default week (8 AM–11 PM every day) and "now" is two days before the first day asked
/// for, so it cuts nothing.</summary>
public class FreeSlotCalculatorTests
{
    private static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];
    private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];
    private static readonly DateTimeZone LosAngeles = DateTimeZoneProviders.Tzdb["America/Los_Angeles"];
    private static readonly DateTimeZone Berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];
    private static readonly DateTimeZone Tokyo = DateTimeZoneProviders.Tzdb["Asia/Tokyo"];

    /// <summary>Thursday; Chicago is on standard time (UTC-6).</summary>
    private static readonly LocalDate Thursday = new(2026, 11, 12);

    private static readonly Instant TwoDaysEarlier = At(Chicago, 2026, 11, 10, 12);

    [Fact]
    public void A_member_with_nothing_on_their_calendar_is_free_for_their_whole_day()
    {
        var days = FreeSlotCalculator.Compute(Query(Chicago, Thursday, days: 3), [Member(Chicago)]);

        Assert.Equal([Thursday, Thursday.PlusDays(1), Thursday.PlusDays(2)], days.Select(d => d.Date));
        Assert.Equal([(At(Chicago, 2026, 11, 12, 8), At(Chicago, 2026, 11, 12, 23))], days[0].Slots);
        Assert.Equal([(At(Chicago, 2026, 11, 13, 8), At(Chicago, 2026, 11, 13, 23))], days[1].Slots);
        Assert.Equal([(At(Chicago, 2026, 11, 14, 8), At(Chicago, 2026, 11, 14, 23))], days[2].Slots);
    }

    [Fact]
    public void A_busy_block_splits_the_day()
    {
        var member = Member(Chicago, (At(Chicago, 2026, 11, 12, 12), At(Chicago, 2026, 11, 12, 13, 30)));

        var day = FreeSlotCalculator.Compute(Query(Chicago, Thursday), [member]).Single();

        Assert.Equal(
            [
                (At(Chicago, 2026, 11, 12, 8), At(Chicago, 2026, 11, 12, 12)),
                (At(Chicago, 2026, 11, 12, 13, 30), At(Chicago, 2026, 11, 12, 23)),
            ],
            day.Slots);
    }

    [Fact]
    public void Busy_blocks_may_arrive_unsorted_overlapping_touching_or_reaching_outside_the_window()
    {
        // A provider promises neither order nor merged blocks, and its blocks need not stop at the
        // window's edge.
        var member = Member(
            Chicago,
            (At(Chicago, 2026, 11, 12, 15), At(Chicago, 2026, 11, 12, 16)),
            (At(Chicago, 2026, 11, 12, 9), At(Chicago, 2026, 11, 12, 10)),
            (At(Chicago, 2026, 11, 12, 9, 30), At(Chicago, 2026, 11, 12, 11)),     // overlaps the one before
            (At(Chicago, 2026, 11, 12, 11), At(Chicago, 2026, 11, 12, 12)),         // touches it
            (At(Chicago, 2026, 11, 11, 22), At(Chicago, 2026, 11, 12, 8, 30)),      // starts before the window
            (At(Chicago, 2026, 11, 12, 14), At(Chicago, 2026, 11, 12, 13)),         // inverted: ignored
            (At(Chicago, 2026, 11, 12, 14), At(Chicago, 2026, 11, 12, 14)));        // empty: ignored

        var day = FreeSlotCalculator.Compute(Query(Chicago, Thursday, minimumMinutes: 15), [member]).Single();

        Assert.Equal(
            [
                (At(Chicago, 2026, 11, 12, 8, 30), At(Chicago, 2026, 11, 12, 9)),
                (At(Chicago, 2026, 11, 12, 12), At(Chicago, 2026, 11, 12, 15)),
                (At(Chicago, 2026, 11, 12, 16), At(Chicago, 2026, 11, 12, 23)),
            ],
            day.Slots);
    }

    [Fact]
    public void A_block_spanning_several_days_cuts_every_day_it_covers()
    {
        var member = Member(Chicago, (At(Chicago, 2026, 11, 12, 10), At(Chicago, 2026, 11, 14, 12)));

        var days = FreeSlotCalculator.Compute(Query(Chicago, Thursday, days: 3), [member]);

        Assert.Equal([(At(Chicago, 2026, 11, 12, 8), At(Chicago, 2026, 11, 12, 10))], days[0].Slots);
        Assert.Empty(days[1].Slots);
        Assert.Equal([(At(Chicago, 2026, 11, 14, 12), At(Chicago, 2026, 11, 14, 23))], days[2].Slots);
    }

    [Fact]
    public void A_gap_shorter_than_the_minimum_is_dropped_and_an_exact_one_is_kept()
    {
        var member = Member(
            Chicago,
            (At(Chicago, 2026, 11, 12, 8, 59), At(Chicago, 2026, 11, 12, 10)),     // leaves 8:00–8:59: 59 minutes
            (At(Chicago, 2026, 11, 12, 11), At(Chicago, 2026, 11, 12, 22)));        // leaves 10–11 and 22–23: 60 each

        var day = FreeSlotCalculator.Compute(Query(Chicago, Thursday, minimumMinutes: 60), [member]).Single();

        Assert.Equal(
            [
                (At(Chicago, 2026, 11, 12, 10), At(Chicago, 2026, 11, 12, 11)),
                (At(Chicago, 2026, 11, 12, 22), At(Chicago, 2026, 11, 12, 23)),
            ],
            day.Slots);
    }

    [Theory]
    [InlineData(10, 7, 10, 15)]     // rounded up to the next quarter hour
    [InlineData(10, 15, 10, 15)]    // an exact quarter hour stays
    [InlineData(10, 16, 10, 30)]
    [InlineData(6, 40, 8, 0)]       // before the day's hours begin: the hours decide
    public void Todays_first_slot_starts_at_now_rounded_up_to_a_quarter_hour(
        int nowHour, int nowMinute, int slotHour, int slotMinute)
    {
        var now = At(Chicago, 2026, 11, 12, nowHour, nowMinute);

        var day = FreeSlotCalculator.Compute(Query(Chicago, Thursday, now: now), [Member(Chicago)]).Single();

        Assert.Equal([(At(Chicago, 2026, 11, 12, slotHour, slotMinute), At(Chicago, 2026, 11, 12, 23))], day.Slots);
    }

    [Fact]
    public void Seconds_past_a_quarter_hour_round_up_too()
    {
        var now = At(Chicago, 2026, 11, 12, 10, 15).Plus(Duration.FromSeconds(1));

        var day = FreeSlotCalculator.Compute(Query(Chicago, Thursday, now: now), [Member(Chicago)]).Single();

        Assert.Equal(At(Chicago, 2026, 11, 12, 10, 30), day.Slots.Single().Start);
    }

    [Theory]
    [InlineData(22, 20)]    // 22:30–23:00 is left: shorter than the 60-minute minimum
    [InlineData(23, 30)]    // after the day's hours
    [InlineData(23, 50)]    // rounds up to the midnight that ends the only day asked for
    public void A_day_with_too_little_left_is_listed_with_nothing_free(int nowHour, int nowMinute)
    {
        var now = At(Chicago, 2026, 11, 12, nowHour, nowMinute);

        var days = FreeSlotCalculator.Compute(Query(Chicago, Thursday, now: now), [Member(Chicago)]);

        var day = Assert.Single(days);
        Assert.Equal(Thursday, day.Date);
        Assert.Empty(day.Slots);
    }

    [Fact]
    public void Two_members_are_free_together_only_where_both_are()
    {
        var early = Member(Chicago, (At(Chicago, 2026, 11, 12, 8), At(Chicago, 2026, 11, 12, 12)));
        var late = Member(Chicago, (At(Chicago, 2026, 11, 12, 18), At(Chicago, 2026, 11, 12, 23)));

        var day = FreeSlotCalculator.Compute(Query(Chicago, Thursday), [early, late]).Single();

        Assert.Equal([(At(Chicago, 2026, 11, 12, 12), At(Chicago, 2026, 11, 12, 18))], day.Slots);
    }

    [Fact]
    public void Hours_are_each_members_own_wall_clock()
    {
        // 8 AM–11 PM in Berlin is 1 AM–4 PM in Chicago, so the two share 8 AM–4 PM Chicago time.
        var day = FreeSlotCalculator.Compute(Query(Chicago, Thursday), [Member(Chicago), Member(Berlin)]).Single();

        Assert.Equal([(At(Chicago, 2026, 11, 12, 8), At(Chicago, 2026, 11, 12, 16))], day.Slots);
        Assert.Equal(At(Berlin, 2026, 11, 12, 23), day.Slots.Single().End);
    }

    [Fact]
    public void Members_with_no_waking_hours_in_common_share_no_slot()
    {
        // Tokyo's day ends (11 PM JST = 8 AM Chicago) at the very instant Chicago's begins: the
        // two stretches touch and share nothing.
        var days = FreeSlotCalculator.Compute(
            Query(Chicago, Thursday, days: 3, minimumMinutes: 15),
            [Member(Chicago), Member(Berlin), Member(Tokyo)]);

        Assert.Equal(3, days.Count);
        Assert.All(days, day => Assert.Empty(day.Slots));
    }

    [Fact]
    public void Days_are_the_callers_and_a_slot_stays_with_the_day_it_starts_on()
    {
        // A Los Angeles member's 8 AM–11 PM is 11 AM–2 AM for a caller in New York.
        var days = FreeSlotCalculator.Compute(Query(NewYork, Thursday, days: 2), [Member(LosAngeles)]);

        // Thursday opens with what is left of Wednesday evening's stretch: the first day's midnight
        // cuts it, and the two hours that remain are listed.
        Assert.Equal(
            [
                (At(NewYork, 2026, 11, 12, 0), At(NewYork, 2026, 11, 12, 2)),
                (At(NewYork, 2026, 11, 12, 11), At(NewYork, 2026, 11, 13, 2)),
            ],
            days[0].Slots);

        // The last day's slot keeps its real end, past the midnight that closes the range…
        Assert.Equal([(At(NewYork, 2026, 11, 13, 11), At(NewYork, 2026, 11, 14, 2))], days[1].Slots);

        // …and nothing that starts after that midnight is listed, although the window reaches it.
        Assert.Equal(2, days.Count);
    }

    [Fact]
    public void Daylight_saving_keeps_the_wall_clock_hours()
    {
        // US clocks go forward on Sunday 2026-03-08. Saturday's 8 AM is 14:00 UTC, Sunday's 13:00
        // UTC — and both days still run fifteen hours.
        var days = FreeSlotCalculator.Compute(
            Query(Chicago, new LocalDate(2026, 3, 7), days: 2, now: At(Chicago, 2026, 3, 1, 12)),
            [Member(Chicago)]);

        Assert.Equal([(Instant.FromUtc(2026, 3, 7, 14, 0), Instant.FromUtc(2026, 3, 8, 5, 0))], days[0].Slots);
        Assert.Equal([(Instant.FromUtc(2026, 3, 8, 13, 0), Instant.FromUtc(2026, 3, 9, 4, 0))], days[1].Slots);
    }

    [Theory]
    [InlineData(2026, 3, 8, 5)]     // the night the clocks skip an hour
    [InlineData(2026, 11, 1, 7)]    // the night they repeat one
    public void A_range_across_a_clock_change_is_as_long_as_the_night_really_is(
        int year, int month, int dayOfMonth, int expectedHours)
    {
        var sunday = new LocalDate(year, month, dayOfMonth);
        var nights = Week((IsoDayOfWeek.Sunday, 0, 360));     // midnight to 6 AM

        var day = FreeSlotCalculator.Compute(
            Query(Chicago, sunday, now: At(Chicago, year, month, dayOfMonth, 0).Minus(Duration.FromDays(2))),
            [new MemberAvailability(Chicago, nights, [])]).Single();

        var slot = Assert.Single(day.Slots);
        Assert.Equal(sunday.AtStartOfDayInZone(Chicago).ToInstant(), slot.Start);
        Assert.Equal(Duration.FromHours(expectedHours), slot.End - slot.Start);
    }

    [Fact]
    public void A_weekday_without_a_range_offers_nothing()
    {
        var weekdayEvenings = Week(
            (IsoDayOfWeek.Monday, 1080, 1380),
            (IsoDayOfWeek.Tuesday, 1080, 1380),
            (IsoDayOfWeek.Wednesday, 1080, 1380),
            (IsoDayOfWeek.Thursday, 1080, 1380),
            (IsoDayOfWeek.Friday, 1080, 1380));
        var saturday = new LocalDate(2026, 11, 14);

        var days = FreeSlotCalculator.Compute(
            Query(Chicago, saturday, days: 3), [new MemberAvailability(Chicago, weekdayEvenings, [])]);

        Assert.Empty(days[0].Slots);
        Assert.Empty(days[1].Slots);
        Assert.Equal([(At(Chicago, 2026, 11, 16, 18), At(Chicago, 2026, 11, 16, 23))], days[2].Slots);
    }

    [Fact]
    public void Hours_that_run_to_midnight_and_resume_at_midnight_are_one_slot()
    {
        var lateNights = Week((IsoDayOfWeek.Thursday, 1200, 1440), (IsoDayOfWeek.Friday, 0, 120));

        var days = FreeSlotCalculator.Compute(
            Query(Chicago, Thursday, days: 2), [new MemberAvailability(Chicago, lateNights, [])]);

        // One stretch from 8 PM Thursday to 2 AM Friday, listed under Thursday; Friday's early
        // hours are part of it, not a slot of their own.
        Assert.Equal([(At(Chicago, 2026, 11, 12, 20), At(Chicago, 2026, 11, 13, 2))], days[0].Slots);
        Assert.Empty(days[1].Slots);
    }

    [Fact]
    public void With_no_members_every_day_comes_back_empty()
    {
        var days = FreeSlotCalculator.Compute(Query(Chicago, Thursday, days: 2), []);

        Assert.Equal([Thursday, Thursday.PlusDays(1)], days.Select(d => d.Date));
        Assert.All(days, day => Assert.Empty(day.Slots));
    }

    [Fact]
    public void The_window_starts_at_the_rounded_now_today_and_reaches_one_day_past_the_range()
    {
        var query = Query(Chicago, Thursday, days: 7, now: At(Chicago, 2026, 11, 12, 10, 7));

        var (start, end) = FreeSlotCalculator.QueryWindow(query);

        Assert.Equal(At(Chicago, 2026, 11, 12, 10, 15), start);
        // Seven days end at midnight on the 19th; the window runs one day further.
        Assert.Equal(At(Chicago, 2026, 11, 20, 0), end);
    }

    [Fact]
    public void The_window_for_a_future_first_day_starts_at_its_midnight_in_the_callers_zone()
    {
        var (start, end) = FreeSlotCalculator.QueryWindow(Query(Berlin, Thursday));

        Assert.Equal(At(Berlin, 2026, 11, 12, 0), start);
        Assert.Equal(At(Berlin, 2026, 11, 14, 0), end);
    }

    [Fact]
    public void A_lookup_needs_at_least_one_day()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeSlotCalculator.QueryWindow(Query(Chicago, Thursday, days: 0)));
    }

    [Fact]
    public void The_interval_arithmetic_agrees_with_a_minute_by_minute_model()
    {
        // The calculator works on interval lists; this model walks the window one minute at a time
        // and asks each member "inside your hours, and not busy?". Zones without daylight saving
        // only — there the two readings of "inside your hours" cannot differ — but with whole-,
        // half- and quarter-hour offsets. A fixed seed keeps the cases the same on every run.
        var random = new Random(20261111);
        string[] zoneIds =
        [
            "UTC", "Asia/Tokyo", "America/Phoenix", "Asia/Kolkata", "Asia/Kathmandu", "Pacific/Kiritimati", "America/Bogota",
        ];
        var zones = zoneIds.Select(id => DateTimeZoneProviders.Tzdb[id]).ToArray();
        int[] minimums = [15, 30, 60, 90, 240];

        for (var run = 0; run < 250; run++)
        {
            var callerZone = zones[random.Next(zones.Length)];
            var firstDay = new LocalDate(2026, 1, 12).PlusDays(random.Next(4));
            var days = random.Next(1, 5);
            var firstMidnight = firstDay.AtStartOfDayInZone(callerZone).ToInstant();
            var now = random.Next(2) == 0
                ? firstMidnight - Duration.FromDays(2)
                : firstMidnight + Duration.FromMinutes(random.Next(24 * 60)) + Duration.FromSeconds(random.Next(60));
            var query = new FreeSlotQuery(now, callerZone, firstDay, days, Duration.FromMinutes(minimums[random.Next(minimums.Length)]));

            var members = Enumerable.Range(0, random.Next(1, 4)).Select(_ => new MemberAvailability(
                zones[random.Next(zones.Length)],
                RandomWeek(random),
                RandomBusy(random, firstMidnight, days))).ToList();

            var actual = FreeSlotCalculator.Compute(query, members);
            var expected = ModelSlots(query, members);

            Assert.Equal(expected.Select(day => day.Date), actual.Select(day => day.Date));
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.True(
                    expected[i].Slots.SequenceEqual(actual[i].Slots),
                    $"run {run}, {expected[i].Date}: the model finds [{Describe(expected[i].Slots)}], the calculator [{Describe(actual[i].Slots)}]");
            }
        }
    }

    [Fact]
    public void Slots_stay_ordered_apart_and_inside_the_window_across_clock_changes()
    {
        // Around the nights the clocks change the minute-by-minute model above no longer applies
        // (an hour is skipped or runs twice), but what must hold of any answer still does. Zones
        // with an ordinary one-hour change, a half-hour change (Lord Howe) and offsets on the
        // half and three-quarter hour (St John's, Chatham); first days on and beside each change.
        var random = new Random(20261112);
        string[] zoneIds =
        [
            "America/Chicago", "Europe/Berlin", "Australia/Lord_Howe", "America/St_Johns", "Pacific/Chatham", "UTC",
        ];
        var zones = zoneIds.Select(id => DateTimeZoneProviders.Tzdb[id]).ToArray();
        LocalDate[] changes =
        [
            new(2026, 3, 8), new(2026, 3, 29), new(2026, 4, 5), new(2026, 9, 27), new(2026, 10, 4), new(2026, 10, 25), new(2026, 11, 1),
        ];
        int[] minimums = [15, 30, 60, 90, 240];

        for (var run = 0; run < 250; run++)
        {
            var callerZone = zones[random.Next(zones.Length)];
            var firstDay = changes[random.Next(changes.Length)].PlusDays(random.Next(-2, 2));
            var days = random.Next(1, 4);
            var firstMidnight = firstDay.AtStartOfDayInZone(callerZone).ToInstant();
            var now = random.Next(2) == 0
                ? firstMidnight - Duration.FromDays(2)
                : firstMidnight + Duration.FromMinutes(random.Next(24 * 60));
            var minimum = Duration.FromMinutes(minimums[random.Next(minimums.Length)]);
            var query = new FreeSlotQuery(now, callerZone, firstDay, days, minimum);
            var members = Enumerable.Range(0, random.Next(1, 4)).Select(_ => new MemberAvailability(
                zones[random.Next(zones.Length)],
                RandomWeek(random),
                RandomBusy(random, firstMidnight, days))).ToList();

            var result = FreeSlotCalculator.Compute(query, members);

            var (windowStart, windowEnd) = FreeSlotCalculator.QueryWindow(query);
            var lastStart = firstDay.PlusDays(days).AtStartOfDayInZone(callerZone).ToInstant();
            var context = $"run {run}, {callerZone.Id} from {firstDay}";

            Assert.Equal(Enumerable.Range(0, days).Select(offset => firstDay.PlusDays(offset)), result.Select(day => day.Date));

            Instant? previousEnd = null;
            foreach (var day in result)
            {
                foreach (var slot in day.Slots)
                {
                    Assert.True(slot.End - slot.Start >= minimum, $"{context}: {slot.Start}–{slot.End} is shorter than the minimum");
                    Assert.True(slot.Start >= windowStart && slot.End <= windowEnd, $"{context}: {slot.Start}–{slot.End} leaves the window");
                    Assert.True(slot.Start < lastStart, $"{context}: {slot.Start} starts after the last requested day");
                    Assert.True(day.Date == slot.Start.InZone(callerZone).Date, $"{context}: {slot.Start} is listed under {day.Date}");

                    // In order, and neither overlapping nor touching the slot before it.
                    Assert.True(previousEnd is null || slot.Start > previousEnd, $"{context}: {slot.Start} does not follow {previousEnd}");
                    previousEnd = slot.End;

                    Assert.All(members, member => Assert.DoesNotContain(
                        member.Busy, block => block.Start < block.End && block.Start < slot.End && slot.Start < block.End));
                }
            }
        }
    }

    private static string Describe(IEnumerable<(Instant Start, Instant End)> slots) =>
        string.Join(", ", slots.Select(slot => $"{slot.Start}–{slot.End}"));

    private static WeeklyHours RandomWeek(Random random)
    {
        var ranges = new Dictionary<IsoDayOfWeek, (int StartMinute, int EndMinute)>();
        for (var day = IsoDayOfWeek.Monday; day <= IsoDayOfWeek.Sunday; day++)
        {
            if (random.Next(5) == 0)
            {
                continue;       // a day off
            }

            // Half-hour steps, and the end may be midnight (48 half hours).
            var start = random.Next(48);
            ranges[day] = (start * 30, random.Next(start + 1, 49) * 30);
        }

        return new WeeklyHours(ranges);
    }

    private static List<(Instant Start, Instant End)> RandomBusy(Random random, Instant firstMidnight, int days) =>
        [.. Enumerable.Range(0, random.Next(7)).Select(_ =>
        {
            // Anywhere from a day before the range to two days after it; one block in ten is
            // empty or inverted, which the calculator must ignore.
            var start = firstMidnight + Duration.FromMinutes(random.Next(-24 * 60, (days + 2) * 24 * 60));
            var length = random.Next(10) == 0 ? -random.Next(120) : random.Next(1, 600);
            return (start, start + Duration.FromMinutes(length));
        })];

    /// <summary>The same answer the slow way: every minute of the window is free or not, runs of
    /// free minutes are the slots, and the same keep-and-group rules apply.</summary>
    private static List<(LocalDate Date, List<(Instant Start, Instant End)> Slots)> ModelSlots(
        FreeSlotQuery query, List<MemberAvailability> members)
    {
        var firstMidnight = query.FirstDay.AtStartOfDayInZone(query.CallerZone).ToInstant();
        var lastStart = query.FirstDay.PlusDays(query.Days).AtStartOfDayInZone(query.CallerZone).ToInstant();
        var windowEnd = query.FirstDay.PlusDays(query.Days + 1).AtStartOfDayInZone(query.CallerZone).ToInstant();
        var quarterHour = 15 * 60;
        var roundedNow = Instant.FromUnixTimeSeconds(
            (long)Math.Ceiling(query.Now.ToUnixTimeTicks() / (double)NodaConstants.TicksPerSecond / quarterHour) * quarterHour);
        var rangeStart = Instant.Max(firstMidnight, roundedNow);

        bool IsFree(Instant minute) => members.All(member =>
        {
            var local = minute.InZone(member.Zone).LocalDateTime;
            var minuteOfDay = (local.Hour * 60) + local.Minute;
            return member.Hours.RangeFor(local.DayOfWeek) is { } range
                && minuteOfDay >= range.StartMinute && minuteOfDay < range.EndMinute
                && !member.Busy.Any(block => block.Start <= minute && minute < block.End);
        });

        var slots = new List<(Instant Start, Instant End)>();
        Instant? runStart = null;
        for (var minute = rangeStart; minute < windowEnd; minute += Duration.FromMinutes(1))
        {
            if (IsFree(minute))
            {
                runStart ??= minute;
            }
            else if (runStart is { } start)
            {
                slots.Add((start, minute));
                runStart = null;
            }
        }

        if (runStart is { } open)
        {
            slots.Add((open, windowEnd));
        }

        return [.. Enumerable.Range(0, query.Days)
            .Select(offset => query.FirstDay.PlusDays(offset))
            .Select(day => (day, slots
                .Where(slot => slot.Start < lastStart && slot.End - slot.Start >= query.Minimum)
                .Where(slot => slot.Start.InZone(query.CallerZone).Date == day)
                .ToList()))];
    }

    private static Instant At(DateTimeZone zone, int year, int month, int day, int hour, int minute = 0) =>
        new LocalDateTime(year, month, day, hour, minute).InZoneStrictly(zone).ToInstant();

    private static FreeSlotQuery Query(
        DateTimeZone callerZone, LocalDate firstDay, int days = 1, int minimumMinutes = 60, Instant? now = null) =>
        new(now ?? TwoDaysEarlier, callerZone, firstDay, days, Duration.FromMinutes(minimumMinutes));

    private static MemberAvailability Member(DateTimeZone zone, params (Instant Start, Instant End)[] busy) =>
        new(zone, WeeklyHours.Default, busy);

    private static WeeklyHours Week(params (IsoDayOfWeek Day, int StartMinute, int EndMinute)[] ranges) =>
        new(ranges.ToDictionary(r => r.Day, r => (r.StartMinute, r.EndMinute)));
}
