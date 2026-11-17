using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The migration that takes the payload column's limit off, taken back and applied
/// again. In a class of its own, so no other test meets the database part-way.</summary>
public class DeliveryPayloadMigrationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Going_back_puts_the_limit_back_and_removes_only_the_payloads_that_would_not_fit()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();
        Delivery With(int payloadLength) => new()
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.Reminder,
            ChannelId = 9401,
            PayloadJson = new string('x', payloadLength),
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        };
        var atTheLimit = With(8192);
        var overTheLimit = With(8193);
        db.Deliveries.AddRange(atTheLimit, overTheLimit);
        await db.SaveChangesAsync();

        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync("AddAvailableHours");
        try
        {
            Assert.Equal(("character varying", 8192), await DeliveryPayloadTests.ColumnAsync(db));
            var left = await db.Deliveries.AsNoTracking().Select(d => d.Id).ToListAsync();
            Assert.Contains(atTheLimit.Id, left);
            Assert.DoesNotContain(overTheLimit.Id, left);
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(("text", null), await DeliveryPayloadTests.ColumnAsync(db));
        Assert.Equal(8192, (await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == atTheLimit.Id)).PayloadJson.Length);
    }
}
