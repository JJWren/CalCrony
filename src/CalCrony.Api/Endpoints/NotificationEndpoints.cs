using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Endpoints;

public static class NotificationEndpoints
{
    private const int MaxPerEvent = 5;

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/{id:guid}/notifications", List);
        app.MapPost("/events/{id:guid}/notifications", Create);
        app.MapDelete("/events/{id:guid}/notifications/{notificationId:guid}", Delete);
    }

    private static async Task<IResult> List(Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        var notifications = await db.EventNotifications
            .Where(n => n.EventId == id)
            .OrderByDescending(n => n.MinutesBefore)
            .ToListAsync(cancellationToken);
        return Results.Ok(notifications.Select(ToDto));
    }

    private static async Task<IResult> Create(
        Guid id,
        CreateEventNotificationRequest request,
        CalCronyDbContext db,
        CancellationToken cancellationToken)
    {
        if (request.MinutesBefore < 0)
        {
            return Results.BadRequest(new ErrorResponse("minutesBefore must be zero or positive."));
        }

        var ev = await db.Events
            .Include(e => e.Notifications)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (ev.Notifications.Count >= MaxPerEvent)
        {
            return Results.Conflict(new ErrorResponse($"An event can have at most {MaxPerEvent} notifications."));
        }

        var notification = new EventNotification
        {
            Id = Guid.NewGuid(),
            EventId = ev.Id,
            MinutesBefore = request.MinutesBefore,
            Message = request.Message,
            Mentions = request.Mentions,
            ChannelId = request.ChannelId,
        };
        db.EventNotifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/events/{ev.Id}/notifications/{notification.Id}", ToDto(notification));
    }

    private static async Task<IResult> Delete(
        Guid id, Guid notificationId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var deleted = await db.EventNotifications
            .Where(n => n.EventId == id && n.Id == notificationId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static EventNotificationDto ToDto(EventNotification n) =>
        new(n.Id, n.EventId, n.MinutesBefore, n.Message, n.Mentions, n.ChannelId);
}
