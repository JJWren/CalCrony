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

        if (!request.Present)
        {
            // The bot can no longer see who holds what, so the role snapshot goes with it — and
            // keeping it would be holding member data for a server that removed us (ADR 0004).
            await RoleSnapshotEndpoints.DropSnapshotsAsync(db, [guildId], cancellationToken);
            guild.RolesSyncedAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Clamps a bot-reported name snapshot to its column limit; null/whitespace stays
    /// null (never overwrites a stored snapshot with nothing). The cut backs off one unit rather
    /// than split a surrogate pair — Discord names carry emoji, and a lone surrogate is an
    /// invalid string Npgsql refuses to persist.</summary>
    /// <param name="name">The reported name.</param>
    /// <param name="limit">The column max length.</param>
    /// <returns>The storable name, or null to leave the snapshot untouched.</returns>
    internal static string? Truncate(string? name, int limit)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Length <= limit)
        {
            return name;
        }

        var cut = char.IsHighSurrogate(name[limit - 1]) ? limit - 1 : limit;
        return name[..cut];
    }

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
        var departed = new List<long>();
        foreach (var guild in known)
        {
            guild.BotPresent = current.ContainsKey(guild.Id);
            if (current.TryGetValue(guild.Id, out var name) && name is not null)
            {
                guild.Name = name;
            }

            if (!guild.BotPresent && guild.RolesSyncedAt is not null)
            {
                // Same as a single leave: no bot, no role snapshot.
                guild.RolesSyncedAt = null;
                departed.Add(guild.Id);
            }
        }

        foreach (var guildId in current.Keys.Except(known.Select(g => g.Id)))
        {
            db.Guilds.Add(new Guild { Id = guildId, Name = current[guildId] });
        }

        await db.SaveChangesAsync(cancellationToken);
        await RoleSnapshotEndpoints.DropSnapshotsAsync(db, departed, cancellationToken);
        return Results.Ok(new SyncGuildPresenceResponse(
            current.Count, known.Count(g => !g.BotPresent)));
    }
}
