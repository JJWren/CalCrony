using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Outbox endpoints the bot polls, plus reminder creation.</summary>
public static class DeliveryEndpoints
{
    /// <summary>Rows fetched this many times without an ack are abandoned as Failed.</summary>
    private const int MaxAttempts = 10;

    /// <summary>Maps delivery polling/ack and reminder routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        // The outbox itself stays bot-only; reminders are open to guild members (Phase B).
        var group = app.MapGroup("").RequireAuthorization("BotOnly");
        group.MapGet("/deliveries/pending", GetPending);
        group.MapPost("/deliveries/{id:guid}/ack", Ack);
        app.MapPost("/reminders", CreateReminder);
    }

    /// <summary>Returns pending, due deliveries for the bot to post (oldest first).</summary>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
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

    /// <summary>Marks a delivery sent; the bot acks only after the Discord post succeeds.</summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
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

    /// <summary>Creates a future-dated reminder delivery from natural-language text (web callers are self-forced to the default channel).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="parser">The natural-language datetime parser.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> CreateReminder(
        HttpContext context,
        GuildAccessService access,
        CreateReminderRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, request.GuildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, request.GuildId, cancellationToken);

        // Web callers: reminder is always for themselves, and it posts in the default channel
        // (the web has no channel picker by design).
        var isBot = context.User.IsBot();
        var userId = isBot ? request.UserId : context.User.WebUserId()!.Value;
        long channelId;
        if (isBot)
        {
            channelId = request.ChannelId;
        }
        else if (guild.DefaultChannelId is long defaultChannel)
        {
            channelId = defaultChannel;
        }
        else
        {
            return Results.BadRequest(new ErrorResponse(
                "This server has no default events channel yet — a manager must run /settings default-channel in Discord."));
        }

        var zone = await EventEndpoints.ResolveZoneAsync(db, userId, guild, cancellationToken);
        if (!parser.TryResolve(request.WhenText, zone, out var fireAt, out var error))
        {
            return Results.BadRequest(new ErrorResponse(error!));
        }

        // Reminders are immutable one-shots, so they go straight into the outbox future-dated.
        var delivery = new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.Reminder,
            ChannelId = channelId,
            PayloadJson = JsonSerializer.Serialize(new ReminderPayload(userId, request.Text)),
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
