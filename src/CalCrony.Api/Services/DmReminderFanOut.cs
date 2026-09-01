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
/// DMs loses nothing.</summary>
public static class DmReminderFanOut
{
    /// <summary>Enqueues one DM delivery per opted-in attendee.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event whose channel notification/start ping was just enqueued.</param>
    /// <param name="message">The notification's custom message (null for start announcements).</param>
    /// <param name="isStart">True for the "starting now" announcement.</param>
    /// <param name="dueAt">When the DM is due (the channel post's due time).</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many DM deliveries were enqueued.</returns>
    public static async Task<int> EnqueueAsync(
        CalCronyDbContext db, Event ev, string? message, bool isStart, Instant dueAt, Instant now,
        CancellationToken cancellationToken)
    {
        var options = await db.RsvpOptions.Where(o => o.EventId == ev.Id).ToListAsync(cancellationToken);
        if (RsvpPolicy.AttendingOption(options) is not { } attending)
        {
            return 0;
        }

        var recipients = await db.Rsvps
            .Where(r => r.EventId == ev.Id && r.OptionId == attending.Id && !r.Waitlisted)
            .Join(db.UserProfiles.Where(u => u.DmReminders), r => r.UserId, u => u.Id, (r, _) => r.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (recipients.Count == 0)
        {
            return 0;
        }

        // Server-name snapshot (ADR 0001): null just omits the "in <server>" phrase.
        var guildName = await db.Guilds
            .Where(g => g.Id == ev.GuildId)
            .Select(g => g.Name)
            .FirstOrDefaultAsync(cancellationToken);

        foreach (var userId in recipients)
        {
            db.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(),
                Type = DeliveryType.DmEventReminder,
                // DMs are addressed by the payload's UserId; the required ChannelId column carries
                // the event's channel for consistency (and the jump link), like thread deliveries.
                ChannelId = ev.ChannelId,
                PayloadJson = JsonSerializer.Serialize(new DmEventReminderPayload(
                    userId, ev.Id, ev.Title, ev.StartsAt.ToUnixTimeSeconds(), message, isStart,
                    ev.GuildId, ev.ChannelId, ev.MessageId, guildName)),
                DueAt = dueAt,
                Status = DeliveryStatus.Pending,
                CreatedAt = now,
            });
        }

        return recipients.Count;
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
