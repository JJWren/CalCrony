using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        // Phase B: web (JWT) callers get bot-parity mutations — member to create, creator or
        // ManageGuild to edit/delete. SetMessage stays bot-only (only the bot knows message ids).
        app.MapPost("/guilds/{guildId:long}/events", CreateEvent);
        app.MapGet("/guilds/{guildId:long}/events", ListEvents);
        app.MapGet("/events/{id:guid}", GetEvent);
        app.MapPatch("/events/{id:guid}", UpdateEvent);
        app.MapDelete("/events/{id:guid}", DeleteEvent);
        app.MapPut("/events/{id:guid}/message", SetMessage).RequireAuthorization("BotOnly");
        app.MapPut("/events/{id:guid}/rsvps/{userId:long}", PutRsvp);
        app.MapDelete("/events/{id:guid}/rsvps/{userId:long}", DeleteRsvp);
        app.MapPost("/tools/parse-datetime", ParseDateTime);
    }

    /// <summary>Mutation guard for JWT callers: event's guild member AND (creator or manager).
    /// Bot passes. Non-members get 404 (same anti-probing rule as reads).</summary>
    internal static async Task<IResult?> GuardEventMutateAsync(
        HttpContext context, GuildAccessService access, Event ev, CancellationToken cancellationToken)
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

        return await access.CheckAsync(userId.Value, ev.GuildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.Manager => null,
            GuildAccess.Member when ev.CreatorId == userId.Value => null,
            GuildAccess.Member => Results.Json(
                new ErrorResponse("Only the event creator or a server manager can change this event."),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

    /// <summary>Guild-read guard for web callers: bot passes, members pass, others get 403/stale.</summary>
    internal static async Task<IResult?> GuardGuildReadAsync(
        HttpContext context, GuildAccessService access, long guildId, CancellationToken cancellationToken)
    {
        if (context.User.IsBot())
        {
            return null;
        }

        var userId = context.User.WebUserId();
        if (userId is null)
        {
            return GuildAccessService.Forbidden();
        }

        return await access.CheckAsync(userId.Value, guildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.None => GuildAccessService.Forbidden(),
            _ => null,
        };
    }

    /// <summary>Event-read guard: like GuardGuildReadAsync but non-members get 404 so event ids
    /// can't be probed for existence.</summary>
    internal static async Task<IResult?> GuardEventReadAsync(
        HttpContext context, GuildAccessService access, Event ev, CancellationToken cancellationToken)
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

        return await access.CheckAsync(userId.Value, ev.GuildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.Member or GuildAccess.Manager => null,
            _ => Results.NotFound(),
        };
    }

    /// <summary>Enqueue a Discord-embed re-render for web-initiated changes. Bot callers skip
    /// this (the bot edits the message itself); coalesces with an identical pending sync.</summary>
    internal static async Task EnqueueEmbedSyncAsync(
        HttpContext context, CalCronyDbContext db, Event ev, IClock clock, CancellationToken cancellationToken)
    {
        if (context.User.IsBot() || ev.MessageId is null)
        {
            return;
        }

        var payloadJson = JsonSerializer.Serialize(new SyncEventMessagePayload(ev.Id));
        var alreadyQueued = await db.Deliveries.AnyAsync(
            d => d.Type == DeliveryType.SyncEventMessage
                 && d.Status == DeliveryStatus.Pending
                 && d.PayloadJson == payloadJson,
            cancellationToken);
        if (alreadyQueued)
        {
            return;
        }

        var now = clock.GetCurrentInstant();
        db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.SyncEventMessage,
            ChannelId = ev.ChannelId,
            PayloadJson = payloadJson,
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
    }

    private static async Task<IResult> CreateEvent(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CreateEventRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await GetOrCreateGuildAsync(db, guildId, cancellationToken);

        // Web callers can't spoof identity or pick channels: creator is always the JWT subject,
        // and the embed goes to the guild's default channel — creation is blocked without one
        // (a channel-less event would be invisible in Discord with no RSVP buttons).
        var isBot = context.User.IsBot();
        var creatorId = isBot ? request.CreatorId : context.User.WebUserId()!.Value;
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

        var zone = await ResolveZoneAsync(db, creatorId, guild, cancellationToken);
        if (!parser.TryResolve(request.WhenText, zone, out var startsAt, out var error))
        {
            return Results.BadRequest(new ErrorResponse(error!));
        }

        var now = clock.GetCurrentInstant();
        var ev = new Event
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatorId = creatorId,
            Title = request.Title,
            Description = request.Description,
            StartsAt = startsAt,
            TimeZone = zone.Id,
            DurationMinutes = request.DurationMinutes,
            ChannelId = channelId,
            Location = request.Location,
            ImageUrl = request.ImageUrl,
            Status = EventStatus.Scheduled,
            CreatedAt = now,
            Options =
            [
                new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", SortOrder = 0 },
                new RsvpOption { Id = Guid.NewGuid(), Emote = "❌", Label = "Not going", SortOrder = 1 },
                new RsvpOption { Id = Guid.NewGuid(), Emote = "🤔", Label = "Maybe", SortOrder = 2 },
            ],
        };
        db.Events.Add(ev);

        if (!isBot)
        {
            // The bot posts the embed itself on /create; web creates hand that job to the outbox.
            db.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(),
                Type = DeliveryType.PostEventMessage,
                ChannelId = channelId,
                PayloadJson = JsonSerializer.Serialize(new PostEventMessagePayload(ev.Id)),
                DueAt = now,
                Status = DeliveryStatus.Pending,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/events/{ev.Id}", ev.ToDto());
    }

    private static async Task<IResult> ListEvents(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken,
        long? channelId = null,
        int limit = 10,
        bool includePast = false)
    {
        if (await GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        limit = Math.Clamp(limit, 1, 25);
        var query = db.Events
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .Where(e => e.GuildId == guildId && e.Status != EventStatus.Cancelled);

        if (!includePast)
        {
            var now = clock.GetCurrentInstant();
            query = query.Where(e => e.StartsAt >= now);
        }

        if (channelId is not null)
        {
            query = query.Where(e => e.ChannelId == channelId);
        }

        var events = await query.OrderBy(e => e.StartsAt).Take(limit).ToListAsync(cancellationToken);
        return Results.Ok(events.Select(e => e.ToDto()));
    }

    private static async Task<IResult> GetEvent(
        HttpContext context, GuildAccessService access, Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> UpdateEvent(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        UpdateEventRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventMutateAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (request.WhenText is not null)
        {
            var zone = Mapping.FindZone(ev.TimeZone) ?? DateTimeZone.Utc;
            if (!parser.TryResolve(request.WhenText, zone, out var startsAt, out var error))
            {
                return Results.BadRequest(new ErrorResponse(error!));
            }

            ev.StartsAt = startsAt;
        }

        ev.Title = request.Title ?? ev.Title;
        ev.Description = request.Description ?? ev.Description;
        ev.DurationMinutes = request.DurationMinutes ?? ev.DurationMinutes;
        ev.Location = request.Location ?? ev.Location;
        ev.ImageUrl = request.ImageUrl ?? ev.ImageUrl;
        ev.Status = request.Status ?? ev.Status;

        await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> DeleteEvent(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventMutateAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        // Web deletes: capture the posted message's ids before the row dies so the bot can
        // remove the embed. The bot deletes messages itself, so bot callers enqueue nothing.
        if (!context.User.IsBot() && ev.MessageId is long messageId)
        {
            var now = clock.GetCurrentInstant();
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

        db.Events.Remove(ev);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SetMessage(
        Guid id, SetEventMessageRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        ev.ChannelId = request.ChannelId;
        ev.MessageId = request.MessageId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> PutRsvp(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        long userId,
        RsvpRequest request,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        var option = ev.Options.FirstOrDefault(o => o.Id == request.OptionId);
        if (option is null)
        {
            return Results.BadRequest(new ErrorResponse("Unknown RSVP option for this event."));
        }

        var existing = ev.Rsvps.FirstOrDefault(r => r.UserId == userId);
        if (option.Capacity is int capacity &&
            existing?.OptionId != option.Id &&
            ev.Rsvps.Count(r => r.OptionId == option.Id) >= capacity)
        {
            return Results.Conflict(new ErrorResponse($"\"{option.Label}\" is full."));
        }

        if (existing is null)
        {
            var rsvp = new Rsvp
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                UserId = userId,
                OptionId = option.Id,
                CreatedAt = clock.GetCurrentInstant(),
            };
            // Explicit Add: with a client-set Guid key, graph fixup alone would
            // mark this entity as existing and issue an UPDATE instead of INSERT.
            // Fixup then places it into ev.Rsvps for the response DTO.
            db.Rsvps.Add(rsvp);
        }
        else
        {
            existing.OptionId = option.Id;
            existing.CreatedAt = clock.GetCurrentInstant();
        }

        await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> DeleteRsvp(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        long userId,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        var existing = ev.Rsvps.FirstOrDefault(r => r.UserId == userId);
        if (existing is not null)
        {
            db.Rsvps.Remove(existing);
            ev.Rsvps.Remove(existing);
            await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> ParseDateTime(
        HttpContext context,
        GuildAccessService access,
        ParseDateTimeRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        CancellationToken cancellationToken)
    {
        // JWT callers always parse as themselves, and may only reference guilds they're in.
        var effectiveUserId = context.User.IsBot() ? request.UserId : context.User.WebUserId();
        if (!context.User.IsBot() && request.GuildId is long requestedGuild &&
            await GuardGuildReadAsync(context, access, requestedGuild, cancellationToken) is { } denied)
        {
            return denied;
        }

        DateTimeZone zone = DateTimeZone.Utc;
        if (request.GuildId is long guildId)
        {
            var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
            zone = Mapping.FindZone(guild?.TimeZone) ?? zone;
        }

        if (effectiveUserId is long userId)
        {
            var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
            zone = Mapping.FindZone(user?.TimeZone) ?? zone;
        }

        if (!parser.TryResolve(request.Text, zone, out var instant, out var error))
        {
            return Results.BadRequest(new ErrorResponse(error!));
        }

        var utc = instant.ToDateTimeOffset();
        return Results.Ok(new ParseDateTimeResponse(utc, utc.ToUnixTimeSeconds(), zone.Id));
    }

    private static Task<Event?> LoadEventAsync(CalCronyDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Events
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    internal static async Task<Guild> GetOrCreateGuildAsync(
        CalCronyDbContext db, long guildId, CancellationToken cancellationToken)
    {
        var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
        if (guild is null)
        {
            guild = new Guild { Id = guildId };
            db.Guilds.Add(guild);
        }

        return guild;
    }

    internal static async Task<DateTimeZone> ResolveZoneAsync(
        CalCronyDbContext db, long userId, Guild guild, CancellationToken cancellationToken)
    {
        var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
        return Mapping.FindZone(user?.TimeZone) ?? Mapping.FindZone(guild.TimeZone) ?? DateTimeZone.Utc;
    }
}
