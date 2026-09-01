using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Event-thread outbox logic: adding "Going" RSVPers to the event's discussion thread
/// (add-only — switching away never removes) and archiving the thread when the event leaves the
/// live states. All Discord thread changes flow through the outbox (types 12/13); the bot
/// handlers are best-effort.</summary>
public static class EventThreadSync
{
    /// <summary>Whether thread deliveries may fire at all: a thread exists and the event is live.
    /// Membership adds gate on this; archive fires exactly when an event exits the live states.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>True when thread member-add deliveries apply.</returns>
    public static bool IsThreadActive(Event ev) =>
        ev.ThreadId is not null && ev.Status is EventStatus.Scheduled or EventStatus.Started;

    /// <summary>The exact payload a member-add for one user carries — the value the dedup lookup
    /// matches on and the enqueued row stores.</summary>
    /// <param name="ev">The event (ThreadId set).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <returns>The serialized <see cref="ThreadMemberPayload"/>.</returns>
    private static string MemberPayloadJson(Event ev, long userId) =>
        JsonSerializer.Serialize(new ThreadMemberPayload(ev.Id, ev.GuildId, ev.ThreadId!.Value, userId));

    /// <summary>Loads the already-queued member-add payloads for any of <paramref name="userIds"/>
    /// in ONE query, so a caller enqueuing for many users at once (waitlist promotion) dedups
    /// against an in-memory set instead of a lookup per user. Feed the result to
    /// <see cref="EnqueueMemberAdd"/>, which keeps it in step with what it enqueues.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (ThreadId set).</param>
    /// <param name="userIds">The Discord user ids about to be enqueued for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The pending payloads for those users; the enqueue mutates it.</returns>
    public static async Task<HashSet<string>> LoadPendingMemberAddsAsync(
        CalCronyDbContext db, Event ev, IEnumerable<long> userIds, CancellationToken cancellationToken)
    {
        var payloads = userIds.Select(userId => MemberPayloadJson(ev, userId)).ToList();
        if (payloads.Count == 0)
        {
            return [];
        }

        var queued = await db.Deliveries
            .Where(d => d.Type == DeliveryType.AddThreadMember
                        && d.Status == DeliveryStatus.Pending
                        && payloads.Contains(d.PayloadJson))
            .Select(d => d.PayloadJson)
            .ToListAsync(cancellationToken);
        return [.. queued];
    }

    /// <summary>Enqueues one thread member-add against an already-loaded pending set — the batch
    /// form of <see cref="EnqueueMemberAddAsync"/>, applying the identical dedup rule. Adding the
    /// payload to <paramref name="pending"/> IS the check, so repeated calls within one pass see
    /// each other's work — which the per-call query form cannot do, since EF never queries
    /// un-saved additions.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (ThreadId set).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="pending">The payloads from <see cref="LoadPendingMemberAddsAsync"/>.</param>
    /// <param name="now">The current instant.</param>
    public static void EnqueueMemberAdd(
        CalCronyDbContext db, Event ev, long userId, HashSet<string> pending, Instant now)
    {
        var payloadJson = MemberPayloadJson(ev, userId);
        if (pending.Add(payloadJson))
        {
            AddDelivery(db, ev, DeliveryType.AddThreadMember, payloadJson, now);
        }
    }

    /// <summary>Enqueues one thread member-add with dedup-only coalescing (an identical pending
    /// payload skips). There is no opposite-cancel here — the operation is add-only, so the M13
    /// grant/revoke netting has no analog. Enqueuing for a whole set of users at once? Use
    /// <see cref="LoadPendingMemberAddsAsync"/> with <see cref="EnqueueMemberAdd"/> — same rule,
    /// one lookup for the pass instead of one per user.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (ThreadId set).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task EnqueueMemberAddAsync(
        CalCronyDbContext db, Event ev, long userId, IClock clock, CancellationToken cancellationToken)
    {
        var pending = await LoadPendingMemberAddsAsync(db, ev, [userId], cancellationToken);
        EnqueueMemberAdd(db, ev, userId, pending, clock.GetCurrentInstant());
    }

    /// <summary>Enqueues the thread archive. One-shot at each live-exit transition (end / delete /
    /// skip / cancel); the bot handler treats already-archived or deleted threads as done.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (ThreadId set).</param>
    /// <param name="now">The current instant.</param>
    public static void EnqueueArchive(CalCronyDbContext db, Event ev, Instant now)
        => AddDelivery(
            db, ev, DeliveryType.ArchiveThread,
            JsonSerializer.Serialize(new ArchiveThreadPayload(ev.Id, ev.GuildId, ev.ThreadId!.Value)),
            now);

    private static void AddDelivery(CalCronyDbContext db, Event ev, DeliveryType type, string payloadJson, Instant now)
        => db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = type,
            // Threads are addressed by their own channel id in the payload; the row's required
            // ChannelId column carries the parent channel for consistency but is unused.
            ChannelId = ev.ChannelId,
            PayloadJson = payloadJson,
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
}
