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
        app.MapPatch("/series/{id:guid}", UpdateSeries);
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

    /// <summary>Edits a series' rule/end condition under the "apply and (re)activate" invariant:
    /// success always leaves the series running with a computable future occurrence, which makes
    /// reviving an ended series (incl. via an empty PATCH) the same code path as any other edit.
    /// Never touches Event rows — the live occurrence's start time can't move here.</summary>
    private static async Task<IResult> UpdateSeries(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        UpdateSeriesRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
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

        // Validate against effective values; the tracked entity is only mutated after every
        // check passes so a 400 can never leave a half-applied edit.
        var unit = request.Unit ?? series.Unit;
        var interval = request.Interval ?? series.Interval;
        var mode = request.MonthlyMode ?? series.MonthlyMode;

        if (interval is < 1 or > 12)
        {
            return Results.BadRequest(new ErrorResponse("Repeat interval must be between 1 and 12."));
        }

        if (request.End is SeriesEndChoice.Keep or SeriesEndChoice.Never
            && (request.RepeatUntilText is not null || request.RepeatCount is not null))
        {
            return Results.BadRequest(new ErrorResponse("Choose an end option to set an end date or count."));
        }

        if ((request.End == SeriesEndChoice.Until && request.RepeatCount is not null)
            || (request.End == SeriesEndChoice.Count && request.RepeatUntilText is not null))
        {
            return Results.BadRequest(new ErrorResponse("Choose either an end date or a number of times, not both."));
        }

        if (request.End == SeriesEndChoice.Until && request.RepeatUntilText is null)
        {
            return Results.BadRequest(new ErrorResponse("Enter the date the series should stop repeating."));
        }

        if (request.End == SeriesEndChoice.Count && request.RepeatCount is null)
        {
            return Results.BadRequest(new ErrorResponse("Enter how many times the series should run in total."));
        }

        if (request.RepeatCount is < 2 or > 500)
        {
            return Results.BadRequest(new ErrorResponse("Repeat count must be between 2 and 500."));
        }

        var effectiveCount = request.End switch
        {
            SeriesEndChoice.Count => request.RepeatCount,
            SeriesEndChoice.Keep => series.MaxOccurrences,
            _ => null,
        };
        if (effectiveCount is int max && max <= series.OccurrenceCount)
        {
            return Results.BadRequest(new ErrorResponse(
                $"This series has already run {series.OccurrenceCount} times — choose a larger count."));
        }

        var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
        LocalDate? effectiveUntil = request.End switch
        {
            SeriesEndChoice.Keep => series.UntilDate,
            _ => null,
        };
        if (request.End == SeriesEndChoice.Until)
        {
            // The parser already rejects past-only text, so no separate floor check is needed.
            if (!parser.TryResolve(request.RepeatUntilText!, zone, out var untilInstant, out var untilError))
            {
                return Results.BadRequest(new ErrorResponse(untilError!));
            }

            effectiveUntil = untilInstant.InZone(zone).Date;
        }

        var now = clock.GetCurrentInstant();
        if (RecurrenceCalculator.NextOccurrence(
                unit, interval, mode, series.AnchorDate, series.StartTime, zone,
                series.CurrentOccurrenceDate, effectiveUntil, now) is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "These settings leave no upcoming occurrences — use stop to end the series instead."));
        }

        series.Unit = unit;
        series.Interval = interval;
        series.MonthlyMode = mode;
        if (request.End != SeriesEndChoice.Keep)
        {
            series.UntilDate = effectiveUntil;
            series.MaxOccurrences = request.End == SeriesEndChoice.Count ? request.RepeatCount : null;
        }

        series.Ended = false;

        // The summary changed, so a posted live embed needs a re-render (web callers only; the
        // bot edits its message itself). A revived series with no live occurrence has nothing to
        // sync — the sweep's spawn posts a fresh embed via the outbox.
        var liveEventId = await LiveEventIdAsync(db, id, cancellationToken);
        if (liveEventId is { } liveId)
        {
            var live = await db.Events.FirstOrDefaultAsync(e => e.Id == liveId, cancellationToken);
            if (live is not null)
            {
                await EventEndpoints.EnqueueEmbedSyncAsync(context, db, live, clock, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(series.ToDto(liveEventId));
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
