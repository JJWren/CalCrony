using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>POST /calendar/free-slots end to end on the fake calendar provider. The API runs on the
/// real clock, so each test asks for a concrete day a few days ahead in the zone under test —
/// "now" then cuts nothing, and the expected instants do not depend on when the suite runs. The
/// scheduler is off in tests, so events and series stay as a test seeds them.</summary>
public class FreeSlotsApiTests(CalendarApiFixture fixture) : IClassFixture<CalendarApiFixture>
{
    private static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];
    private static readonly DateTimeZone Berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];

    /// <summary>The tables a lookup reads CalCrony's events from, as they are quoted in a command.</summary>
    private static readonly string[] EventTables = ["\"Events\"", "\"EventSeries\"", "\"Rsvps\"", "\"RsvpOptions\""];

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task A_linked_members_calendar_cuts_their_day_into_slots()
    {
        const long guildId = 9100, caller = 9101, member = 9102;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedConnectionAsync(member, "access-slots-9102");
        var day = DayAhead(Chicago);
        fixture.Provider.BusyBlocksByAccessToken["access-slots-9102"] =
        [
            (At(Chicago, day, 10), At(Chicago, day, 11, 30)),
            (At(Chicago, day, 15), At(Chicago, day, 17)),
        ];

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 2, 60));

        Assert.Equal("America/Chicago", result.TimeZone);
        Assert.Equal(day.ToDateOnly(), result.FirstDay);
        Assert.Equal(day.PlusDays(1).ToDateOnly(), result.LastDay);
        Assert.Equal(1, result.CountedMembers);
        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.DefaultHours)], result.Members);

        // Every requested day is listed; the first is cut by the two blocks, the second is whole.
        Assert.Equal([day.ToDateOnly(), day.PlusDays(1).ToDateOnly()], result.Days.Select(d => d.Date));
        Assert.Equal(
            [
                Slot(At(Chicago, day, 8), At(Chicago, day, 10)),
                Slot(At(Chicago, day, 11, 30), At(Chicago, day, 15)),
                Slot(At(Chicago, day, 17), At(Chicago, day, 23)),
            ],
            result.Days[0].Slots);
        Assert.Equal([Slot(At(Chicago, day.PlusDays(1), 8), At(Chicago, day.PlusDays(1), 23))], result.Days[1].Slots);
    }

    [Fact]
    public async Task Members_enter_the_answer_by_what_could_be_read()
    {
        const long guildId = 9110, caller = 9111, linked = 9112, unlinked = 9113, needsReconnect = 9114, failing = 9115;
        await SeedGuildAsync(guildId, "America/Chicago");
        var expired = SystemClock.Instance.GetCurrentInstant() - Duration.FromMinutes(5);
        await SeedConnectionAsync(linked, "access-slots-9112");
        await SeedConnectionAsync(needsReconnect, "access-slots-9114", expired, "refresh-slots-9114");
        await SeedConnectionAsync(failing, "access-slots-9115", expired, "refresh-slots-9115");
        fixture.Provider.RefreshOverrides["refresh-slots-9114"] = CalendarTokenRefreshResult.ReconnectRequired("invalid_grant");
        fixture.Provider.RefreshOverrides["refresh-slots-9115"] = CalendarTokenRefreshResult.Error("timeout");
        var day = DayAhead(Chicago);
        fixture.Provider.BusyBlocksByAccessToken["access-slots-9112"] = [(At(Chicago, day, 8), At(Chicago, day, 20))];

        var result = await GetAsync(new FreeSlotsRequest(
            [linked, unlinked, needsReconnect, failing], caller, guildId, Iso(day), 1, 60));

        // One entry per member, in request order, each with the reason it was or was not counted.
        Assert.Equal(
            [
                new FreeSlotMemberDto(linked, FreeSlotMemberState.DefaultHours),
                new FreeSlotMemberDto(unlinked, FreeSlotMemberState.NotCounted),
                new FreeSlotMemberDto(needsReconnect, FreeSlotMemberState.ReconnectRequired),
                new FreeSlotMemberDto(failing, FreeSlotMemberState.Error),
            ],
            result.Members);

        // Only the member whose calendar was read shapes the slots.
        Assert.Equal(1, result.CountedMembers);
        Assert.Equal([Slot(At(Chicago, day, 20), At(Chicago, day, 23))], result.Days.Single().Slots);
    }

    [Fact]
    public async Task When_nobody_is_counted_the_answer_carries_no_days()
    {
        const long guildId = 9120, caller = 9121;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);

        var result = await GetAsync(new FreeSlotsRequest([9122, 9123], caller, guildId, Iso(day), 7, 60));

        Assert.Equal(0, result.CountedMembers);
        Assert.Empty(result.Days);
        Assert.Equal(
            [
                new FreeSlotMemberDto(9122, FreeSlotMemberState.NotCounted),
                new FreeSlotMemberDto(9123, FreeSlotMemberState.NotCounted),
            ],
            result.Members);

        // The range asked for is still reported, so the reply can say what was looked at.
        Assert.Equal(day.ToDateOnly(), result.FirstDay);
        Assert.Equal(day.PlusDays(6).ToDateOnly(), result.LastDay);
    }

    [Fact]
    public async Task Each_members_hours_are_read_in_their_own_timezone()
    {
        const long guildId = 9130, caller = 9131, inBerlin = 9132, onServerTime = 9133;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedProfileAsync(caller, "America/Chicago");
        await SeedProfileAsync(inBerlin, "Europe/Berlin");
        await SeedConnectionAsync(inBerlin, "access-slots-9132");
        await SeedConnectionAsync(onServerTime, "access-slots-9133");     // no profile: the server's zone
        var day = DayAhead(Chicago);

        var result = await GetAsync(new FreeSlotsRequest([inBerlin, onServerTime], caller, guildId, Iso(day), 1, 60));

        // 8 AM–11 PM in Berlin ends mid-afternoon in Chicago; 8 AM–11 PM on server time starts at
        // 8 AM Chicago. The two share the stretch between.
        Assert.Equal(2, result.CountedMembers);
        Assert.Equal([Slot(At(Chicago, day, 8), At(Berlin, day, 23))], result.Days.Single().Slots);
    }

    [Theory]
    [InlineData(9140, "Europe/Berlin", "America/Chicago", "Europe/Berlin")]       // the caller's own zone wins
    [InlineData(9142, null, "America/Chicago", "America/Chicago")]                // none set: the server's
    [InlineData(9144, "Not/AZone", "America/Chicago", "America/Chicago")]         // a stored id that names no zone
    [InlineData(9146, null, null, "UTC")]                                         // a server CalCrony has no row for
    public async Task Days_are_cut_in_the_zone_natural_language_times_parse_in(
        long guildId, string? callerTimeZone, string? guildTimeZone, string expected)
    {
        var caller = guildId + 1;
        if (guildTimeZone is not null)
        {
            await SeedGuildAsync(guildId, guildTimeZone);
        }

        if (callerTimeZone is not null)
        {
            await SeedProfileAsync(caller, callerTimeZone);
        }

        var slots = await GetAsync(new FreeSlotsRequest([guildId + 300], caller, guildId, null, 1, 60));
        var parsed = await Client.PostAsJsonAsync("/tools/parse-datetime", new ParseDateTimeRequest("tomorrow 6pm", caller, guildId));
        parsed.EnsureSuccessStatusCode();

        // The slots route states the member-zone rule through MemberZone, the datetime route
        // inline; the two must never disagree about whose day it is.
        Assert.Equal(expected, slots.TimeZone);
        Assert.Equal((await parsed.Content.ReadFromJsonAsync<ParseDateTimeResponse>())!.TimeZone, slots.TimeZone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("today")]
    public async Task Without_a_day_or_with_today_the_first_day_is_today_in_the_callers_zone(string? when)
    {
        // UTC+14: for most of the day "today" here is not the UTC date, so a UTC-day bug shows.
        const long guildId = 9150, caller = 9151;
        var zone = DateTimeZoneProviders.Tzdb["Pacific/Kiritimati"];
        await SeedGuildAsync(guildId, zone.Id);

        var before = Today(zone);
        var result = await GetAsync(new FreeSlotsRequest([9152], caller, guildId, when, 3, 60));
        var after = Today(zone);

        // Read on both sides of the call: midnight may pass between them.
        Assert.Contains(result.FirstDay, new[] { before.ToDateOnly(), after.ToDateOnly() });
        Assert.Equal(result.FirstDay.AddDays(2), result.LastDay);
    }

    [Theory]
    [InlineData("yesterday", "already passed")]
    [InlineData("january 1 2020", "already passed")]
    [InlineData("banana hammock", "Could not understand")]
    public async Task A_first_day_that_cannot_be_used_is_refused_with_the_reason(string when, string expected)
    {
        var response = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([9160], 9161, 9162, when, 7, 60));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task A_first_day_more_than_a_year_away_is_refused_not_looked_up()
    {
        await AssertRefusedAsTooFarAheadAsync("year 5000", member: 9190);
    }

    [Fact]
    public async Task A_first_day_in_the_last_days_of_the_calendar_is_refused_not_a_500()
    {
        // The last days of year 9999, where adding the requested days would overflow. The number
        // of weeks is counted from today, so the text lands there whenever the suite runs: a fixed
        // number stops being a date at all once today plus that many weeks is past the year's end.
        var weeks = Period.DaysBetween(Today(DateTimeZone.Utc), new LocalDate(9999, 12, 25)) / 7;

        await AssertRefusedAsTooFarAheadAsync($"in {weeks} weeks", member: 9191);
    }

    [Fact]
    public async Task The_year_ahead_is_the_horizon_for_the_first_day()
    {
        // A date typed without its year rolls forward by up to a year, so that much must work.
        // One day inside and two days outside the limit: midnight may pass between this reading
        // of "today" and the API's.
        var today = Today(DateTimeZone.Utc);

        var inside = await Client.PostAsJsonAsync("/calendar/free-slots", new FreeSlotsRequest(
            [9195], 9196, 9197, Iso(today.PlusDays(FreeSlotLimits.MaxDaysAhead - 1)), 14, 60));
        inside.EnsureSuccessStatusCode();

        var outside = await Client.PostAsJsonAsync("/calendar/free-slots", new FreeSlotsRequest(
            [9195], 9196, 9197, Iso(today.PlusDays(FreeSlotLimits.MaxDaysAhead + 2)), 14, 60));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Contains("more than a year away", (await outside.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task First_day_text_over_its_length_limit_is_refused_and_text_at_the_limit_is_read()
    {
        // Both texts are the same day filled out with dots, so they differ in length alone: one
        // character decides between "read" and "refused".
        var atLimit = "tomorrow".PadRight(DateTextLimits.MaxLength, '.');
        var overLimit = "tomorrow".PadRight(DateTextLimits.MaxLength + 1, '.');

        var read = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([9310], 9311, 9312, atLimit, 1, 60));
        read.EnsureSuccessStatusCode();

        var refused = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([9310], 9311, 9312, overLimit, 1, 60));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(
            $"when must be at most {DateTextLimits.MaxLength} characters",
            (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task A_day_that_begins_inside_a_slot_from_the_day_before_says_until_when_it_is_free()
    {
        // A caller in Tokyo looks at a member on Chicago time. The member's 8 AM–11 PM starts late
        // in the caller's evening and ends the next afternoon, so each stretch is listed under one
        // of the caller's days and runs well into the next.
        const long guildId = 9300, caller = 9301, member = 9302;
        var tokyo = DateTimeZoneProviders.Tzdb["Asia/Tokyo"];
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedProfileAsync(caller, "Asia/Tokyo");
        await SeedConnectionAsync(member, "access-slots-9302");
        var day = DayAhead(tokyo);

        // Busy for the whole stretch that would have started on the caller's second day.
        fixture.Provider.BusyBlocksByAccessToken["access-slots-9302"] =
            [(At(Chicago, day.PlusDays(1), 8), At(Chicago, day.PlusDays(1), 23))];

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 2, 60));

        // The first day ends with the stretch that starts that evening; nothing ran into it.
        Assert.Equal("Asia/Tokyo", result.TimeZone);
        Assert.Equal(Slot(At(Chicago, day, 8), At(Chicago, day, 23)), result.Days[0].Slots[^1]);
        Assert.Null(result.Days[0].OpenUntilUtc);

        // The second day has no slot of its own — and is free until that stretch ends.
        Assert.Empty(result.Days[1].Slots);
        Assert.Equal(At(Chicago, day, 23).ToDateTimeOffset(), result.Days[1].OpenUntilUtc);
    }

    [Theory]
    [InlineData(0, 60, "days must be between 1 and 14")]
    [InlineData(15, 60, "days must be between 1 and 14")]
    [InlineData(7, 14, "minimumMinutes must be between 15 and 1440")]
    [InlineData(7, 1441, "minimumMinutes must be between 15 and 1440")]
    public async Task Days_and_minimum_outside_their_limits_are_refused(int days, int minimumMinutes, string expected)
    {
        var response = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([9165], 9166, 9167, null, days, minimumMinutes));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task The_limits_themselves_are_accepted()
    {
        var response = await Client.PostAsJsonAsync("/calendar/free-slots", new FreeSlotsRequest(
            [9168], 9169, 9170, null, FreeSlotLimits.MaxDays, FreeSlotLimits.MaxMinimumMinutes));
        response.EnsureSuccessStatusCode();

        response = await Client.PostAsJsonAsync("/calendar/free-slots", new FreeSlotsRequest(
            [9168], 9169, 9170, null, 1, FreeSlotLimits.MinMinimumMinutes));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Zero_or_too_many_user_ids_are_refused()
    {
        var empty = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([], 9171, 9172, null, 7, 60));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var tooMany = await Client.PostAsJsonAsync("/calendar/free-slots", new FreeSlotsRequest(
            [.. Enumerable.Range(9200, FreeSlotLimits.MaxUsers + 1).Select(i => (long)i)], 9171, 9172, null, 7, 60));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("between 1 and 50", (await tooMany.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        // A body with no userIds at all deserializes to null — still a 400, not a 500.
        var missing = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new { requesterId = 9171, guildId = 9172, days = 7, minimumMinutes = 60 });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Duplicate_user_ids_are_answered_once()
    {
        var result = await GetAsync(new FreeSlotsRequest([9175, 9175, 9175], 9176, 9177, null, 1, 60));

        Assert.Equal([new FreeSlotMemberDto(9175, FreeSlotMemberState.NotCounted)], result.Members);
    }

    [Fact]
    public async Task One_provider_query_per_linked_member_covers_the_whole_range_and_a_day_more()
    {
        const long guildId = 9180, caller = 9181, member = 9182;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedConnectionAsync(member, "access-slots-9182");
        var day = DayAhead(Chicago);

        await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), FreeSlotLimits.MaxDays, 60));

        // Fourteen days are one query, not fourteen — from the first day's midnight to the midnight
        // one day after the last, so a slot starting late on the last day shows its real end.
        var call = Assert.Single(fixture.Provider.FreeBusyCalls, c => c.AccessToken == "access-slots-9182");
        Assert.Equal(At(Chicago, day, 0), call.Start);
        Assert.Equal(At(Chicago, day.PlusDays(FreeSlotLimits.MaxDays + 1), 0), call.End);
    }

    [Fact]
    public async Task A_range_starting_today_is_asked_for_from_the_next_quarter_hour_not_from_midnight()
    {
        const long guildId = 9185, caller = 9186, member = 9187;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedConnectionAsync(member, "access-slots-9187");

        var before = SystemClock.Instance.GetCurrentInstant();
        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, "today", 1, 15));

        var call = Assert.Single(fixture.Provider.FreeBusyCalls, c => c.AccessToken == "access-slots-9187");
        Assert.Equal(0, call.Start.ToUnixTimeSeconds() % (15 * 60));
        Assert.InRange(call.Start, before, before + Duration.FromMinutes(16));

        // Nothing offered starts in the past.
        Assert.All(result.Days.SelectMany(d => d.Slots), slot => Assert.True(slot.StartUtc >= before.ToDateTimeOffset()));
    }

    [Fact]
    public async Task A_linked_member_with_saved_hours_is_counted_on_those_hours()
    {
        const long guildId = 9400, caller = 9401, member = 9402;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);

        // An evening on the first day's weekday, a late morning on the next, and no other day.
        await SeedProfileAsync(member, "America/Chicago", Week(
            (day.DayOfWeek, 1080, 1320), (day.PlusDays(1).DayOfWeek, 600, 720)));
        await SeedConnectionAsync(member, "access-slots-9402");
        fixture.Provider.BusyBlocksByAccessToken["access-slots-9402"] = [(At(Chicago, day, 19), At(Chicago, day, 20))];

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 3, 60));

        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.Counted)], result.Members);
        Assert.Equal(1, result.CountedMembers);
        Assert.Equal(
            [Slot(At(Chicago, day, 18), At(Chicago, day, 19)), Slot(At(Chicago, day, 20), At(Chicago, day, 22))],
            result.Days[0].Slots);
        Assert.Equal([Slot(At(Chicago, day.PlusDays(1), 10), At(Chicago, day.PlusDays(1), 12))], result.Days[1].Slots);

        // A weekday without a range is not available at all.
        Assert.Empty(result.Days[2].Slots);
        Assert.Null(result.Days[2].OpenUntilUtc);
    }

    [Fact]
    public async Task Saved_hours_and_no_calendar_count_a_member_on_the_hours_alone()
    {
        const long guildId = 9410, caller = 9411, member = 9412;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(member, "America/Chicago", Week((day.DayOfWeek, 540, 1020)));
        var providerCalls = fixture.Provider.FreeBusyCalls.Count;

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 1, 60));

        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.HoursOnly)], result.Members);
        Assert.Equal(1, result.CountedMembers);
        Assert.Equal([Slot(At(Chicago, day, 9), At(Chicago, day, 17))], result.Days.Single().Slots);

        // There is no calendar, so none was asked.
        Assert.Equal(providerCalls, fixture.Provider.FreeBusyCalls.Count);
    }

    [Fact]
    public async Task Saved_hours_and_the_default_meet_each_in_its_members_own_timezone()
    {
        const long guildId = 9420, caller = 9421, inBerlin = 9422, onTheDefault = 9423;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedProfileAsync(caller, "America/Chicago");
        await SeedProfileAsync(inBerlin, "Europe/Berlin", EveryDay(840, 1320));     // 2 PM–10 PM in Berlin
        await SeedConnectionAsync(inBerlin, "access-slots-9422");
        await SeedConnectionAsync(onTheDefault, "access-slots-9423");               // no profile: server time, default hours
        var day = DayAhead(Chicago);

        var result = await GetAsync(new FreeSlotsRequest([inBerlin, onTheDefault], caller, guildId, Iso(day), 1, 60));

        Assert.Equal(
            [
                new FreeSlotMemberDto(inBerlin, FreeSlotMemberState.Counted),
                new FreeSlotMemberDto(onTheDefault, FreeSlotMemberState.DefaultHours),
            ],
            result.Members);
        Assert.Equal(2, result.CountedMembers);

        // From 8 AM in Chicago, where the default hours begin, to 10 PM in Berlin, where the saved ones end.
        Assert.Equal([Slot(At(Chicago, day, 8), At(Berlin, day, 22))], result.Days.Single().Slots);
    }

    [Fact]
    public async Task Hours_from_midnight_to_midnight_with_no_calendar_are_one_slot_across_the_range()
    {
        const long guildId = 9430, caller = 9431, member = 9432;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedProfileAsync(member, "America/Chicago", EveryDay(0, 1440));
        var day = DayAhead(Chicago);

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 3, 60));

        // One stretch from the first midnight to the end of the day after the last one asked for,
        // listed under the day it starts on; the days it runs through say until when they are free.
        var end = At(Chicago, day.PlusDays(4), 0);
        Assert.Equal([Slot(At(Chicago, day, 0), end)], result.Days[0].Slots);
        Assert.Empty(result.Days[1].Slots);
        Assert.Empty(result.Days[2].Slots);
        Assert.Equal(end.ToDateTimeOffset(), result.Days[1].OpenUntilUtc);
        Assert.Equal(end.ToDateTimeOffset(), result.Days[2].OpenUntilUtc);

        // The hours run on; the slot ends there only because the lookup stopped looking. The
        // answer names that instant, so the reply can say "or later".
        Assert.Equal(end.ToDateTimeOffset(), result.LookedUntilUtc);
    }

    [Fact]
    public async Task The_answer_says_how_far_the_lookup_looked_and_an_ordinary_slot_ends_short_of_it()
    {
        const long guildId = 9435, caller = 9436, member = 9437;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedConnectionAsync(member, "access-slots-9437");
        var day = DayAhead(Chicago);

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 2, 60));

        // One day past the last day listed, at the caller's midnight.
        var lookedUntil = At(Chicago, day.PlusDays(3), 0).ToDateTimeOffset();
        Assert.Equal(lookedUntil, result.LookedUntilUtc);
        Assert.All(result.Days.SelectMany(d => d.Slots), slot => Assert.True(slot.EndUtc < lookedUntil));
    }

    [Theory]
    [InlineData(9442, false, FreeSlotMemberState.ReconnectRequired)]    // the provider turned the refresh down
    [InlineData(9443, true, FreeSlotMemberState.Error)]                 // the provider could not be reached
    public async Task A_linked_calendar_that_cannot_be_read_is_not_made_up_for_by_saved_hours(
        long member, bool couldNotBeReached, FreeSlotMemberState expected)
    {
        const long guildId = 9440, caller = 9441;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedProfileAsync(member, "America/Chicago", EveryDay(540, 1020));
        var expired = SystemClock.Instance.GetCurrentInstant() - Duration.FromMinutes(5);
        await SeedConnectionAsync(member, $"access-slots-{member}", expired, $"refresh-slots-{member}");
        fixture.Provider.RefreshOverrides[$"refresh-slots-{member}"] = couldNotBeReached
            ? CalendarTokenRefreshResult.Error("timeout")
            : CalendarTokenRefreshResult.ReconnectRequired("invalid_grant");

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(DayAhead(Chicago)), 1, 60));

        // The calendar exists; a list that ignored it would say more than is known.
        Assert.Equal([new FreeSlotMemberDto(member, expected)], result.Members);
        Assert.Equal(0, result.CountedMembers);
        Assert.Empty(result.Days);
    }

    [Fact]
    public async Task By_name_another_member_who_has_saved_hours_is_listed_and_the_answer_says_it_was_by_name()
    {
        const long guildId = 9450, caller = 9451, member = 9452;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(member, "America/Chicago", Week((day.DayOfWeek, 540, 1020)));

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 1, 60, ByName: true));

        Assert.True(result.ByName);
        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.HoursOnly)], result.Members);
        Assert.Equal([Slot(At(Chicago, day, 9), At(Chicago, day, 17))], result.Days.Single().Slots);
    }

    [Theory]
    [InlineData(9462, true, true)]      // a calendar linked and a profile, but no saved hours
    [InlineData(9463, false, true)]     // a profile and nothing else
    [InlineData(9464, false, false)]    // someone CalCrony has never seen
    public async Task By_name_another_member_without_saved_hours_is_refused_and_no_calendar_is_asked(
        long member, bool linked, bool hasProfile)
    {
        const long guildId = 9460, caller = 9461;
        await SeedGuildAsync(guildId, "America/Chicago");
        if (hasProfile)
        {
            await SeedProfileAsync(member, "America/Chicago");
        }

        if (linked)
        {
            await SeedConnectionAsync(member, $"access-slots-{member}");
        }

        var providerCalls = fixture.Provider.FreeBusyCalls.Count;

        var response = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([member], caller, guildId, null, 7, 60, ByName: true));

        // One text for all three: the refusal tells the caller nothing else about the member.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            FreeSlotsService.NamedMemberHasNoSavedHours, (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Equal(providerCalls, fixture.Provider.FreeBusyCalls.Count);
    }

    [Fact]
    public void The_by_name_refusal_says_how_hours_are_saved()
    {
        Assert.Equal(
            "That member hasn't saved their available hours, so their free slots can't be listed by name. " +
            "They can save them with `/settings available-hours set` or under My settings on the web. " +
            "To see when a group is free, pick a role.",
            FreeSlotsService.NamedMemberHasNoSavedHours);
    }

    [Fact]
    public async Task The_by_name_refusal_comes_before_the_first_day_is_read()
    {
        const long guildId = 9470, caller = 9471, member = 9472;
        await SeedGuildAsync(guildId, "America/Chicago");

        var response = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([member], caller, guildId, "banana", 7, 60, ByName: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            FreeSlotsService.NamedMemberHasNoSavedHours, (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task A_stored_week_that_cannot_be_read_is_no_saved_hours_for_a_lookup_by_name()
    {
        const long guildId = 9475, caller = 9476, member = 9477;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedProfileAsync(member, "America/Chicago", "not a week");

        var byName = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([member], caller, guildId, null, 7, 60, ByName: true));
        Assert.Equal(HttpStatusCode.BadRequest, byName.StatusCode);

        // Through a role the same member is simply not counted: nothing to go on, and no failure.
        var throughARole = await GetAsync(new FreeSlotsRequest([member], caller, guildId, null, 7, 60));
        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.NotCounted)], throughARole.Members);
    }

    [Fact]
    public async Task By_name_the_requester_is_listed_with_or_without_saved_hours()
    {
        const long guildId = 9480, caller = 9481;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedConnectionAsync(caller, "access-slots-9481");

        var result = await GetAsync(new FreeSlotsRequest([caller], caller, guildId, Iso(DayAhead(Chicago)), 1, 60, ByName: true));

        Assert.True(result.ByName);
        Assert.Equal([new FreeSlotMemberDto(caller, FreeSlotMemberState.DefaultHours)], result.Members);
    }

    [Fact]
    public async Task Without_by_name_one_member_is_answered_as_a_role_lookup_is()
    {
        // A role one member holds is still a role lookup: no saved hours are asked of them.
        const long guildId = 9485, caller = 9486, member = 9487;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedConnectionAsync(member, "access-slots-9487");

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(DayAhead(Chicago)), 1, 60));

        Assert.False(result.ByName);
        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.DefaultHours)], result.Members);
    }

    [Fact]
    public async Task By_name_takes_exactly_one_member()
    {
        const long guildId = 9490, caller = 9491, member = 9492;
        await SeedGuildAsync(guildId, "America/Chicago");
        await SeedProfileAsync(member, "America/Chicago", EveryDay(540, 1020));

        var two = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([member, 9493], caller, guildId, null, 7, 60, ByName: true));
        Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);
        Assert.Equal("A lookup by name takes exactly one member.", (await two.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        // The same member sent twice is one member.
        var twice = await GetAsync(new FreeSlotsRequest([member, member], caller, guildId, null, 7, 60, ByName: true));
        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.HoursOnly)], twice.Members);
    }

    // CalCrony's own events as busy time. The members here are on saved hours of 9 AM to 5 PM
    // with no calendar linked unless a test says otherwise, so an event is all that can cut them.

    [Theory]
    [InlineData(9500, null)]
    [InlineData(9510, "Not going")]
    [InlineData(9520, "Maybe")]
    [InlineData(9530, "Going")]
    public async Task An_event_cuts_its_creators_slots_whatever_rsvp_they_gave_themself(long guildId, string? ownRsvp)
    {
        long caller = guildId + 1, creator = guildId + 2;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(540, 1020));
        var eventId = await SeedEventAsync(guildId, creator, At(Chicago, day, 12));
        if (ownRsvp is not null)
        {
            await SeedRsvpAsync(eventId, creator, ownRsvp);
        }

        var providerCalls = fixture.Provider.FreeBusyCalls.Count;

        var result = await GetAsync(new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60));

        // Counted as before, on hours alone, and cut by the two hours of their event.
        Assert.Equal([new FreeSlotMemberDto(creator, FreeSlotMemberState.HoursOnly)], result.Members);
        Assert.Equal(1, result.CountedMembers);
        Assert.Equal(
            [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 14), At(Chicago, day, 17))],
            result.Days.Single().Slots);

        // There is no calendar, so none was asked.
        Assert.Equal(providerCalls, fixture.Provider.FreeBusyCalls.Count);
    }

    [Theory]
    [InlineData(9540, "Going", false, true)]
    [InlineData(9550, "Going", true, false)]        // waitlisted: no seat
    [InlineData(9560, "Not going", false, false)]
    [InlineData(9570, "Maybe", false, false)]
    public async Task Only_a_seat_on_the_attending_option_ties_a_member_to_an_event_they_did_not_create(
        long guildId, string option, bool waitlisted, bool cut)
    {
        long caller = guildId + 1, host = guildId + 2, member = guildId + 3;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(member, "America/Chicago", EveryDay(540, 1020));
        var eventId = await SeedEventAsync(guildId, host, At(Chicago, day, 12));
        await SeedRsvpAsync(eventId, member, option, waitlisted);

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 1, 60));

        Assert.Equal(
            cut
                ? [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 14), At(Chicago, day, 17))]
                : [Slot(At(Chicago, day, 9), At(Chicago, day, 17))],
            result.Days.Single().Slots);
    }

    [Theory]
    [InlineData(9580, EventStatus.Scheduled, true)]
    [InlineData(9590, EventStatus.Started, true)]
    [InlineData(9600, EventStatus.Ended, false)]
    [InlineData(9610, EventStatus.Cancelled, false)]
    public async Task Only_a_scheduled_or_started_event_counts(long guildId, EventStatus status, bool cut)
    {
        long caller = guildId + 1, creator = guildId + 2;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(540, 1020));
        await SeedEventAsync(guildId, creator, At(Chicago, day, 12), status: status);

        var result = await GetAsync(new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60));

        Assert.Equal(
            cut
                ? [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 14), At(Chicago, day, 17))]
                : [Slot(At(Chicago, day, 9), At(Chicago, day, 17))],
            result.Days.Single().Slots);
    }

    [Fact]
    public async Task A_deleted_event_gives_the_time_back_on_the_next_lookup()
    {
        const long guildId = 9620, caller = 9621, creator = 9622;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(540, 1020));
        var eventId = await SeedEventAsync(guildId, creator, At(Chicago, day, 12));
        var request = new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60);

        var before = await GetAsync(request);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.Events.Remove(await db.Events.SingleAsync(e => e.Id == eventId));
            await db.SaveChangesAsync();
        }

        var after = await GetAsync(request);

        // Nothing is kept between lookups: the row is gone, so the time is free again.
        Assert.Equal(
            [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 14), At(Chicago, day, 17))],
            before.Days.Single().Slots);
        Assert.Equal([Slot(At(Chicago, day, 9), At(Chicago, day, 17))], after.Days.Single().Slots);
    }

    [Fact]
    public async Task Events_and_series_count_only_in_the_server_they_belong_to()
    {
        const long here = 9630, elsewhere = 9635, caller = 9631, creator = 9632;
        await SeedGuildAsync(here, "America/Chicago");
        await SeedGuildAsync(elsewhere, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(540, 1020));

        // In the other server: an event at noon, and a weekly series whose next date is the same
        // day at 3 PM.
        await SeedEventAsync(elsewhere, creator, At(Chicago, day, 12));
        await SeedSeriesAsync(elsewhere, creator, anchor: day.PlusDays(-7), cursor: day.PlusDays(-7), new LocalTime(15, 0));

        var inThisServer = await GetAsync(new FreeSlotsRequest([creator], caller, here, Iso(day), 1, 60));
        var inTheOther = await GetAsync(new FreeSlotsRequest([creator], caller, elsewhere, Iso(day), 1, 60));

        // Neither reaches a list made here; both cut the list made there.
        Assert.Equal([Slot(At(Chicago, day, 9), At(Chicago, day, 17))], inThisServer.Days.Single().Slots);
        Assert.Equal(
            [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 14), At(Chicago, day, 15))],
            inTheOther.Days.Single().Slots);
    }

    [Fact]
    public async Task With_a_custom_option_set_only_the_option_marked_as_attending_counts()
    {
        const long guildId = 9750, caller = 9751, host = 9752, healer = 9753, tank = 9754;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(healer, "America/Chicago", EveryDay(540, 1020));
        await SeedProfileAsync(tank, "America/Chicago", EveryDay(540, 1020));

        // The attending option is the second one by sort order: the mark decides, not the order.
        var eventId = await SeedEventAsync(
            guildId, host, At(Chicago, day, 12),
            options:
            [
                new RsvpOption { Id = Guid.NewGuid(), Emote = "T", Label = "Tank", SortOrder = 0 },
                new RsvpOption { Id = Guid.NewGuid(), Emote = "H", Label = "Healer", SortOrder = 1, IsAttending = true },
            ]);
        await SeedRsvpAsync(eventId, healer, "Healer");
        await SeedRsvpAsync(eventId, tank, "Tank");

        var forTheHealer = await GetAsync(new FreeSlotsRequest([healer], caller, guildId, Iso(day), 1, 60));
        var forTheTank = await GetAsync(new FreeSlotsRequest([tank], caller, guildId, Iso(day), 1, 60));

        Assert.Equal(
            [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 14), At(Chicago, day, 17))],
            forTheHealer.Days.Single().Slots);
        Assert.Equal([Slot(At(Chicago, day, 9), At(Chicago, day, 17))], forTheTank.Days.Single().Slots);
    }

    [Fact]
    public async Task An_event_with_no_length_set_counts_sixty_minutes()
    {
        const long guildId = 9640, caller = 9641, creator = 9642;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(540, 1020));
        await SeedEventAsync(guildId, creator, At(Chicago, day, 12), durationMinutes: null);

        var result = await GetAsync(new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60));

        Assert.Equal(
            [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 13), At(Chicago, day, 17))],
            result.Days.Single().Slots);
    }

    [Fact]
    public async Task An_event_that_began_before_the_first_day_and_runs_into_it_cuts_the_start_of_the_day()
    {
        const long guildId = 9650, caller = 9651, creator = 9652;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(540, 1020));

        // Sixteen hours, ending at noon. Counted back from noon, so that a clock change in the
        // night between cannot move the end.
        await SeedEventAsync(guildId, creator, At(Chicago, day, 12) - Duration.FromHours(16), durationMinutes: 16 * 60);

        var result = await GetAsync(new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60));

        Assert.Equal([Slot(At(Chicago, day, 12), At(Chicago, day, 17))], result.Days.Single().Slots);
    }

    [Fact]
    public async Task An_event_of_the_longest_length_that_began_four_weeks_earlier_still_counts()
    {
        const long guildId = 9660, caller = 9661, creator = 9662;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(540, 1020));

        // Four weeks long, the most an event can be given, and ending at 10 AM on the first day:
        // it began an hour less than four weeks before the 9 AM the member's hours start at.
        var end = At(Chicago, day, 10);
        await SeedEventAsync(
            guildId, creator, end - Duration.FromMinutes(FieldLimits.MaxMinutes), durationMinutes: FieldLimits.MaxMinutes,
            status: EventStatus.Started);

        var result = await GetAsync(new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60));

        Assert.Equal([Slot(end, At(Chicago, day, 17))], result.Days.Single().Slots);
    }

    [Fact]
    public async Task An_event_on_the_day_after_the_last_one_ends_a_slot_that_runs_to_it()
    {
        const long guildId = 9670, caller = 9671, creator = 9672;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);

        // Midnight to midnight every day: without the event, one slot to the lookup's edge.
        await SeedProfileAsync(creator, "America/Chicago", EveryDay(0, 1440));
        await SeedEventAsync(guildId, creator, At(Chicago, day.PlusDays(1), 2), durationMinutes: 60);

        var result = await GetAsync(new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60));

        // The lookup reads a day past the last day listed, events included, so the slot shows
        // its real end.
        Assert.Equal([Slot(At(Chicago, day, 0), At(Chicago, day.PlusDays(1), 2))], result.Days.Single().Slots);
    }

    [Fact]
    public async Task Events_never_make_a_member_counted_and_with_nobody_counted_none_is_read()
    {
        const long guildId = 9680, caller = 9681, creator = 9682;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedEventAsync(guildId, creator, At(Chicago, day, 12));

        var (response, eventReads) = await PostCountingEventReadsAsync(
            new FreeSlotsRequest([creator], caller, guildId, Iso(day), 1, 60));

        // No hours saved and no calendar linked: nothing to go on, whatever events they have.
        var result = (await response.Content.ReadFromJsonAsync<FreeSlotsResponse>())!;
        Assert.Equal([new FreeSlotMemberDto(creator, FreeSlotMemberState.NotCounted)], result.Members);
        Assert.Equal(0, result.CountedMembers);
        Assert.Empty(result.Days);
        Assert.Empty(eventReads);
    }

    [Fact]
    public async Task The_by_name_refusal_is_the_same_text_and_reads_no_event_rsvp_or_series()
    {
        const long guildId = 9690, caller = 9691, member = 9692;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);

        // A profile and no saved hours — and an event, a seat and a running series that a
        // lookup would otherwise read.
        await SeedProfileAsync(member, "America/Chicago");
        var eventId = await SeedEventAsync(guildId, member, At(Chicago, day, 12));
        await SeedRsvpAsync(eventId, member, "Going");
        await SeedSeriesAsync(guildId, member, anchor: day, cursor: day, new LocalTime(18, 0));

        var (response, eventReads) = await PostCountingEventReadsAsync(
            new FreeSlotsRequest([member], caller, guildId, Iso(day), 1, 60, ByName: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            FreeSlotsService.NamedMemberHasNoSavedHours, (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Empty(eventReads);
    }

    [Fact]
    public async Task A_role_lookup_cuts_each_member_by_their_own_events()
    {
        const long guildId = 9700, caller = 9701, first = 9702, second = 9703;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(first, "America/Chicago", EveryDay(540, 1020));
        await SeedProfileAsync(second, "America/Chicago", EveryDay(540, 1020));
        await SeedEventAsync(guildId, first, At(Chicago, day, 10), durationMinutes: 60);
        await SeedEventAsync(guildId, second, At(Chicago, day, 14), durationMinutes: 60);

        var result = await GetAsync(new FreeSlotsRequest([first, second], caller, guildId, Iso(day), 1, 60));

        // The time both are free: each one's event is cut out of what they share.
        Assert.Equal(2, result.CountedMembers);
        Assert.Equal(
            [
                Slot(At(Chicago, day, 9), At(Chicago, day, 10)),
                Slot(At(Chicago, day, 11), At(Chicago, day, 14)),
                Slot(At(Chicago, day, 15), At(Chicago, day, 17)),
            ],
            result.Days.Single().Slots);
    }

    [Fact]
    public async Task A_series_later_date_cuts_its_creators_slots_and_not_an_attendees()
    {
        const long guildId = 9710, caller = 9711, gameMaster = 9712, player = 9713;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(gameMaster, "America/Chicago", EveryDay(540, 1380));
        await SeedProfileAsync(player, "America/Chicago", EveryDay(540, 1380));

        // A weekly session at 6 PM, two hours long. Its first occurrence exists and the player is going.
        var seriesId = await SeedSeriesAsync(guildId, gameMaster, anchor: day, cursor: day, new LocalTime(18, 0));
        var occurrence = await SeedEventAsync(guildId, gameMaster, At(Chicago, day, 18), seriesId: seriesId);
        await SeedRsvpAsync(occurrence, player, "Going");

        // A second series of the game master's that was stopped: it has no later dates.
        await SeedSeriesAsync(guildId, gameMaster, anchor: day, cursor: day, new LocalTime(12, 0), ended: true);

        async Task<IReadOnlyList<FreeSlotDto>> SlotsAsync(long member, LocalDate firstDay) =>
            (await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(firstDay), 1, 60))).Days.Single().Slots;

        // The occurrence that exists is an event: busy for its creator and for whoever is going.
        FreeSlotDto[] cutThisWeek = [Slot(At(Chicago, day, 9), At(Chicago, day, 18)), Slot(At(Chicago, day, 20), At(Chicago, day, 23))];
        Assert.Equal(cutThisWeek, await SlotsAsync(gameMaster, day));
        Assert.Equal(cutThisWeek, await SlotsAsync(player, day));

        // A week on there is no occurrence yet. Its time is busy for the member who created the
        // series, and nobody holds a seat in it.
        var nextWeek = day.PlusDays(7);
        Assert.Equal(
            [Slot(At(Chicago, nextWeek, 9), At(Chicago, nextWeek, 18)), Slot(At(Chicago, nextWeek, 20), At(Chicago, nextWeek, 23))],
            await SlotsAsync(gameMaster, nextWeek));
        Assert.Equal([Slot(At(Chicago, nextWeek, 9), At(Chicago, nextWeek, 23))], await SlotsAsync(player, nextWeek));
    }

    [Fact]
    public async Task A_series_occurrence_moved_past_the_next_slot_leaves_that_slot_free()
    {
        const long guildId = 9760, caller = 9761, gameMaster = 9762;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(gameMaster, "America/Chicago", EveryDay(540, 1380));

        // Weekly at 6 PM. This week's occurrence was moved, on its own, to the day after next
        // week's slot.
        var nextWeek = day.PlusDays(7);
        var movedTo = nextWeek.PlusDays(1);
        var seriesId = await SeedSeriesAsync(guildId, gameMaster, anchor: day, cursor: day, new LocalTime(18, 0));
        await SeedEventAsync(guildId, gameMaster, At(Chicago, movedTo, 18), seriesId: seriesId);

        var result = await GetAsync(new FreeSlotsRequest([gameMaster], caller, guildId, Iso(nextWeek), 2, 60));

        // The scheduler creates the next occurrence when the moved one ends, so next week's slot
        // will never exist: that evening is free. The day after is cut by the occurrence itself.
        Assert.Equal([Slot(At(Chicago, nextWeek, 9), At(Chicago, nextWeek, 23))], result.Days[0].Slots);
        Assert.Equal(
            [Slot(At(Chicago, movedTo, 9), At(Chicago, movedTo, 18)), Slot(At(Chicago, movedTo, 20), At(Chicago, movedTo, 23))],
            result.Days[1].Slots);
    }

    [Fact]
    public async Task A_member_with_a_linked_calendar_is_cut_by_their_calendar_and_their_events_together()
    {
        const long guildId = 9780, caller = 9781, member = 9782;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedConnectionAsync(member, "access-slots-9782");
        fixture.Provider.BusyBlocksByAccessToken["access-slots-9782"] = [(At(Chicago, day, 10), At(Chicago, day, 11))];
        await SeedEventAsync(guildId, member, At(Chicago, day, 15));

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 1, 60));

        // No hours saved, so the default ones, with the calendar's block and the event both cut out.
        Assert.Equal([new FreeSlotMemberDto(member, FreeSlotMemberState.DefaultHours)], result.Members);
        Assert.Equal(
            [
                Slot(At(Chicago, day, 8), At(Chicago, day, 10)),
                Slot(At(Chicago, day, 11), At(Chicago, day, 15)),
                Slot(At(Chicago, day, 17), At(Chicago, day, 23)),
            ],
            result.Days.Single().Slots);
    }

    [Fact]
    public async Task With_no_option_marked_as_attending_the_first_by_sort_order_is_the_one_that_counts()
    {
        const long guildId = 9790, caller = 9791, host = 9792, early = 9793, late = 9794;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(early, "America/Chicago", EveryDay(540, 1020));
        await SeedProfileAsync(late, "America/Chicago", EveryDay(540, 1020));

        // A set with no option marked, as a row from before the mark could be: the rule everywhere
        // else takes the option first by sort order, and so does a lookup. It is not the first
        // option stored.
        var eventId = await SeedEventAsync(
            guildId, host, At(Chicago, day, 12),
            options:
            [
                new RsvpOption { Id = Guid.NewGuid(), Emote = "L", Label = "Late", SortOrder = 1 },
                new RsvpOption { Id = Guid.NewGuid(), Emote = "E", Label = "Early", SortOrder = 0 },
            ]);
        await SeedRsvpAsync(eventId, early, "Early");
        await SeedRsvpAsync(eventId, late, "Late");

        var forEarly = await GetAsync(new FreeSlotsRequest([early], caller, guildId, Iso(day), 1, 60));
        var forLate = await GetAsync(new FreeSlotsRequest([late], caller, guildId, Iso(day), 1, 60));

        Assert.Equal(
            [Slot(At(Chicago, day, 9), At(Chicago, day, 12)), Slot(At(Chicago, day, 14), At(Chicago, day, 17))],
            forEarly.Days.Single().Slots);
        Assert.Equal([Slot(At(Chicago, day, 9), At(Chicago, day, 17))], forLate.Days.Single().Slots);
    }

    [Fact]
    public async Task A_series_reaches_the_projection_as_it_is_stored()
    {
        const long guildId = 9800, creator = 9802;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);

        // Every field differs from each field of the same type, so one read into the wrong place
        // gives other dates: every second week on two weekdays, with a count and an until date, in
        // a zone, at a start time and with a length of its own. The weeks it falls on are counted
        // from its first date, a week before the last one created, as after a change of its rule.
        var schedule = new SeriesSchedule(
            Unit: RecurrenceUnit.Week,
            Interval: 2,
            MonthlyMode: MonthlyMode.NthWeekday,
            DaysOfWeek: DayOf(day) | DayOf(day.PlusDays(2)),
            AnchorDate: day.PlusDays(-7),
            StartTime: new LocalTime(18, 30),
            TimeZone: "Europe/Berlin",
            UntilDate: day.PlusDays(45),
            MaxOccurrences: 5,
            CurrentOccurrenceDate: day,
            OccurrenceCount: 1,
            DurationMinutes: 150);
        await SeedSeriesAsync(guildId, creator, schedule);

        var now = SystemClock.Instance.GetCurrentInstant();
        var (windowStart, windowEnd) = (At(Chicago, day, 0), At(Chicago, day.PlusDays(60), 0));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var busy = await scope.ServiceProvider.GetRequiredService<EventBusyTimes>().GetAsync(
            guildId, [creator], now, windowStart, windowEnd, CancellationToken.None);

        var expected = SeriesProjection.LaterOccurrences(schedule, live: null, now, windowStart, windowEnd);
        Assert.True(expected.Count >= 2, $"The projection gives {expected.Count} occurrences.");
        Assert.Equal(expected, busy[creator]);
    }

    [Fact]
    public async Task A_session_the_scheduler_creates_while_a_lookup_reads_is_busy_for_its_creator_wherever_it_lands()
    {
        var day = DayAhead(Chicago);
        var lastWeek = day.PlusDays(-7);

        // The scheduler's step — last week's session ends and this week's is created — lands just
        // before a different read of the lookup each time: before its first read of events, RSVPs,
        // options or series, before its second, and so on. Wherever it lands, this week's session
        // is busy time for the member who created the series.
        for (var read = 1; read <= 5; read++)
        {
            long guildId = 9810 + (10 * read), caller = guildId + 1, gameMaster = guildId + 2;
            await SeedGuildAsync(guildId, "America/Chicago");
            await SeedProfileAsync(gameMaster, "America/Chicago", EveryDay(540, 1380));

            // Weekly at 6 PM. Last week's session is over, and the sweep has not moved the series on yet.
            var seriesId = await SeedSeriesAsync(guildId, gameMaster, anchor: lastWeek, cursor: lastWeek, new LocalTime(18, 0));
            var over = await SeedEventAsync(
                guildId, gameMaster, At(Chicago, lastWeek, 18), status: EventStatus.Started, seriesId: seriesId);

            // Someone else's event at noon that the game master is going to, so that the lookup
            // reads seats and options as well: five reads in all.
            var noon = await SeedEventAsync(guildId, guildId + 3, At(Chicago, day, 12));
            await SeedRsvpAsync(noon, gameMaster, "Going");

            var seen = 0;
            var landsBefore = read;
            var landed = false;
            fixture.Commands.BeforeReader = async command =>
            {
                if (!EventTables.Any(command.Contains) || ++seen != landsBefore)
                {
                    return;
                }

                fixture.Commands.BeforeReader = null;
                landed = true;
                await MoveSeriesOnAsync(seriesId, over, day, At(Chicago, day, 18));
            };

            IReadOnlyList<FreeSlotDto> slots;
            try
            {
                slots = (await GetAsync(new FreeSlotsRequest([gameMaster], caller, guildId, Iso(day), 1, 60))).Days.Single().Slots;
            }
            finally
            {
                fixture.Commands.BeforeReader = null;
            }

            Assert.True(landed, $"The step never landed before read {read}.");
            FreeSlotDto[] expected =
            [
                Slot(At(Chicago, day, 9), At(Chicago, day, 12)),
                Slot(At(Chicago, day, 14), At(Chicago, day, 18)),
                Slot(At(Chicago, day, 20), At(Chicago, day, 23)),
            ];
            Assert.True(
                slots.SequenceEqual(expected),
                $"With the step before read {read}: {string.Join(", ", slots.Select(slot => $"{slot.StartUtc:t}-{slot.EndUtc:t}"))}");
        }
    }

    [Fact]
    public async Task The_events_cost_five_reads_whether_one_member_is_looked_up_for_a_day_or_three_for_fourteen()
    {
        const long guildId = 9720, caller = 9721, first = 9722, second = 9723, third = 9724;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        foreach (var member in new[] { first, second, third })
        {
            await SeedProfileAsync(member, "America/Chicago", EveryDay(540, 1020));
        }

        // Everything a lookup can read, twice over: two events with seats, and two series, each
        // with its occurrence. A read per event, per seated event or per series would show.
        var eventId = await SeedEventAsync(guildId, first, At(Chicago, day, 12));
        await SeedRsvpAsync(eventId, first, "Going");
        await SeedRsvpAsync(eventId, second, "Going");
        var otherEventId = await SeedEventAsync(guildId, third, At(Chicago, day, 9), durationMinutes: 60);
        await SeedRsvpAsync(otherEventId, first, "Going");
        foreach (var hour in new[] { 15, 18 })
        {
            var seriesId = await SeedSeriesAsync(guildId, first, anchor: day, cursor: day, new LocalTime(hour, 0));
            await SeedEventAsync(guildId, first, At(Chicago, day, hour), durationMinutes: 60, seriesId: seriesId);
        }

        var (oneResponse, oneMemberOneDay) = await PostCountingEventReadsAsync(
            new FreeSlotsRequest([first], caller, guildId, Iso(day), 1, 60));
        var (threeResponse, threeMembersFourteenDays) = await PostCountingEventReadsAsync(
            new FreeSlotsRequest([first, second, third], caller, guildId, Iso(day), 14, 60));

        // The events in range, the seats, the options, the series and their live occurrences:
        // five reads, not one per member, day or event.
        Assert.Equal(HttpStatusCode.OK, oneResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, threeResponse.StatusCode);
        Assert.Equal(5, oneMemberOneDay.Count);
        Assert.Equal(5, threeMembersFourteenDays.Count);
    }

    [Fact]
    public async Task An_event_counts_once_for_a_member_however_many_ties_they_have_to_it()
    {
        const long guildId = 9730, creator = 9732, bystander = 9733;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);

        // Created it, holds a seat in it, and gave a second RSVP on an event that allows several.
        var eventId = await SeedEventAsync(guildId, creator, At(Chicago, day, 12), allowMultipleRsvps: true);
        await SeedRsvpAsync(eventId, creator, "Going");
        await SeedRsvpAsync(eventId, creator, "Maybe");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var busy = await scope.ServiceProvider.GetRequiredService<EventBusyTimes>().GetAsync(
            guildId, [creator, bystander], SystemClock.Instance.GetCurrentInstant(),
            At(Chicago, day, 13), At(Chicago, day, 20), CancellationToken.None);

        // One interval, given whole although the window starts inside it; the calculation clips.
        Assert.Equal([(At(Chicago, day, 12), At(Chicago, day, 14))], busy[creator]);
        Assert.Empty(busy[bystander]);
    }

    [Fact]
    public async Task The_answer_says_that_events_were_counted_even_when_there_are_none()
    {
        const long guildId = 9740, caller = 9741, member = 9742;
        await SeedGuildAsync(guildId, "America/Chicago");
        var day = DayAhead(Chicago);
        await SeedProfileAsync(member, "America/Chicago", EveryDay(540, 1020));

        var result = await GetAsync(new FreeSlotsRequest([member], caller, guildId, Iso(day), 1, 60));

        Assert.True(result.EventsCounted);
        Assert.Equal([Slot(At(Chicago, day, 9), At(Chicago, day, 17))], result.Days.Single().Slots);
    }

    /// <summary>A first day beyond the horizon: a refusal with its reason, not a 500 — and the
    /// member's calendar is never asked. Natural language reaches any year.</summary>
    private async Task AssertRefusedAsTooFarAheadAsync(string when, long member)
    {
        var accessToken = $"access-slots-{member}";
        await SeedConnectionAsync(member, accessToken);

        var response = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([member], 9193, 9194, when, 14, 60));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("more than a year away", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.DoesNotContain(fixture.Provider.FreeBusyCalls, call => call.AccessToken == accessToken);
    }

    private async Task<FreeSlotsResponse> GetAsync(FreeSlotsRequest request)
    {
        var response = await Client.PostAsJsonAsync("/calendar/free-slots", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FreeSlotsResponse>())!;
    }

    private static LocalDate Today(DateTimeZone zone) => SystemClock.Instance.GetCurrentInstant().InZone(zone).Date;

    /// <summary>A day far enough ahead that "now" cannot reach into it.</summary>
    private static LocalDate DayAhead(DateTimeZone zone) => Today(zone).PlusDays(3);

    /// <summary>ISO text for a day — the one form the recognizer reads without guessing a year.</summary>
    private static string Iso(LocalDate day) => day.ToString("uuuu-MM-dd", CultureInfo.InvariantCulture);

    private static Instant At(DateTimeZone zone, LocalDate day, int hour, int minute = 0) =>
        day.At(new LocalTime(hour, minute)).InZoneLeniently(zone).ToInstant();

    private static FreeSlotDto Slot(Instant start, Instant end) => new(start.ToDateTimeOffset(), end.ToDateTimeOffset());

    private async Task SeedGuildAsync(long guildId, string timeZone)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();

        // Theory rows share the fixture's database, so a guild may already be there.
        if (await db.Guilds.FindAsync(guildId) is null)
        {
            db.Guilds.Add(new Guild { Id = guildId, TimeZone = timeZone });
            await db.SaveChangesAsync();
        }
    }

    private async Task SeedProfileAsync(long userId, string timeZone, string? availableHours = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        db.UserProfiles.Add(new UserProfile { Id = userId, TimeZone = timeZone, AvailableHoursJson = availableHours });
        await db.SaveChangesAsync();
    }

    /// <summary>A stored week with a range on each of the given weekdays and no other day.</summary>
    private static string Week(params (IsoDayOfWeek Day, int StartMinute, int EndMinute)[] days) =>
        new WeeklyHours(days.ToDictionary(d => d.Day, d => (d.StartMinute, d.EndMinute))).Serialize();

    /// <summary>A stored week with the same range on all seven days.</summary>
    private static string EveryDay(int startMinute, int endMinute) =>
        Week([.. Enumerable.Range((int)IsoDayOfWeek.Monday, 7).Select(day => ((IsoDayOfWeek)day, startMinute, endMinute))]);

    private async Task SeedConnectionAsync(long userId, string accessToken, Instant? expiresAt = null, string? refreshToken = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CalendarTokenProtector>();
        var now = SystemClock.Instance.GetCurrentInstant();
        db.CalendarConnections.Add(new CalendarConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = CalendarProvider.Google,
            EncryptedAccessToken = protector.Protect(accessToken),
            EncryptedRefreshToken = protector.Protect(refreshToken ?? $"refresh-{userId}"),
            AccessTokenExpiresAt = expiresAt ?? now + Duration.FromHours(2),
            ConnectedAt = now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Posts a lookup and gives its response together with the commands it sent that read
    /// an event, an RSVP, an option or a series — what the answer itself does not show.</summary>
    private async Task<(HttpResponseMessage Response, IReadOnlyList<string> EventReads)> PostCountingEventReadsAsync(
        FreeSlotsRequest request)
    {
        var mark = fixture.Commands.Count;
        var response = await Client.PostAsJsonAsync("/calendar/free-slots", request);
        return (response, [.. fixture.Commands.Since(mark).Where(command => EventTables.Any(command.Contains))]);
    }

    /// <summary>An event two hours long, with the default options — Going (the attending one),
    /// Not going, Maybe — unless the test says otherwise.</summary>
    private async Task<Guid> SeedEventAsync(
        long guildId, long creatorId, Instant startsAt, int? durationMinutes = 120,
        EventStatus status = EventStatus.Scheduled, Guid? seriesId = null, bool allowMultipleRsvps = false,
        List<RsvpOption>? options = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var ev = new Event
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatorId = creatorId,
            Title = "Session",
            StartsAt = startsAt,
            TimeZone = "America/Chicago",
            DurationMinutes = durationMinutes,
            ChannelId = 9001,
            Status = status,
            SeriesId = seriesId,
            AllowMultipleRsvps = allowMultipleRsvps,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            Options = options ?? RsvpPolicy.OptionsFromTemplate(null),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    /// <summary>An RSVP on the option with the given label; waitlisted when the test says so.</summary>
    private async Task SeedRsvpAsync(Guid eventId, long userId, string option, bool waitlisted = false)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var optionId = await db.RsvpOptions
            .Where(o => o.EventId == eventId && o.Label == option)
            .Select(o => o.Id)
            .SingleAsync();
        db.Rsvps.Add(new Rsvp
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            UserId = userId,
            OptionId = optionId,
            Waitlisted = waitlisted,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A weekly series in Chicago time, two hours long, on its anchor's weekday. The
    /// cursor is the slot date of the last occurrence created.</summary>
    private Task<Guid> SeedSeriesAsync(
        long guildId, long creatorId, LocalDate anchor, LocalDate cursor, LocalTime startTime, bool ended = false) =>
        SeedSeriesAsync(
            guildId,
            creatorId,
            new SeriesSchedule(
                Unit: RecurrenceUnit.Week,
                Interval: 1,
                MonthlyMode: MonthlyMode.DayOfMonth,
                DaysOfWeek: RecurrenceDays.None,
                AnchorDate: anchor,
                StartTime: startTime,
                TimeZone: "America/Chicago",
                UntilDate: null,
                MaxOccurrences: null,
                CurrentOccurrenceDate: cursor,
                OccurrenceCount: 1,
                DurationMinutes: 120),
            ended);

    /// <summary>A series stored with the given schedule.</summary>
    private async Task<Guid> SeedSeriesAsync(long guildId, long creatorId, SeriesSchedule schedule, bool ended = false)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var series = new EventSeries
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatorId = creatorId,
            Unit = schedule.Unit,
            Interval = schedule.Interval,
            MonthlyMode = schedule.MonthlyMode,
            DaysOfWeek = schedule.DaysOfWeek,
            AnchorDate = schedule.AnchorDate,
            StartTime = schedule.StartTime,
            TimeZone = schedule.TimeZone,
            UntilDate = schedule.UntilDate,
            MaxOccurrences = schedule.MaxOccurrences,
            CurrentOccurrenceDate = schedule.CurrentOccurrenceDate,
            OccurrenceCount = schedule.OccurrenceCount,
            Ended = ended,
            Title = "Weekly session",
            DurationMinutes = schedule.DurationMinutes,
            ChannelId = 9001,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        };
        db.EventSeries.Add(series);
        await db.SaveChangesAsync();
        return series.Id;
    }

    /// <summary>What the scheduler does once a series' live occurrence is over: that one ends, and
    /// the next is created at the slot given and becomes the series' live occurrence. Two saves,
    /// so that the series never has two live occurrences; the lookup a test pauses runs no read
    /// between them.</summary>
    private async Task MoveSeriesOnAsync(Guid seriesId, Guid overId, LocalDate nextDate, Instant nextStart)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var over = await db.Events.SingleAsync(e => e.Id == overId);
        over.Status = EventStatus.Ended;
        await db.SaveChangesAsync();

        var series = await db.EventSeries.SingleAsync(s => s.Id == seriesId);
        series.CurrentOccurrenceDate = nextDate;
        series.OccurrenceCount++;
        db.Events.Add(new Event
        {
            Id = Guid.NewGuid(),
            GuildId = series.GuildId,
            CreatorId = series.CreatorId,
            Title = series.Title,
            StartsAt = nextStart,
            TimeZone = series.TimeZone,
            DurationMinutes = series.DurationMinutes,
            ChannelId = series.ChannelId,
            Status = EventStatus.Scheduled,
            SeriesId = series.Id,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            Options = RsvpPolicy.OptionsFromTemplate(null),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The weekly day-set flag for a date's weekday.</summary>
    private static RecurrenceDays DayOf(LocalDate date) => (RecurrenceDays)(1 << ((int)date.DayOfWeek - 1));
}
