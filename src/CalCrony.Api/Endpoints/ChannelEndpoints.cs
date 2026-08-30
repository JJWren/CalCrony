using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Bot-only channel-name snapshot endpoints. Rows exist only for channels CalCrony
/// references (events on the feed horizon, running series, guild default channels): the bulk
/// sync creates rows for the bot's Ready-time reconcile, the single-name route updates existing
/// rows only, so renames of unreferenced channels never grow the table.</summary>
public static class ChannelEndpoints
{
    /// <summary>Maps channel-snapshot routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/channels/referenced", GetReferenced).RequireAuthorization("BotOnly");
        app.MapPut("/channels/sync", SyncChannels).RequireAuthorization("BotOnly");
        app.MapPut("/channels/{channelId:long}/name", PutName).RequireAuthorization("BotOnly");
    }

    /// <summary>Lists every channel the API currently references and wants a name snapshot for:
    /// channels of events on the feed horizon (last 30 days plus upcoming, matching what the
    /// feed renders), of running series, and guild default channels.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetReferenced(
        CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var horizon = clock.GetCurrentInstant().Minus(Duration.FromDays(30));
        var fromEvents = await db.Events
            .Where(e => e.Status != EventStatus.Cancelled && e.StartsAt >= horizon)
            .Select(e => new { e.GuildId, e.ChannelId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var fromSeries = await db.EventSeries
            .Where(s => !s.Ended)
            .Select(s => new { s.GuildId, s.ChannelId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var fromDefaults = await db.Guilds
            .Where(g => g.DefaultChannelId != null)
            .Select(g => new { GuildId = g.Id, ChannelId = g.DefaultChannelId!.Value })
            .ToListAsync(cancellationToken);

        var channels = fromEvents.Concat(fromSeries).Concat(fromDefaults)
            .Select(c => new ReferencedChannelDto(c.GuildId, c.ChannelId))
            .Distinct()
            .ToList();
        return Results.Ok(new ReferencedChannelsResponse(channels));
    }

    /// <summary>Bulk-upserts channel-name snapshots (the bot's Ready-time reconcile). Creates
    /// missing rows — callers only send channels the API said it references.</summary>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SyncChannels(
        SyncChannelsRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (request.Channels is null)
        {
            return Results.BadRequest(new ErrorResponse("Channels is required."));
        }

        var snapshots = request.Channels
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c with { Name = GuildPresenceEndpoints.Truncate(c.Name, FieldLimits.ChannelName)! })
            .GroupBy(c => c.ChannelId)
            .ToDictionary(g => g.Key, g => g.First());
        var ids = snapshots.Keys.ToList();
        var known = await db.Channels.Where(c => ids.Contains(c.Id)).ToListAsync(cancellationToken);
        foreach (var channel in known)
        {
            var snapshot = snapshots[channel.Id];
            channel.GuildId = snapshot.GuildId;
            channel.Name = snapshot.Name;
        }

        foreach (var snapshot in snapshots.Values.Where(s => known.All(c => c.Id != s.ChannelId)))
        {
            db.Channels.Add(new Channel { Id = snapshot.ChannelId, GuildId = snapshot.GuildId, Name = snapshot.Name });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Records a channel rename. Updates an existing snapshot only — a rename of a
    /// channel CalCrony has never referenced is deliberately a no-op (204 either way).</summary>
    /// <param name="channelId">The Discord channel id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutName(
        long channelId, ChannelNameRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (GuildPresenceEndpoints.Truncate(request.Name, FieldLimits.ChannelName) is not { } name)
        {
            return Results.BadRequest(new ErrorResponse("Name is required."));
        }

        await db.Channels.Where(c => c.Id == channelId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, name), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Upserts one channel-name snapshot from an embed post site (SetMessage carries the
    /// name the bot just posted into). Creating here is correct — a posted-into channel is by
    /// definition referenced.</summary>
    /// <param name="db">The database context (caller saves).</param>
    /// <param name="channelId">The Discord channel id.</param>
    /// <param name="guildId">The guild the channel belongs to.</param>
    /// <param name="name">The bot-reported channel name; null/whitespace is a no-op.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    internal static async Task UpsertSnapshotAsync(
        CalCronyDbContext db, long channelId, long guildId, string? name, CancellationToken cancellationToken)
    {
        if (GuildPresenceEndpoints.Truncate(name, FieldLimits.ChannelName) is not { } storable)
        {
            return;
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            db.Channels.Add(new Channel { Id = channelId, GuildId = guildId, Name = storable });
        }
        else
        {
            channel.GuildId = guildId;
            channel.Name = storable;
        }
    }
}
