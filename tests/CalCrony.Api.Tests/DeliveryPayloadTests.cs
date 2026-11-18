using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>A delivery's payload column: no length limit of its own, and what a failed save
/// says about the rows it names.</summary>
public class DeliveryPayloadTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task The_payload_column_has_no_length_limit_of_its_own()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();

        var property = db.Model.FindEntityType(typeof(Delivery))!.FindProperty(nameof(Delivery.PayloadJson))!;
        Assert.Null(property.GetMaxLength());
        Assert.Equal("text", property.GetColumnType());

        Assert.Equal(("text", null), await ColumnAsync(db));
    }

    [Fact]
    public async Task A_long_payload_is_stored_and_served_whole()
    {
        var payload = JsonSerializer.Serialize(new ReminderPayload(77, new string('x', 20_000)));
        var id = await AddDeliveryAsync(payload);

        var pending = (await fixture.Client.GetFromJsonAsync<List<DeliveryDto>>("/deliveries/pending?limit=50"))!;

        var served = Assert.Single(pending, d => d.Id == id);
        Assert.Equal(payload, served.PayloadJson);
        Assert.True(served.PayloadJson.Length > 20_000);
    }

    [Fact]
    public void A_failed_save_that_names_no_rows_says_so() =>
        Assert.Equal("none named", SaveFailure.Describe(new DbUpdateException("could not be saved")));

    [Fact]
    public async Task A_failed_save_names_each_row_by_entity_key_and_state_and_not_by_what_it_holds()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var delivery = NewDelivery("""{"Text":"what the row holds"}""");
        var added = db.Add(delivery);
        var modified = db.Attach(new GuildRole { GuildId = 5, RoleId = 7, Name = "a role's name" });
        modified.State = EntityState.Modified;

        var text = SaveFailure.Describe(new DbUpdateException("could not be saved", new InvalidOperationException(), [added, modified]));

        // The row that was being changed comes first: it is stored and can be looked up.
        Assert.Equal($"GuildRole 5/7 (Modified), Delivery {delivery.Id} (Added)", text);
    }

    [Fact]
    public async Task A_failed_save_names_five_rows_and_counts_the_rest()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var deliveries = Enumerable.Range(0, 7).Select(_ => NewDelivery("{}")).ToList();
        var entries = deliveries.Select(d => db.Add(d)).ToList();

        var five = SaveFailure.Describe(new DbUpdateException("could not be saved", new InvalidOperationException(), [.. entries.Take(5)]));
        var seven = SaveFailure.Describe(new DbUpdateException("could not be saved", new InvalidOperationException(), entries));

        var named = string.Join(", ", deliveries.Take(5).Select(d => $"Delivery {d.Id} (Added)"));
        Assert.Equal(named, five);
        Assert.Equal($"{named} and 2 more", seven);
    }

    [Fact]
    public async Task A_failed_save_names_the_rows_being_changed_or_removed_before_the_rows_being_added()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var deliveries = Enumerable.Range(0, 6).Select(_ => NewDelivery("{}")).ToList();
        var entries = deliveries.Select(d => db.Add(d)).ToList();
        var changed = db.Attach(new GuildRole { GuildId = 5, RoleId = 7 });
        changed.State = EntityState.Modified;
        var removed = db.Attach(new GuildRole { GuildId = 5, RoleId = 8 });
        removed.State = EntityState.Deleted;

        // The failure gives the six new rows first, as a sweep's save does.
        var text = SaveFailure.Describe(new DbUpdateException(
            "could not be saved", new InvalidOperationException(), [.. entries, changed, removed]));

        // The two stored rows are named first, in the failure's order, then three of the six
        // new ones, also in its order; the rest are counted.
        var three = string.Join(", ", deliveries.Take(3).Select(d => $"Delivery {d.Id} (Added)"));
        Assert.Equal($"GuildRole 5/7 (Modified), GuildRole 5/8 (Deleted), {three} and 3 more", text);
    }

    private static Delivery NewDelivery(string payloadJson)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.Reminder,
            ChannelId = 9301,
            PayloadJson = payloadJson,
            DueAt = now.Minus(Duration.FromMinutes(1)),
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        };
    }

    private async Task<Guid> AddDeliveryAsync(string payloadJson)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var delivery = NewDelivery(payloadJson);
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync();
        return delivery.Id;
    }

    /// <summary>The payload column as the database has it: its type and its length limit.</summary>
    internal static async Task<(string Type, int? MaxLength)> ColumnAsync(CalCronyDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select data_type, character_maximum_length from information_schema.columns
                where table_name = 'Deliveries' and column_name = 'PayloadJson'
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
