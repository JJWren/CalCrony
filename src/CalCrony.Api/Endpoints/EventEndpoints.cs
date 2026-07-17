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
        app.MapPost("/guilds/{guildId:long}/events", CreateEvent);
        app.MapGet("/guilds/{guildId:long}/events", ListEvents);
        app.MapGet("/events/{id:guid}", GetEvent);
        app.MapPatch("/events/{id:guid}", UpdateEvent);
        app.MapDelete("/events/{id:guid}", DeleteEvent);
        app.MapPut("/events/{id:guid}/message", SetMessage);
        app.MapPut("/events/{id:guid}/rsvps/{userId:long}", PutRsvp);
        app.MapDelete("/events/{id:guid}/rsvps/{userId:long}", DeleteRsvp);
        app.MapPost("/tools/parse-datetime", ParseDateTime);
    }

    private static async Task<IResult> CreateEvent(
        long guildId,
        CreateEventRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var guild = await GetOrCreateGuildAsync(db, guildId, cancellationToken);
        var zone = await ResolveZoneAsync(db, request.CreatorId, guild, cancellationToken);

        if (!parser.TryResolve(request.WhenText, zone, out var startsAt, out var error))
        {
            return Results.BadRequest(new ErrorResponse(error!));
        }

        var ev = new Event
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatorId = request.CreatorId,
            Title = request.Title,
            Description = request.Description,
            StartsAt = startsAt,
            TimeZone = zone.Id,
            DurationMinutes = request.DurationMinutes,
            ChannelId = request.ChannelId,
            Location = request.Location,
            ImageUrl = request.ImageUrl,
            Status = EventStatus.Scheduled,
            CreatedAt = clock.GetCurrentInstant(),
            Options =
            [
                new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", SortOrder = 0 },
                new RsvpOption { Id = Guid.NewGuid(), Emote = "❌", Label = "Not going", SortOrder = 1 },
                new RsvpOption { Id = Guid.NewGuid(), Emote = "🤔", Label = "Maybe", SortOrder = 2 },
            ],
        };

        db.Events.Add(ev);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/events/{ev.Id}", ev.ToDto());
    }

    private static async Task<IResult> ListEvents(
        long guildId,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken,
        long? channelId = null,
        int limit = 10,
        bool includePast = false)
    {
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

    private static async Task<IResult> GetEvent(Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        return ev is null ? Results.NotFound() : Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> UpdateEvent(
        Guid id,
        UpdateEventRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
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

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> DeleteEvent(Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var deleted = await db.Events.Where(e => e.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
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

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> DeleteRsvp(
        Guid id, long userId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        var existing = ev.Rsvps.FirstOrDefault(r => r.UserId == userId);
        if (existing is not null)
        {
            db.Rsvps.Remove(existing);
            ev.Rsvps.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ev.ToDto());
    }

    private static async Task<IResult> ParseDateTime(
        ParseDateTimeRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        CancellationToken cancellationToken)
    {
        DateTimeZone zone = DateTimeZone.Utc;
        if (request.GuildId is long guildId)
        {
            var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
            zone = Mapping.FindZone(guild?.TimeZone) ?? zone;
        }

        if (request.UserId is long userId)
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
