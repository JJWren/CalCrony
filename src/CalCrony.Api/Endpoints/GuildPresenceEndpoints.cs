using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Endpoints;

/// <summary>Bot-only guild presence endpoints — the source of truth for which guilds the bot is
/// in, and the write path for guild-name snapshots (the API never asks Discord for names).
/// Guild rows are never deleted here: leaving only clears the flag, so settings and data
/// survive a re-invite.</summary>
public static class GuildPresenceEndpoints
{
    /// <summary>Maps presence routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapGuildPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/guilds/{guildId:long}/presence", PutPresence).RequireAuthorization("BotOnly");
        app.MapPut("/guilds/presence/sync", SyncPresence).RequireAuthorization("BotOnly");
    }

    /// <summary>Records a single guild's presence change (bot joined or left).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutPresence(
        long guildId, GuildPresenceRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);
        guild.BotPresent = request.Present;
        if (Truncate(request.Name, FieldLimits.GuildName) is { } name)
        {
            guild.Name = name;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Clamps a bot-reported name snapshot to its column limit; null/whitespace stays
    /// null (never overwrites a stored snapshot with nothing).</summary>
    /// <param name="name">The reported name.</param>
    /// <param name="limit">The column max length.</param>
    /// <returns>The storable name, or null to leave the snapshot untouched.</returns>
    internal static string? Truncate(string? name, int limit) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Length <= limit ? name : name[..limit];

    /// <summary>Reconciles presence against the bot's full guild list (reported at Ready):
    /// listed guilds become present (rows created as needed), unlisted known guilds become absent.</summary>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SyncPresence(
        SyncGuildPresenceRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (request.Guilds is null)
        {
            return Results.BadRequest(new ErrorResponse("Guilds is required (an empty list marks every guild absent)."));
        }

        var current = request.Guilds
            .GroupBy(g => g.Id)
            .ToDictionary(g => g.Key, g => Truncate(g.First().Name, FieldLimits.GuildName));
        var known = await db.Guilds.ToListAsync(cancellationToken);
        foreach (var guild in known)
        {
            guild.BotPresent = current.ContainsKey(guild.Id);
            if (current.TryGetValue(guild.Id, out var name) && name is not null)
            {
                guild.Name = name;
            }
        }

        foreach (var guildId in current.Keys.Except(known.Select(g => g.Id)))
        {
            db.Guilds.Add(new Guild { Id = guildId, Name = current[guildId] });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new SyncGuildPresenceResponse(
            current.Count, known.Count(g => !g.BotPresent)));
    }
}
