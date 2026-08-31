using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Enqueues SyncLiveList outbox rows whenever a guild's upcoming events change. Unlike
/// the per-event embed sync, this fires for BOTH caller types — the bot's command handlers only
/// edit the message they acted on and don't know which channels host live lists, so every
/// rewrite rides the outbox.</summary>
public static class LiveListSync
{
    /// <summary>How far a sync's DueAt is pushed into the future. Later changes coalesce onto the
    /// still-pending row, so a burst of edits/RSVPs on a busy server produces one Discord edit.</summary>
    public const int DebounceSeconds = 10;

    /// <summary>Enqueues one debounced re-render per live list in the guild (no-op for guilds
    /// without one, or when an identical sync is already pending). Adds to the context — the
    /// caller saves.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    internal static async Task EnqueueSyncForGuildAsync(
        CalCronyDbContext db, long guildId, Instant now, CancellationToken cancellationToken)
    {
        var lists = await db.LiveLists
            .Where(l => l.GuildId == guildId)
            .Select(l => new { l.Id, l.ChannelId })
            .ToListAsync(cancellationToken);
        if (lists.Count == 0)
        {
            return;
        }

        // Coalesce with pending identical syncs the bot has never been served (Attempts == 0,
        // the attendee-role rule) — an in-flight row may have fetched events BEFORE this change,
        // so folding into it would leave the embed stale. One batched query for all the guild's
        // lists (this runs on every event/RSVP mutation); Local covers rows added earlier in
        // this same unit of work (e.g. a sweep touching several events of one guild).
        var payloads = lists.ToDictionary(
            l => JsonSerializer.Serialize(new SyncLiveListPayload(l.Id)), l => l);
        var candidates = payloads.Keys.ToList();
        var queued = (await db.Deliveries
                .Where(d => d.Type == DeliveryType.SyncLiveList
                            && d.Status == DeliveryStatus.Pending
                            && d.Attempts == 0
                            && candidates.Contains(d.PayloadJson))
                .Select(d => d.PayloadJson)
                .ToListAsync(cancellationToken))
            .Concat(db.Deliveries.Local
                .Where(d => d.Type == DeliveryType.SyncLiveList && d.Status == DeliveryStatus.Pending)
                .Select(d => d.PayloadJson))
            .ToHashSet();

        foreach (var (payloadJson, list) in payloads.Where(p => !queued.Contains(p.Key)))
        {
            db.Deliveries.Add(NewSync(list.Id, list.ChannelId, payloadJson, now));
        }
    }

    /// <summary>Enqueues the just-registered list's first sync in the same save as its row —
    /// closes the window where an event changes between the bot's initial render and the
    /// registration commit. No coalescing: the id is brand new, nothing can be pending.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="list">The live list row being registered (caller saves).</param>
    /// <param name="now">The current instant.</param>
    internal static void EnqueueInitialSync(CalCronyDbContext db, LiveList list, Instant now) =>
        db.Deliveries.Add(NewSync(
            list.Id, list.ChannelId, JsonSerializer.Serialize(new SyncLiveListPayload(list.Id)), now));

    /// <summary>One future-dated (debounced) SyncLiveList row.</summary>
    private static Delivery NewSync(Guid listId, long channelId, string payloadJson, Instant now) => new()
    {
        Id = Guid.NewGuid(),
        Type = DeliveryType.SyncLiveList,
        ChannelId = channelId,
        PayloadJson = payloadJson,
        DueAt = now.Plus(Duration.FromSeconds(DebounceSeconds)),
        Status = DeliveryStatus.Pending,
        CreatedAt = now,
    };
}
