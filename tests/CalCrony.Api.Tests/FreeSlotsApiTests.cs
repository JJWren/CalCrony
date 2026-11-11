using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>POST /calendar/free-slots end to end on the fake calendar provider. The API runs on the
/// real clock, so each test asks for a concrete day a few days ahead in the zone under test —
/// "now" then cuts nothing, and the expected instants do not depend on when the suite runs.</summary>
public class FreeSlotsApiTests(CalendarApiFixture fixture) : IClassFixture<CalendarApiFixture>
{
    private static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];
    private static readonly DateTimeZone Berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];

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

        var slots = await GetAsync(new FreeSlotsRequest([guildId + 50], caller, guildId, null, 1, 60));
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

    [Theory]
    [InlineData("year 5000", 9190)]
    [InlineData("in 416018 weeks", 9191)]   // lands in the last days of year 9999, where day arithmetic overflows
    public async Task A_first_day_more_than_a_year_away_is_refused_not_looked_up(string when, long member)
    {
        var accessToken = $"access-slots-{member}";
        await SeedConnectionAsync(member, accessToken);

        var response = await Client.PostAsJsonAsync(
            "/calendar/free-slots", new FreeSlotsRequest([member], 9193, 9194, when, 14, 60));

        // Natural language reaches any year. A refusal, not a 500 — and no calendar is asked.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("more than a year away", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.DoesNotContain(fixture.Provider.FreeBusyCalls, call => call.AccessToken == accessToken);
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

    private async Task SeedProfileAsync(long userId, string timeZone)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        db.UserProfiles.Add(new UserProfile { Id = userId, TimeZone = timeZone });
        await db.SaveChangesAsync();
    }

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
}
