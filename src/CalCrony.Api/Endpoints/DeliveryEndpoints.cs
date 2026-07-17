using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

public static class DeliveryEndpoints
{
    /// <summary>Rows fetched this many times without an ack are abandoned as Failed.</summary>
    private const int MaxAttempts = 10;

    public static void MapDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/deliveries/pending", GetPending);
        app.MapPost("/deliveries/{id:guid}/ack", Ack);
        app.MapPost("/reminders", CreateReminder);
    }

    private static async Task<IResult> GetPending(
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken,
        int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 50);
        var now = clock.GetCurrentInstant();

        var due = await db.Deliveries
            .Where(d => d.Status == DeliveryStatus.Pending && d.DueAt <= now)
            .OrderBy(d => d.DueAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var delivery in due)
        {
            delivery.Attempts++;
            if (delivery.Attempts > MaxAttempts)
            {
                delivery.Status = DeliveryStatus.Failed;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(due
            .Where(d => d.Status == DeliveryStatus.Pending)
            .Select(d => new DeliveryDto(d.Id, d.Type, d.ChannelId, d.PayloadJson, d.DueAt.ToDateTimeOffset())));
    }

    private static async Task<IResult> Ack(Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries.FindAsync([id], cancellationToken);
        if (delivery is null)
        {
            return Results.NotFound();
        }

        delivery.Status = DeliveryStatus.Sent;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateReminder(
        CreateReminderRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, request.GuildId, cancellationToken);
        var zone = await EventEndpoints.ResolveZoneAsync(db, request.UserId, guild, cancellationToken);

        if (!parser.TryResolve(request.WhenText, zone, out var fireAt, out var error))
        {
            return Results.BadRequest(new ErrorResponse(error!));
        }

        // Reminders are immutable one-shots, so they go straight into the outbox future-dated.
        var delivery = new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.Reminder,
            ChannelId = request.ChannelId,
            PayloadJson = JsonSerializer.Serialize(new ReminderPayload(request.UserId, request.Text)),
            DueAt = fireAt,
            Status = DeliveryStatus.Pending,
            CreatedAt = clock.GetCurrentInstant(),
        };
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/deliveries/{delivery.Id}",
            new ReminderDto(delivery.Id, delivery.ChannelId, request.Text, fireAt.ToDateTimeOffset()));
    }
}
