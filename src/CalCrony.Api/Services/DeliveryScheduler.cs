using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>
/// One sweep of the scheduling state machine: enqueues due event notifications,
/// transitions events Scheduled→Started (with a start ping) and Started→Ended.
/// Reminders skip this path — they are enqueued directly as future-dated deliveries.
/// </summary>
public sealed class DeliveryScheduler(
    CalCronyDbContext db, SeriesMaterializer materializer, ILogger<DeliveryScheduler> logger)
{
    private const int DefaultEventLengthMinutes = 60;

    public async Task<int> SweepAsync(Instant now, CancellationToken cancellationToken)
    {
        var enqueued = 0;

        // Due event notifications (fire time recomputed from the event's current start).
        var pendingNotifications = await db.EventNotifications
            .Where(n => !n.Enqueued)
            .Join(db.Events, n => n.EventId, e => e.Id, (n, e) => new { Notification = n, Event = e })
            .Where(x => x.Event.Status == EventStatus.Scheduled)
            .ToListAsync(cancellationToken);

        foreach (var pair in pendingNotifications)
        {
            var fireAt = pair.Event.StartsAt.Minus(Duration.FromMinutes(pair.Notification.MinutesBefore));
            if (fireAt > now)
            {
                continue;
            }

            pair.Notification.Enqueued = true;
            db.Deliveries.Add(NewDelivery(
                DeliveryType.EventNotification,
                pair.Notification.ChannelId ?? pair.Event.ChannelId,
                new EventNotificationPayload(
                    pair.Event.Id,
                    pair.Event.Title,
                    pair.Event.StartsAt.ToUnixTimeSeconds(),
                    pair.Notification.Message,
                    pair.Notification.Mentions),
                fireAt,
                now));
            enqueued++;
        }

        // Scheduled → Started, with a start ping.
        var starting = await db.Events
            .Where(e => e.Status == EventStatus.Scheduled && e.StartsAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var ev in starting)
        {
            ev.Status = EventStatus.Started;
            db.Deliveries.Add(NewDelivery(
                DeliveryType.EventStart,
                ev.ChannelId,
                new EventStartPayload(ev.Id, ev.Title, ev.StartsAt.ToUnixTimeSeconds(), ev.MessageId),
                ev.StartsAt,
                now));
            enqueued++;
        }

        // Started → Ended once the duration (default 60 min) has elapsed.
        var endedCutoffCandidates = await db.Events
            .Where(e => e.Status == EventStatus.Started)
            .ToListAsync(cancellationToken);
        foreach (var ev in endedCutoffCandidates)
        {
            var length = Duration.FromMinutes(ev.DurationMinutes ?? DefaultEventLengthMinutes);
            if (ev.StartsAt.Plus(length) <= now)
            {
                ev.Status = EventStatus.Ended;
            }
        }

        // Rolling next occurrence: any running series without a live (Scheduled/Started)
        // occurrence gets its next event materialized — self-healing regardless of how the slot
        // freed (ended above, skipped, cancelled via PATCH, or a crashed earlier attempt).
        var activeSeries = await db.EventSeries
            .Include(s => s.NotificationSpecs)
            .Where(s => !s.Ended)
            .ToListAsync(cancellationToken);
        if (activeSeries.Count > 0)
        {
            var liveCandidates = await db.Events
                .Where(e => e.SeriesId != null
                            && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started))
                .ToListAsync(cancellationToken);
            // Re-filter in memory: identity resolution replays this sweep's not-yet-saved
            // transitions, so an event that just Ended above no longer counts as live. A
            // projection here would read the stale DB status and miss the freed slot.
            var liveSeriesIds = liveCandidates
                .Where(e => e.Status is EventStatus.Scheduled or EventStatus.Started)
                .Select(e => e.SeriesId!.Value)
                .ToHashSet();
            foreach (var series in activeSeries.Where(s => !liveSeriesIds.Contains(s.Id)))
            {
                if (materializer.MaterializeNext(series, now) is not null)
                {
                    enqueued++;
                }
            }
        }

        // Auto-close polls whose deadline passed; re-render their embeds in the closed state.
        var closingPolls = await db.Polls
            .Where(p => p.Status == PollStatus.Open && p.ClosesAt != null && p.ClosesAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var poll in closingPolls)
        {
            poll.Status = PollStatus.Closed;
            poll.ClosedAt = now;
            if (poll.MessageId is not null)
            {
                db.Deliveries.Add(NewDelivery(
                    DeliveryType.SyncPollMessage,
                    poll.ChannelId,
                    new SyncPollMessagePayload(poll.Id),
                    now,
                    now));
                enqueued++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (enqueued > 0)
        {
            logger.LogInformation("Scheduler sweep enqueued {Count} deliveries.", enqueued);
        }

        return enqueued;
    }

    private static Delivery NewDelivery<TPayload>(
        DeliveryType type, long channelId, TPayload payload, Instant dueAt, Instant now) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        ChannelId = channelId,
        PayloadJson = JsonSerializer.Serialize(payload),
        DueAt = dueAt,
        Status = DeliveryStatus.Pending,
        CreatedAt = now,
    };
}
