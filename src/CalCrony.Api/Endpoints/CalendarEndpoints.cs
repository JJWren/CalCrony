using System.Security.Cryptography;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Calendar linking and availability endpoints (linking is per-user; availability checks are BotOnly).</summary>
public static class CalendarEndpoints
{
    private const int LinkTokenExpiryMinutes = 10;

    /// <summary>Google's own hard per-request calendar-item limit on the freeBusy API.</summary>
    private const int MaxUsersPerQuery = 50;

    /// <summary>Maps calendar connection and availability routes.</summary>
    public static void MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/calendar/connections/{userId:long}/link-token", CreateLinkToken);
        app.MapGet("/calendar/connections/{userId:long}", GetStatus);
        app.MapDelete("/calendar/connections/{userId:long}", Disconnect);
        // BotOnly: accepts arbitrary UserIds — exposing it to web callers would let any
        // signed-in user probe any Discord user's free/busy. Web uses the event-scoped route.
        app.MapPost("/calendar/availability", CheckAvailability).RequireAuthorization("BotOnly");
        app.MapGet("/events/{id:guid}/availability", GetEventAvailability);
    }

    /// <summary>Web callers may only manage their own calendar connection.</summary>
    private static IResult? GuardSelf(HttpContext context, long userId) =>
        !context.User.IsBot() && context.User.WebUserId() != userId
            ? GuildAccessService.SelfOnly()
            : null;

    /// <summary>Mints a single-use link token and the provider consent URL for a user (self-only for web callers).</summary>
    private static async Task<IResult> CreateLinkToken(
        HttpContext context,
        long userId,
        CalCronyDbContext db,
        IConfiguration configuration,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (GuardSelf(context, userId) is { } denied)
        {
            return denied;
        }

        // Both are required to mint a usable link: without PublicBaseUrl the StartUrl would be a
        // relative path, useless when pasted into Discord. ErrorResponse shape (not RFC 7807) so
        // CalCronyApiClient.SendAsync surfaces the message rather than a generic status code.
        if (string.IsNullOrWhiteSpace(configuration["Calendar:Google:ClientId"]) ||
            string.IsNullOrWhiteSpace(configuration["Api:PublicBaseUrl"]))
        {
            return Results.Json(
                new ErrorResponse("Google Calendar isn't configured on this server yet — ask the operator to set Calendar:Google:ClientId/ClientSecret and Api:PublicBaseUrl."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var now = clock.GetCurrentInstant();
        var token = new CalendarLinkToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = CalendarProvider.Google,
            Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)),
            CreatedAt = now,
            ExpiresAt = now.Plus(Duration.FromMinutes(LinkTokenExpiryMinutes)),
        };
        db.CalendarLinkTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        var baseUrl = (configuration["Api:PublicBaseUrl"] ?? "").TrimEnd('/');
        return Results.Ok(new CalendarLinkTokenDto(token.Token, $"{baseUrl}/oauth/google/start?token={token.Token}"));
    }

    /// <summary>Reports whether the user has a linked calendar and when it last refreshed.</summary>
    private static async Task<IResult> GetStatus(
        HttpContext context, long userId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (GuardSelf(context, userId) is { } denied)
        {
            return denied;
        }

        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        return Results.Ok(connection is null
            ? new CalendarConnectionStatusDto(false, null, null)
            : new CalendarConnectionStatusDto(true, connection.Provider, connection.ConnectedAt.ToDateTimeOffset()));
    }

    /// <summary>Revokes and deletes the user's calendar connection (best-effort provider-side revoke).</summary>
    private static async Task<IResult> Disconnect(
        HttpContext context,
        long userId,
        CalCronyDbContext db,
        ICalendarProvider provider,
        CalendarTokenProtector protector,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (GuardSelf(context, userId) is { } denied)
        {
            return denied;
        }

        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        if (connection is null)
        {
            return Results.NoContent();
        }

        // Local disconnect must always succeed even if decrypting or revoking the remote token fails.
        try
        {
            await provider.RevokeAsync(protector.Unprotect(connection.EncryptedRefreshToken), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not revoke calendar token for user {UserId} at the provider; removing local connection anyway.", userId);
        }

        db.CalendarConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Runs a live free/busy check for the requested users and window (BotOnly — prevents member probing).</summary>
    private static async Task<IResult> CheckAvailability(
        AvailabilityRequest request,
        CalendarAvailabilityService availability,
        CancellationToken cancellationToken)
    {
        // De-duplicate first: repeated IDs would otherwise create duplicate pipeline entries and
        // crash the per-user result keying downstream.
        var userIds = request.UserIds.Distinct().ToList();
        if (userIds.Count is 0 or > MaxUsersPerQuery)
        {
            return Results.BadRequest(new ErrorResponse($"userIds must have between 1 and {MaxUsersPerQuery} distinct entries."));
        }

        if (request.EndsAtUtc <= request.StartsAtUtc)
        {
            return Results.BadRequest(new ErrorResponse("endsAtUtc must be after startsAtUtc."));
        }

        var start = Instant.FromDateTimeOffset(request.StartsAtUtc);
        var end = Instant.FromDateTimeOffset(request.EndsAtUtc);
        var results = await availability.CheckAsync(userIds, start, end, cancellationToken);
        return Results.Ok(new AvailabilityResponse(request.StartsAtUtc, request.EndsAtUtc, results));
    }

    /// <summary>Web-safe availability: the user set is the event's "Going" RSVPs and the window
    /// is the event's own — nothing caller-controlled to probe with.</summary>
    private static async Task<IResult> GetEventAvailability(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        CalCronyDbContext db,
        CalendarAvailabilityService availability,
        CancellationToken cancellationToken)
    {
        var ev = await db.Events
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await EventEndpoints.GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        var going = ev.Options.FirstOrDefault(o => o.SortOrder == 0);
        var userIds = going is null
            ? []
            : ev.Rsvps.Where(r => r.OptionId == going.Id).Select(r => r.UserId).Distinct().Take(MaxUsersPerQuery).ToList();

        var start = ev.StartsAt;
        var end = ev.StartsAt + Duration.FromMinutes(ev.DurationMinutes ?? 60);
        IReadOnlyList<UserAvailabilityDto> results = userIds.Count == 0
            ? []
            : await availability.CheckAsync(userIds, start, end, cancellationToken);

        return Results.Ok(new AvailabilityResponse(start.ToDateTimeOffset(), end.ToDateTimeOffset(), results));
    }
}
