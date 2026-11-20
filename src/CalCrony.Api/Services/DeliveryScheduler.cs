using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>
/// One sweep of the scheduling state machine: enqueues due event notifications,
/// transitions events Scheduled→Started (with a start ping) and Started→Ended, makes the next
/// occurrence of a running series that has none, re-renders embeds whose RSVP cutoff passed, and
/// closes due polls. Reminders skip this path — they are enqueued directly as future-dated
/// deliveries.
/// </summary>
/// <remarks>A sweep works in three steps. It finds what it may change, reading without taking
/// anything. It takes those rows, in one fixed order: series, then events, then reminders, then
/// polls, each kind in id order — the order in which the API's routes take them. And it reads them
/// again and works from them as they stand once it holds them. So every row its save changes is
/// one it holds, and a request that changes one of them while the sweep holds it waits for the
/// sweep.</remarks>
/// <param name="db">The database context.</param>
/// <param name="materializer">The series occurrence materializer.</param>
/// <param name="logger">The host logger.</param>
public sealed class DeliveryScheduler(
    CalCronyDbContext db, SeriesMaterializer materializer, ILogger<DeliveryScheduler> logger)
{
    /// <summary>Runs one sweep: matures notifications, transitions event statuses, materializes freed series slots, and auto-closes due polls.</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many deliveries this sweep enqueued.</returns>
    public async Task<int> SweepAsync(Instant now, CancellationToken cancellationToken)
    {
        // One transaction for the whole sweep: the rows it takes are held until it commits, and
        // the RSVP-cutoff claims below commit together with the deliveries they justify (a claimed
        // flag with no delivery would leave an embed never re-rendered as closed).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The first two steps: what this sweep may change, and those rows taken.
        var found = await FindAsync(now, cancellationToken);
        await TakeAsync(found, cancellationToken);

        // The third step: each row read again, now that the sweep holds it, and the work done
        // from what is read now.
        var enqueued = 0;

        // Guilds whose upcoming-events picture changed this sweep (an event started and drops
        // off, or a series rolled its next occurrence) — their live lists rewrite, deduped here
        // and debounced/coalesced by LiveListSync.
        var liveListGuilds = new HashSet<long>();
        // Channel posts that may need opt-in DM mirrors — collected across the loops below and
        // fanned out in ONE batched pass, so a large sweep costs three extra queries, not 2-3 per item.
        var dmItems = new List<DmReminderFanOut.Item>();
        // Series that end in this sweep because they have no next occurrence — named in the log
        // once the sweep is saved.
        var endedSeries = new List<EventSeries>();

        // Due event notifications (fire time recomputed from the event's current start).
        var pendingNotifications = await db.EventNotifications
            .Where(n => found.NotificationIds.Contains(n.Id) && !n.Enqueued)
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
                    pair.Notification.Mentions,
                    pair.Event.GuildId),
                fireAt,
                now));
            enqueued++;
            dmItems.Add(new DmReminderFanOut.Item(pair.Event, pair.Notification.Message, IsStart: false, fireAt));
        }

        // Scheduled → Started, with a start ping.
        var starting = await db.Events
            .Where(e => found.StartingIds.Contains(e.Id) && e.Status == EventStatus.Scheduled && e.StartsAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var ev in starting)
        {
            ev.Status = EventStatus.Started;
            db.Deliveries.Add(NewDelivery(
                DeliveryType.EventStart,
                ev.ChannelId,
                new EventStartPayload(
                    ev.Id, ev.Title, ev.StartsAt.ToUnixTimeSeconds(), ev.MessageId,
                    ev.GuildId, ev.NativeEventId),
                ev.StartsAt,
                now));
            enqueued++;
            dmItems.Add(new DmReminderFanOut.Item(ev, Message: null, IsStart: true, ev.StartsAt));
            liveListGuilds.Add(ev.GuildId);
        }

        enqueued += await DmReminderFanOut.EnqueueAsync(db, dmItems, now, cancellationToken);

        // Started → Ended once the duration (default 60 min) has elapsed; mirrored native events
        // get a completion delivery and attendee roles are revoked from every Going RSVP
        // (each once per event, guarded by the status transition).
        var endedCutoffCandidates = await db.Events
            .Where(e => found.EndingIds.Contains(e.Id) && e.Status == EventStatus.Started)
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .ToListAsync(cancellationToken);
        foreach (var ev in endedCutoffCandidates)
        {
            if (EventTimes.End(ev.StartsAt, ev.DurationMinutes) <= now)
            {
                ev.Status = EventStatus.Ended;
                if (ev.NativeEventId is long nativeId)
                {
                    db.Deliveries.Add(NewDelivery(
                        DeliveryType.CompleteNativeEvent,
                        ev.ChannelId,
                        new CompleteNativeEventPayload(ev.Id, ev.GuildId, nativeId),
                        now,
                        now));
                    enqueued++;
                }

                enqueued += AttendeeRoleSync.EnqueueRoleFanOutAll(db, ev, DeliveryType.RevokeAttendeeRole, now);

                if (ev.ThreadId is not null)
                {
                    EventThreadSync.EnqueueArchive(db, ev, now);
                    enqueued++;
                }
            }
        }

        // Rolling next occurrence: a running series without a live (Scheduled/Started)
        // occurrence gets its next event materialized — self-healing regardless of how the slot
        // freed (ended above, skipped, cancelled via PATCH, or a crashed earlier attempt). The
        // series are those the sweep took, read again under that hold and advanced in id order.
        if (found.SeriesIds.Count > 0)
        {
            var activeSeries = await db.EventSeries
                .Include(s => s.NotificationSpecs)
                .Where(s => found.SeriesIds.Contains(s.Id) && !s.Ended)
                .OrderBy(s => s.Id)
                .ToListAsync(cancellationToken);

            // Every occurrence of these series is held, so none of them can become live before
            // this sweep is saved.
            var liveCandidates = await db.Events
                .Where(e => e.SeriesId != null
                            && found.SeriesIds.Contains(e.SeriesId.Value)
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
                Event? next;
                try
                {
                    next = materializer.MaterializeNext(series, now);
                }
                catch (Exception ex)
                {
                    // Each series is advanced on its own. MaterializeNext works its occurrence
                    // out and builds it before it adds anything to the context, so a series that
                    // fails there is left exactly as it was: logged, passed over, and tried
                    // again on the next sweep.
                    logger.LogError(
                        ex,
                        "Could not advance series {SeriesId} in guild {GuildId}; leaving it as it is.",
                        series.Id,
                        series.GuildId);
                    continue;
                }

                if (next is null)
                {
                    // No next occurrence, so MaterializeNext has ended the series.
                    endedSeries.Add(series);
                    continue;
                }

                enqueued++;
                liveListGuilds.Add(series.GuildId);
            }
        }

        // RSVP cutoffs that just passed: one-shot embed re-render so the buttons show disabled
        // and the embed says closed.
        foreach (var ev in found.ClosingRsvps)
        {
            // Atomic claim: flip the one-shot flag only if the row's cutoff state is still the
            // one this sweep judged closed. An edit that moved the cutoff in between (and reset
            // the flag) changes one of these columns, so the claim misses, nothing is enqueued,
            // and the next sweep re-evaluates the new deadline instead of losing it. A tracked
            // entity of the event is left alone so the final SaveChanges can't rewrite the flag.
            var claimed = await db.Events
                .Where(e => e.Id == ev.Id
                            && !e.RsvpCloseSynced
                            && e.StartsAt == ev.StartsAt
                            && e.RsvpClosesAt == ev.RsvpClosesAt
                            && e.RsvpCloseMinutesBefore == ev.RsvpCloseMinutesBefore)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.RsvpCloseSynced, true), cancellationToken);
            if (claimed == 0)
            {
                continue;
            }

            db.Deliveries.Add(NewDelivery(
                DeliveryType.SyncEventMessage,
                ev.ChannelId,
                new SyncEventMessagePayload(ev.Id),
                now,
                now));
            enqueued++;
        }

        // Auto-close polls whose deadline passed; re-render their embeds in the closed state.
        var closingPolls = await db.Polls
            .Where(p => found.PollIds.Contains(p.Id)
                        && p.Status == PollStatus.Open
                        && p.ClosesAt != null
                        && p.ClosesAt <= now)
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

        foreach (var guildId in liveListGuilds)
        {
            enqueued += await LiveListSync.EnqueueSyncForGuildAsync(db, guildId, now, cancellationToken);
        }

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Said only now that it is saved, so a series that stops repeating can be traced to the
        // sweep that ended it.
        foreach (var series in endedSeries)
        {
            logger.LogInformation(
                "Series {SeriesId} in guild {GuildId} has no next occurrence and has ended.",
                series.Id,
                series.GuildId);
        }

        if (enqueued > 0)
        {
            logger.LogInformation("Scheduler sweep enqueued {Count} deliveries.", enqueued);
        }

        return enqueued;
    }

    /// <summary>The first step: what this sweep may change, read without taking anything and
    /// without tracking anything, so that the third step reads each row afresh.</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The rows found.</returns>
    private async Task<Found> FindAsync(Instant now, CancellationToken cancellationToken)
    {
        // Reminders whose fire time, from their event's current start, has come, on events that
        // are still scheduled.
        var notifications = (await db.EventNotifications
                .Where(n => !n.Enqueued)
                .Join(
                    db.Events.Where(e => e.Status == EventStatus.Scheduled),
                    n => n.EventId,
                    e => e.Id,
                    (n, e) => new { n.Id, n.EventId, n.MinutesBefore, e.StartsAt })
                .ToListAsync(cancellationToken))
            .Where(n => n.StartsAt.Minus(Duration.FromMinutes(n.MinutesBefore)) <= now)
            .ToList();

        var starting = await db.Events
            .Where(e => e.Status == EventStatus.Scheduled && e.StartsAt <= now)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var ending = (await db.Events
                .Where(e => e.Status == EventStatus.Started)
                .Select(e => new { e.Id, e.StartsAt, e.DurationMinutes })
                .ToListAsync(cancellationToken))
            .Where(e => EventTimes.End(e.StartsAt, e.DurationMinutes) <= now)
            .Select(e => e.Id)
            .ToList();

        // Running series without a live occurrence, one that this sweep is to end counting as
        // over.
        var endingIds = ending.ToHashSet();
        var liveSeriesIds = (await db.Events
                .Where(e => e.SeriesId != null
                            && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started))
                .Select(e => new { e.Id, SeriesId = e.SeriesId!.Value })
                .ToListAsync(cancellationToken))
            .Where(e => !endingIds.Contains(e.Id))
            .Select(e => e.SeriesId)
            .ToHashSet();
        var series = (await db.EventSeries
                .Where(s => !s.Ended)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken))
            .Where(id => !liveSeriesIds.Contains(id))
            .ToList();

        // RSVP cutoffs that just passed. SQL narrows to what can be due this tick — absolute
        // cutoffs exactly, relative ones by the four-week maximum they can precede the start — so
        // the scan doesn't grow with every capped event months out; the exact instant check
        // (EffectiveClose) still runs in memory.
        var relativeHorizon = now.Plus(Duration.FromMinutes(FieldLimits.MaxMinutes));
        var closingRsvps = (await db.Events
                .AsNoTracking()
                .Where(e => (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started)
                            && !e.RsvpCloseSynced
                            && e.MessageId != null
                            && (e.RsvpClosesAt <= now
                                || (e.RsvpClosesAt == null
                                    && e.RsvpCloseMinutesBefore != null
                                    && e.StartsAt <= relativeHorizon)))
                .ToListAsync(cancellationToken))
            .Where(e => RsvpPolicy.IsClosed(e, now))
            .ToList();

        var polls = await db.Polls
            .Where(p => p.Status == PollStatus.Open && p.ClosesAt != null && p.ClosesAt <= now)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        return new Found(
            [.. notifications.Select(n => n.Id)],
            [.. notifications.Select(n => n.EventId)],
            starting,
            ending,
            series,
            closingRsvps,
            polls);
    }

    /// <summary>The second step: the rows found, taken before any of them is changed, in the
    /// order in which the API's routes take them — series, then events, then reminders, then
    /// polls — each kind in id order. Every occurrence of a series the sweep may advance is taken
    /// with it, so that none of them becomes live before the sweep has made the series' next
    /// occurrence. A row that has gone since the first step is not there to take, and the third
    /// step does not find it.</summary>
    /// <param name="found">The rows found in the first step.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the rows are held.</returns>
    private async Task TakeAsync(Found found, CancellationToken cancellationToken)
    {
        Guid[] series = [.. found.SeriesIds];
        Guid[] events =
        [
            .. found.NotifiedEventIds
                .Concat(found.StartingIds)
                .Concat(found.EndingIds)
                .Concat(found.ClosingRsvps.Select(e => e.Id))
                .Distinct(),
        ];
        Guid[] notifications = [.. found.NotificationIds];
        Guid[] polls = [.. found.PollIds];

        if (series.Length > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"""SELECT "Id" FROM "EventSeries" WHERE "Id" = ANY({series}) ORDER BY "Id" FOR NO KEY UPDATE""",
                cancellationToken);
        }

        if (events.Length > 0 || series.Length > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"""SELECT "Id" FROM "Events" WHERE "Id" = ANY({events}) OR "SeriesId" = ANY({series}) ORDER BY "Id" FOR NO KEY UPDATE""",
                cancellationToken);
        }

        if (notifications.Length > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"""SELECT "Id" FROM "EventNotifications" WHERE "Id" = ANY({notifications}) ORDER BY "Id" FOR NO KEY UPDATE""",
                cancellationToken);
        }

        if (polls.Length > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"""SELECT "Id" FROM "Polls" WHERE "Id" = ANY({polls}) ORDER BY "Id" FOR NO KEY UPDATE""",
                cancellationToken);
        }
    }

    /// <summary>Saves what the sweep has worked out. Every row it changes is one it holds. A
    /// failure goes on to the caller, which logs it; before that, the rows the failure names go
    /// into the log by entity and key, and nothing of what they hold.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the sweep is saved.</returns>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when ((ex as DbUpdateException ?? ex.InnerException as DbUpdateException) is { } failure)
        {
            // The database provider hands on a failure it takes to be passing inside an exception
            // of its own, with the failed save inside that.
            logger.LogError(
                "The sweep could not be saved. Rows named by the failure: {Rows}.", SaveFailure.Describe(failure));
            throw;
        }
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

    /// <summary>What the first step of a sweep found it may change.</summary>
    /// <param name="NotificationIds">Reminders that are due.</param>
    /// <param name="NotifiedEventIds">The events of those reminders.</param>
    /// <param name="StartingIds">Events due to start.</param>
    /// <param name="EndingIds">Started events whose length has passed.</param>
    /// <param name="SeriesIds">Running series without a live occurrence once those end.</param>
    /// <param name="ClosingRsvps">Events whose RSVP cutoff has passed, as read.</param>
    /// <param name="PollIds">Open polls whose closing time has passed.</param>
    private sealed record Found(
        List<Guid> NotificationIds,
        List<Guid> NotifiedEventIds,
        List<Guid> StartingIds,
        List<Guid> EndingIds,
        List<Guid> SeriesIds,
        List<Event> ClosingRsvps,
        List<Guid> PollIds);
}
