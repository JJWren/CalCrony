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

    /// <summary>Enqueues one thread member-add with dedup-only coalescing (an identical pending
    /// payload skips). There is no opposite-cancel here — the operation is add-only, so the M13
    /// grant/revoke netting has no analog.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (ThreadId set).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task EnqueueMemberAddAsync(
        CalCronyDbContext db, Event ev, long userId, IClock clock, CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(
            new ThreadMemberPayload(ev.Id, ev.GuildId, ev.ThreadId!.Value, userId));
        var alreadyQueued = await db.Deliveries.AnyAsync(
            d => d.Type == DeliveryType.AddThreadMember
                 && d.Status == DeliveryStatus.Pending
                 && d.PayloadJson == payloadJson,
            cancellationToken);
        if (alreadyQueued)
        {
            return;
        }

        AddDelivery(db, ev, DeliveryType.AddThreadMember, payloadJson, clock.GetCurrentInstant());
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
