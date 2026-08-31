using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Live-list registration endpoints. Live lists are a Discord-surface feature managed
/// entirely by the bot (message ids mean nothing to other callers), so every route is bot-only;
/// manager-only enforcement happens at the slash command.</summary>
public static class LiveListEndpoints
{
    /// <summary>Maps live-list routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapLiveListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization("BotOnly");
        group.MapPost("/guilds/{guildId:long}/livelists", Create);
        group.MapGet("/guilds/{guildId:long}/livelists", ListForGuild);
        group.MapGet("/livelists", ListAll);
        group.MapGet("/livelists/{id:guid}", Get);
        group.MapDelete("/livelists/{id:guid}", Delete);
    }

    /// <summary>Registers a live list the bot just posted. One per channel: a second registration
    /// for the same channel is a 409 (the bot compensates by deleting its message).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> Create(
        long guildId,
        CreateLiveListRequest request,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);

        if (await db.LiveLists.AnyAsync(l => l.ChannelId == request.ChannelId, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse(
                "That channel already has a live list — remove it first with /livelist remove."));
        }

        var list = new LiveList
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            ChannelId = request.ChannelId,
            MessageId = request.MessageId,
            Limit = Math.Clamp(request.Limit, 1, 25),
            CreatorId = request.CreatorId,
            CreatedAt = clock.GetCurrentInstant(),
        };
        db.LiveLists.Add(list);

        // First sync rides the same save: an event changing between the bot's initial render and
        // this commit would otherwise see no list row and leave the fresh embed stale.
        Services.LiveListSync.EnqueueInitialSync(db, list, list.CreatedAt);

        // A channel hosting a live list is referenced — snapshot its name (ADR 0001).
        await ChannelEndpoints.UpsertSnapshotAsync(
            db, request.ChannelId, guildId, request.ChannelName, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
        })
        {
            // Lost a race with a concurrent create for the same channel.
            return Results.Conflict(new ErrorResponse(
                "That channel already has a live list — remove it first with /livelist remove."));
        }

        return Results.Created($"/livelists/{list.Id}", list.ToDto());
    }

    /// <summary>Lists a guild's live lists (used by /livelist remove to find the channel's list).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ListForGuild(
        long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var lists = await db.LiveLists
            .Where(l => l.GuildId == guildId)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
        return Results.Ok(lists.Select(l => l.ToDto()));
    }

    /// <summary>Lists every live list in bot-present guilds — the bot's Ready-time reconcile
    /// re-renders each and clears records whose messages were deleted while offline. Absent
    /// guilds are skipped: the bot can't resolve their channels, and their records survive
    /// untouched for a re-invite.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ListAll(CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var lists = await db.LiveLists
            .Where(l => db.Guilds.Any(g => g.Id == l.GuildId && g.BotPresent))
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
        return Results.Ok(lists.Select(l => l.ToDto()));
    }

    /// <summary>Fetches one live list (the SyncLiveList handler's refetch — 404 means the list
    /// was removed and the delivery is done).</summary>
    /// <param name="id">The live list id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> Get(Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var list = await db.LiveLists.FindAsync([id], cancellationToken);
        return list is null ? Results.NotFound() : Results.Ok(list.ToDto());
    }

    /// <summary>Removes a live list's record — /livelist remove, or the bot clearing a list whose
    /// message it found manually deleted (deleted message = list is gone, never reposted).</summary>
    /// <param name="id">The live list id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> Delete(Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var list = await db.LiveLists.FindAsync([id], cancellationToken);
        if (list is null)
        {
            return Results.NotFound();
        }

        db.LiveLists.Remove(list);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}
