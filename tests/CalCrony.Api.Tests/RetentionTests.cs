using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The retention purge deletes done rows past the window and never touches pending
/// deliveries or young rows.</summary>
public class RetentionTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 19, 12, 0);
    private static readonly Instant Old = Now.Minus(Duration.FromDays(91));
    private static readonly Instant Young = Now.Minus(Duration.FromDays(89));

    [Fact]
    public async Task Purges_old_done_rows_and_keeps_pending_and_young_ones()
    {
        var oldSent = NewDelivery(DeliveryStatus.Sent, Old);
        var oldFailed = NewDelivery(DeliveryStatus.Failed, Old);
        var oldPending = NewDelivery(DeliveryStatus.Pending, Old); // stuck rows stay visible
        var youngSent = NewDelivery(DeliveryStatus.Sent, Young);

        var oldLogin = new WebLoginState
        {
            Id = Guid.NewGuid(), Token = $"t{Guid.NewGuid():N}"[..30], ReturnUrl = "/",
            CreatedAt = Old, ExpiresAt = Old.Plus(Duration.FromMinutes(10)), ConsumedAt = Old,
        };
        var youngLogin = new WebLoginState
        {
            Id = Guid.NewGuid(), Token = $"t{Guid.NewGuid():N}"[..30], ReturnUrl = "/",
            CreatedAt = Young, ExpiresAt = Young.Plus(Duration.FromMinutes(10)),
        };
        var oldRefresh = new WebRefreshToken
        {
            Id = Guid.NewGuid(), UserId = 1, TokenHash = $"h{Guid.NewGuid():N}"[..30],
            CreatedAt = Old, ExpiresAt = Old.Plus(Duration.FromDays(30)),
        };
        var youngRefresh = new WebRefreshToken
        {
            Id = Guid.NewGuid(), UserId = 1, TokenHash = $"h{Guid.NewGuid():N}"[..30],
            CreatedAt = Young, ExpiresAt = Young.Plus(Duration.FromDays(30)),
        };
        var oldLink = new CalendarLinkToken
        {
            Id = Guid.NewGuid(), UserId = 1, Token = $"l{Guid.NewGuid():N}"[..30],
            CreatedAt = Old, ExpiresAt = Old.Plus(Duration.FromMinutes(10)), ConsumedAt = Old,
        };

        await using (var seed = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.AddRange(oldSent, oldFailed, oldPending, youngSent, oldLogin, youngLogin, oldRefresh, youngRefresh, oldLink);
            await db.SaveChangesAsync();
        }

        int purged;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            purged = await retention.PurgeAsync(Now, CancellationToken.None);
        }

        Assert.True(purged >= 5); // the five old done rows (other tests may add their own)

        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.False(await verifyDb.Deliveries.AnyAsync(d => d.Id == oldSent.Id || d.Id == oldFailed.Id));
        Assert.True(await verifyDb.Deliveries.AnyAsync(d => d.Id == oldPending.Id));  // never purged
        Assert.True(await verifyDb.Deliveries.AnyAsync(d => d.Id == youngSent.Id));   // inside window
        Assert.False(await verifyDb.WebLoginStates.AnyAsync(s => s.Id == oldLogin.Id));
        Assert.True(await verifyDb.WebLoginStates.AnyAsync(s => s.Id == youngLogin.Id));
        Assert.False(await verifyDb.WebRefreshTokens.AnyAsync(t => t.Id == oldRefresh.Id));
        Assert.True(await verifyDb.WebRefreshTokens.AnyAsync(t => t.Id == youngRefresh.Id));
        Assert.False(await verifyDb.CalendarLinkTokens.AnyAsync(t => t.Id == oldLink.Id));
    }

    [Fact]
    public async Task Purge_is_a_noop_when_everything_is_young()
    {
        var young = NewDelivery(DeliveryStatus.Sent, Young);
        await using (var seed = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.Add(young);
            await db.SaveChangesAsync();
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
        await retention.PurgeAsync(Now.Minus(Duration.FromDays(2)), CancellationToken.None);

        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.True(await verifyDb.Deliveries.AnyAsync(d => d.Id == young.Id));
    }

    private static Delivery NewDelivery(DeliveryStatus status, Instant createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = DeliveryType.Reminder,
        ChannelId = 1,
        PayloadJson = "{}",
        DueAt = createdAt,
        Status = status,
        CreatedAt = createdAt,
    };
}
