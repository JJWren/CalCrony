using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Mirrors an event's channel reminder / start announcement into per-user DM deliveries
/// for the people who (a) hold a SEAT on the attending option and (b) opted in to DM reminders.
/// Nothing else ever produces a DM: not creators, not servers, not waitlisted or non-attending
/// RSVPs. Rides the same outbox as everything else, so a crash between the channel post and the
/// DMs loses nothing. Works in one batched pass per sweep — three queries for any number of due
/// items — because it runs inside the sweep's transaction.</summary>
public static class DmReminderFanOut
{
    /// <summary>One channel post that may need DM mirrors.</summary>
    /// <param name="Event">The event whose channel notification/start ping was just enqueued.</param>
    /// <param name="Message">The notification's custom message (null for start announcements).</param>
    /// <param name="IsStart">True for the "starting now" announcement.</param>
    /// <param name="DueAt">When the DM is due (the channel post's due time).</param>
    public readonly record struct Item(Event Event, string? Message, bool IsStart, Instant DueAt);

    /// <summary>Enqueues one DM delivery per opted-in seated attendee, for every item at once.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="items">The channel posts enqueued this sweep.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many DM deliveries were enqueued.</returns>
    public static async Task<int> EnqueueAsync(
        CalCronyDbContext db, IReadOnlyList<Item> items, Instant now, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var eventIds = items.Select(i => i.Event.Id).Distinct().ToList();

        // 1. The attending option of each due event.
        var attendingByEvent = (await db.RsvpOptions
                .Where(o => eventIds.Contains(o.EventId))
                .ToListAsync(cancellationToken))
            .GroupBy(o => o.EventId)
            .Select(g => (EventId: g.Key, Attending: RsvpPolicy.AttendingOption(g)?.Id))
            .Where(x => x.Attending is not null)
            .ToDictionary(x => x.EventId, x => x.Attending!.Value);
        if (attendingByEvent.Count == 0)
        {
            return 0;
        }

        // 2. Seated RSVPs on those events whose user opted in (the attending filter is applied
        //    in memory — it is per event, and the rows are already narrowed to due events).
        var seated = await db.Rsvps
            .Where(r => eventIds.Contains(r.EventId) && !r.Waitlisted)
            .Join(db.UserProfiles.Where(u => u.DmReminders), r => r.UserId, u => u.Id,
                (r, _) => new { r.EventId, r.OptionId, r.UserId })
            .ToListAsync(cancellationToken);
        var recipientsByEvent = seated
            .Where(r => attendingByEvent.TryGetValue(r.EventId, out var attending) && attending == r.OptionId)
            .GroupBy(r => r.EventId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.UserId).Distinct().ToList());
        if (recipientsByEvent.Count == 0)
        {
            return 0;
        }

        // 3. Server-name snapshots (ADR 0001): null just omits the "in <server>" phrase.
        var guildIds = items.Where(i => recipientsByEvent.ContainsKey(i.Event.Id)).Select(i => i.Event.GuildId).Distinct().ToList();
        var guildNames = await db.Guilds
            .Where(g => guildIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        var enqueued = 0;
        foreach (var item in items)
        {
            if (!recipientsByEvent.TryGetValue(item.Event.Id, out var recipients))
            {
                continue;
            }

            var ev = item.Event;
            foreach (var userId in recipients)
            {
                db.Deliveries.Add(new Delivery
                {
                    Id = Guid.NewGuid(),
                    Type = DeliveryType.DmEventReminder,
                    // DMs are addressed by the payload's UserId; the required ChannelId column
                    // carries the event's channel for consistency (and the jump link), like
                    // thread deliveries.
                    ChannelId = ev.ChannelId,
                    PayloadJson = JsonSerializer.Serialize(new DmEventReminderPayload(
                        userId, ev.Id, ev.Title, ev.StartsAt.ToUnixTimeSeconds(), item.Message, item.IsStart,
                        ev.GuildId, ev.ChannelId, ev.MessageId, guildNames.GetValueOrDefault(ev.GuildId))),
                    DueAt = item.DueAt,
                    Status = DeliveryStatus.Pending,
                    CreatedAt = now,
                });
                enqueued++;
            }
        }

        return enqueued;
    }

    /// <summary>Withdraws every pending DM reminder for a user — called the moment the opt-in turns
    /// off (an explicit write or a closed-DMs report), so nothing already queued can bypass the
    /// revoked consent. Matches on the payload's leading <c>"UserId":&lt;id&gt;,</c> — UserId is the
    /// first property of <see cref="DmEventReminderPayload"/>, so the prefix is exact.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many pending rows were cancelled.</returns>
    public static Task<int> CancelPendingAsync(CalCronyDbContext db, long userId, CancellationToken cancellationToken)
    {
        var prefix = "{\"UserId\":" + userId + ",";
        return db.Deliveries
            .Where(d => d.Type == DeliveryType.DmEventReminder
                        && d.Status == DeliveryStatus.Pending
                        && d.PayloadJson.StartsWith(prefix))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Cancelled), cancellationToken);
    }
}
