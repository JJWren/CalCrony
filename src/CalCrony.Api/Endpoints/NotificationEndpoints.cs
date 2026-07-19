using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Endpoints;

/// <summary>Scheduled pre-event notification endpoints (max 5 per event; scoped edits on series occurrences).</summary>
public static class NotificationEndpoints
{
    private const int MaxPerEvent = 5;

    /// <summary>Maps notification routes.</summary>
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/{id:guid}/notifications", List);
        app.MapPost("/events/{id:guid}/notifications", Create);
        app.MapDelete("/events/{id:guid}/notifications/{notificationId:guid}", Delete);
    }

    /// <summary>Lists an event's notifications, soonest-relative first.</summary>
    private static async Task<IResult> List(
        HttpContext context, GuildAccessService access, Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await EventEndpoints.GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        var notifications = await db.EventNotifications
            .Where(n => n.EventId == id)
            .OrderByDescending(n => n.MinutesBefore)
            .ToListAsync(cancellationToken);
        return Results.Ok(notifications.Select(ToDto));
    }

    /// <summary>Adds a notification; on a live series occurrence a Scope is required and Series also writes the template spec.</summary>
    private static async Task<IResult> Create(
        HttpContext context,
        GuildAccessService access,
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
            .Include(e => e.Series!).ThenInclude(s => s.NotificationSpecs)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await EventEndpoints.GuardEventMutateAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (ev.Notifications.Count >= MaxPerEvent)
        {
            return Results.Conflict(new ErrorResponse($"An event can have at most {MaxPerEvent} notifications."));
        }

        // Ask-per-edit, same rule as event PATCHes: a live occurrence of an active series must
        // say whether the notification is one-off or belongs on the series template.
        var series = SeriesForScopedEdit(ev);
        if (series is not null && request.Scope is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "This event repeats — specify whether to change this occurrence or the whole series."));
        }

        SeriesNotification? spec = null;
        if (series is not null && request.Scope == EditScope.Series)
        {
            if (series.NotificationSpecs.Count >= MaxPerEvent)
            {
                return Results.Conflict(new ErrorResponse($"A series can have at most {MaxPerEvent} notifications."));
            }

            spec = new SeriesNotification
            {
                Id = Guid.NewGuid(),
                SeriesId = series.Id,
                MinutesBefore = request.MinutesBefore,
                Message = request.Message,
                Mentions = request.Mentions,
                ChannelId = request.ChannelId,
            };
            db.SeriesNotifications.Add(spec);
        }

        var notification = new EventNotification
        {
            Id = Guid.NewGuid(),
            EventId = ev.Id,
            MinutesBefore = request.MinutesBefore,
            Message = request.Message,
            Mentions = request.Mentions,
            ChannelId = request.ChannelId,
            SeriesNotificationId = spec?.Id,
        };
        db.EventNotifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/events/{ev.Id}/notifications/{notification.Id}", ToDto(notification));
    }

    /// <summary>Removes a notification; Series scope also retires the linked template spec.</summary>
    private static async Task<IResult> Delete(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        Guid notificationId,
        CalCronyDbContext db,
        CancellationToken cancellationToken,
        EditScope? scope = null)
    {
        var ev = await db.Events.Include(e => e.Series).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await EventEndpoints.GuardEventMutateAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (SeriesForScopedEdit(ev) is not null && scope is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "This event repeats — specify whether to change this occurrence or the whole series."));
        }

        var notification = await db.EventNotifications
            .FirstOrDefaultAsync(n => n.EventId == id && n.Id == notificationId, cancellationToken);
        if (notification is null)
        {
            return Results.NotFound();
        }

        db.EventNotifications.Remove(notification);

        // Series scope also retires the linked template spec so future occurrences drop it.
        // Diverged rows (null lineage) only have the event row to remove — the template is untouched.
        if (scope == EditScope.Series && notification.SeriesNotificationId is { } specId)
        {
            await db.SeriesNotifications.Where(s => s.Id == specId).ExecuteDeleteAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The series that scoped-edit rules apply to: the event must be its live occurrence
    /// and the series must still be running.</summary>
    private static EventSeries? SeriesForScopedEdit(Event ev) =>
        ev.Series is { Ended: false } series
        && ev.Status is EventStatus.Scheduled or EventStatus.Started
            ? series
            : null;

    /// <summary>Projects a notification row to its DTO.</summary>
    private static EventNotificationDto ToDto(EventNotification n) =>
        new(n.Id, n.EventId, n.MinutesBefore, n.Message, n.Mentions, n.ChannelId);
}
