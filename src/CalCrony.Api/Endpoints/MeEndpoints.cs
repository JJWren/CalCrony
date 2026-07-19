using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Endpoints;

/// <summary>Web-caller self endpoints. Deliberately outside /auth so the SPA's
/// silent-refresh-on-401 handler (which skips /auth/*) applies to them.</summary>
public static class MeEndpoints
{
    /// <summary>Maps the signed-in user's own-data routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me/guilds", GetGuilds).RequireAuthorization("UserOnly");
    }

    /// <summary>Lists the caller's guilds intersected with bot-present guilds, from their login snapshot.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetGuilds(
        HttpContext context, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var userId = context.User.WebUserId()!.Value;

        var rows = await db.UserGuildMemberships
            .Where(m => m.UserId == userId)
            .Join(db.Guilds, m => m.GuildId, g => g.Id, (m, g) => m)
            .OrderBy(m => m.GuildName)
            .ToListAsync(cancellationToken);

        var snapshotAt = rows.Count > 0
            ? rows.Max(m => m.SnapshotAt).ToDateTimeOffset()
            : DateTimeOffset.UtcNow;

        return Results.Ok(new WebGuildListResponse(
            snapshotAt,
            [.. rows.Select(m => new WebGuildDto(m.GuildId, m.GuildName, m.IconHash, m.CanManage))]));
    }
}
