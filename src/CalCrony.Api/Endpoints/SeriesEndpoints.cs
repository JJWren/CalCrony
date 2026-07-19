using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

public static class SeriesEndpoints
{
    public static void MapSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/series/{id:guid}", GetSeries);
        app.MapPost("/series/{id:guid}/stop", StopSeries);
        app.MapPost("/events/{id:guid}/skip", SkipOccurrence);
    }

    private const string ManageMessage = "Only the series creator or a server manager can change this series.";

    private static async Task<IResult> GetSeries(
        HttpContext context, GuildAccessService access, Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var series = await LoadSeriesAsync(db, id, cancellationToken);
        if (series is null)
        {
            return Results.NotFound();
        }

        if (await GuardSeriesReadAsync(context, access, series, cancellationToken) is { } denied)
        {
            return denied;
        }

        return Results.Ok(series.ToDto(await LiveEventIdAsync(db, id, cancellationToken)));
    }

    private static async Task<IResult> StopSeries(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var series = await LoadSeriesAsync(db, id, cancellationToken);
        if (series is null)
        {
            return Results.NotFound();
        }

        if (await EventEndpoints.GuardMutateAsync(
                context, access, series.GuildId, series.CreatorId, ManageMessage, cancellationToken) is { } denied)
        {
            return denied;
        }

        // Idempotent; the live occurrence survives as the final one — "stop repeating" is not
        // "cancel what's scheduled" (that's /delete or /skip on the event).
        var liveEventId = await LiveEventIdAsync(db, id, cancellationToken);
        if (!series.Ended)
        {
            series.Ended = true;
            if (liveEventId is { } liveId)
            {
                var live = await db.Events.FirstOrDefaultAsync(e => e.Id == liveId, cancellationToken);
                if (live is not null)
                {
                    // Web callers hand the 🔁-drop re-render to the outbox; the bot edits itself.
                    await EventEndpoints.EnqueueEmbedSyncAsync(context, db, live, clock, cancellationToken);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(series.ToDto(liveEventId));
    }

    private static async Task<IResult> SkipOccurrence(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        CalCronyDbContext db,
        SeriesMaterializer materializer,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var ev = await db.Events
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

        if (ev.Series is not { } series)
        {
            return Results.BadRequest(new ErrorResponse("This event doesn't repeat."));
        }

        if (ev.Status is not (EventStatus.Scheduled or EventStatus.Started))
        {
            return Results.Conflict(new ErrorResponse("Only the upcoming occurrence can be skipped."));
        }

        var now = clock.GetCurrentInstant();
        ev.Status = EventStatus.Cancelled;

        // Always via the outbox, both caller types — one code path for embed removal, matching
        // the materializer's always-outbox post of the replacement.
        if (ev.MessageId is long messageId)
        {
            db.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(),
                Type = DeliveryType.DeleteEventMessage,
                ChannelId = ev.ChannelId,
                PayloadJson = JsonSerializer.Serialize(new DeleteEventMessagePayload(ev.ChannelId, messageId)),
                DueAt = now,
                Status = DeliveryStatus.Pending,
                CreatedAt = now,
            });
        }

        var next = materializer.MaterializeNext(series, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
        })
        {
            // Lost a race with the scheduler sweep (or another skip) for the live slot.
            return Results.Conflict(new ErrorResponse("The series just advanced — try again."));
        }

        return Results.Ok(new SkipOccurrenceResponse(next?.ToDto(), series.ToDto(next?.Id)));
    }

    /// <summary>Series-read guard: non-members get 404 so series ids can't be probed (mirrors
    /// GuardEventReadAsync).</summary>
    private static async Task<IResult?> GuardSeriesReadAsync(
        HttpContext context, GuildAccessService access, EventSeries series, CancellationToken cancellationToken)
    {
        if (context.User.IsBot())
        {
            return null;
        }

        var userId = context.User.WebUserId();
        if (userId is null)
        {
            return Results.NotFound();
        }

        return await access.CheckAsync(userId.Value, series.GuildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.Member or GuildAccess.Manager => null,
            _ => Results.NotFound(),
        };
    }

    private static Task<EventSeries?> LoadSeriesAsync(CalCronyDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.EventSeries
            .Include(s => s.NotificationSpecs)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    private static async Task<Guid?> LiveEventIdAsync(CalCronyDbContext db, Guid seriesId, CancellationToken cancellationToken)
    {
        var live = await db.Events
            .Where(e => e.SeriesId == seriesId
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started))
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return live;
    }
}
