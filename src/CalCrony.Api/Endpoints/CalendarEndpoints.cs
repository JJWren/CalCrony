using System.Security.Cryptography;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

public static class CalendarEndpoints
{
    private const int LinkTokenExpiryMinutes = 10;

    /// <summary>Google's own hard per-request calendar-item limit on the freeBusy API.</summary>
    private const int MaxUsersPerQuery = 50;

    public static void MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/calendar/connections/{userId:long}/link-token", CreateLinkToken);
        app.MapGet("/calendar/connections/{userId:long}", GetStatus);
        app.MapDelete("/calendar/connections/{userId:long}", Disconnect);
        app.MapPost("/calendar/availability", CheckAvailability);
    }

    private static async Task<IResult> CreateLinkToken(
        long userId,
        CalCronyDbContext db,
        IConfiguration configuration,
        IClock clock,
        CancellationToken cancellationToken)
    {
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

    private static async Task<IResult> GetStatus(long userId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        return Results.Ok(connection is null
            ? new CalendarConnectionStatusDto(false, null, null)
            : new CalendarConnectionStatusDto(true, connection.Provider, connection.ConnectedAt.ToDateTimeOffset()));
    }

    private static async Task<IResult> Disconnect(
        long userId,
        CalCronyDbContext db,
        ICalendarProvider provider,
        CalendarTokenProtector protector,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
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
}
