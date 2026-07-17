using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class CalendarAvailabilityTests(CalendarApiFixture fixture) : IClassFixture<CalendarApiFixture>
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Mixed_connected_and_unconnected_users_report_correct_status()
    {
        const long freeUser = 2001, busyUser = 2002, unconnectedUser = 2003;
        var now = SystemClock.Instance.GetCurrentInstant();
        var windowStart = now + Duration.FromHours(1);
        var windowEnd = windowStart + Duration.FromHours(1);

        await SeedConnectionAsync(freeUser, "access-free-2001", now + Duration.FromHours(2));
        await SeedConnectionAsync(busyUser, "access-busy-2002", now + Duration.FromHours(2));
        fixture.Provider.BusyBlocksByAccessToken["access-busy-2002"] =
            [(windowStart + Duration.FromMinutes(15), windowStart + Duration.FromMinutes(45))];

        var response = await Client.PostAsJsonAsync("/calendar/availability", new AvailabilityRequest(
            [freeUser, busyUser, unconnectedUser], windowStart.ToDateTimeOffset(), windowEnd.ToDateTimeOffset()));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<AvailabilityResponse>())!;

        Assert.Equal(CalendarAvailabilityStatus.Free, Single(result, freeUser).Status);
        Assert.Equal(CalendarAvailabilityStatus.Busy, Single(result, busyUser).Status);
        Assert.Single(Single(result, busyUser).BusyBlocks);
        Assert.Equal(CalendarAvailabilityStatus.NotConnected, Single(result, unconnectedUser).Status);
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_and_persisted()
    {
        const long userId = 2010;
        var now = SystemClock.Instance.GetCurrentInstant();
        await SeedConnectionAsync(userId, "access-stale-2010", now - Duration.FromMinutes(5), "refresh-stale-2010");
        fixture.Provider.BusyBlocksByAccessToken["refreshed-refresh-stale-2010"] = [];

        var windowStart = now + Duration.FromHours(1);
        var response = await Client.PostAsJsonAsync("/calendar/availability", new AvailabilityRequest(
            [userId], windowStart.ToDateTimeOffset(), (windowStart + Duration.FromHours(1)).ToDateTimeOffset()));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<AvailabilityResponse>())!;

        Assert.Equal(CalendarAvailabilityStatus.Free, Single(result, userId).Status);
        Assert.True(fixture.Provider.RefreshCallCount > 0);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CalendarTokenProtector>();
        var connection = await db.CalendarConnections.SingleAsync(c => c.UserId == userId);
        Assert.Equal("refreshed-refresh-stale-2010", protector.Unprotect(connection.EncryptedAccessToken));
        Assert.True(connection.AccessTokenExpiresAt > now);
    }

    [Fact]
    public async Task Zero_or_too_many_user_ids_are_rejected()
    {
        var now = DateTimeOffset.UtcNow;

        var empty = await Client.PostAsJsonAsync("/calendar/availability", new AvailabilityRequest([], now, now.AddHours(1)));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var tooMany = await Client.PostAsJsonAsync("/calendar/availability",
            new AvailabilityRequest([.. Enumerable.Range(3100, 51).Select(i => (long)i)], now, now.AddHours(1)));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task One_users_reconnect_required_does_not_affect_others_in_the_same_request()
    {
        const long okUser = 2020, brokenUser = 2021;
        var now = SystemClock.Instance.GetCurrentInstant();
        await SeedConnectionAsync(okUser, "access-ok-2020", now + Duration.FromHours(2));
        await SeedConnectionAsync(brokenUser, "access-broken-2021", now - Duration.FromMinutes(1), "refresh-broken-2021");
        fixture.Provider.RefreshOverrides["refresh-broken-2021"] =
            CalendarTokenRefreshResult.ReconnectRequired("invalid_grant");

        var windowStart = now + Duration.FromHours(1);
        var response = await Client.PostAsJsonAsync("/calendar/availability", new AvailabilityRequest(
            [okUser, brokenUser], windowStart.ToDateTimeOffset(), (windowStart + Duration.FromHours(1)).ToDateTimeOffset()));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<AvailabilityResponse>())!;

        Assert.Equal(CalendarAvailabilityStatus.Free, Single(result, okUser).Status);
        Assert.Equal(CalendarAvailabilityStatus.ReconnectRequired, Single(result, brokenUser).Status);
    }

    private static UserAvailabilityDto Single(AvailabilityResponse response, long userId) =>
        response.Results.Single(r => r.UserId == userId);

    private async Task SeedConnectionAsync(long userId, string accessToken, Instant expiresAt, string? refreshToken = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CalendarTokenProtector>();
        db.CalendarConnections.Add(new CalendarConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = CalendarProvider.Google,
            EncryptedAccessToken = protector.Protect(accessToken),
            EncryptedRefreshToken = protector.Protect(refreshToken ?? $"refresh-{userId}"),
            AccessTokenExpiresAt = expiresAt,
            ConnectedAt = SystemClock.Instance.GetCurrentInstant(),
        });
        await db.SaveChangesAsync();
    }
}
