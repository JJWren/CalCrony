using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>What an RSVP change means for the event's attendee role.</summary>
public enum AttendeeRoleAction
{
    None = 0,
    Grant = 1,
    Revoke = 2,
}

/// <summary>Attendee-role outbox logic: pure decisions about when "Going" RSVPs earn or lose the
/// event's role, plus the grant/revoke delivery enqueues. All Discord role changes flow through
/// the outbox (types 10/11); the handlers are best-effort so ordering holds without retries.</summary>
public static class AttendeeRoleSync
{
    /// <summary>The primary "Going" option: the minimum SortOrder (today always 0). Null when the
    /// event has no options.</summary>
    /// <param name="options">The event's RSVP options.</param>
    /// <returns>The Going option's id, or null.</returns>
    public static Guid? GoingOptionId(IEnumerable<RsvpOption> options) =>
        options.OrderBy(o => o.SortOrder).Select(o => (Guid?)o.Id).FirstOrDefault();

    /// <summary>Pure decision for an RSVP change: crossing onto the Going option grants, crossing
    /// off it revokes, everything else (including Maybe↔Not going) is a no-op. Null old = fresh
    /// RSVP; null new = un-RSVP.</summary>
    /// <param name="oldOptionId">The option before the change, when an RSVP existed.</param>
    /// <param name="newOptionId">The option after the change, when an RSVP remains.</param>
    /// <param name="goingOptionId">The Going option's id.</param>
    /// <returns>The role action the change implies.</returns>
    public static AttendeeRoleAction Decide(Guid? oldOptionId, Guid? newOptionId, Guid goingOptionId)
    {
        var wasGoing = oldOptionId == goingOptionId;
        var isGoing = newOptionId == goingOptionId;
        return (wasGoing, isGoing) switch
        {
            (false, true) => AttendeeRoleAction.Grant,
            (true, false) => AttendeeRoleAction.Revoke,
            _ => AttendeeRoleAction.None,
        };
    }

    /// <summary>Whether role deliveries may fire at all: a role is set and the event is live.
    /// RSVPs on non-live events succeed but never touch roles.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>True when grant/revoke deliveries apply.</returns>
    public static bool IsRoleActive(Event ev) =>
        ev.AttendeeRoleId is not null && ev.Status is EventStatus.Scheduled or EventStatus.Started;

    /// <summary>Enqueues one grant or revoke with rapid-toggle coalescing: an identical pending
    /// payload of the same type dedups; an identical pending payload of the OPPOSITE type that the
    /// bot has never been served (Attempts == 0) is deleted instead — the pair nets to a no-op.
    /// An in-flight opposite (Attempts &gt; 0) always acks first (best-effort handler), so
    /// enqueueing normally keeps last-write-wins ordering.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="type">Grant or revoke.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task EnqueueRoleChangeAsync(
        CalCronyDbContext db, Event ev, DeliveryType type, long userId, IClock clock,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(
            new AttendeeRolePayload(ev.Id, ev.GuildId, ev.AttendeeRoleId!.Value, userId));
        var opposite = type == DeliveryType.GrantAttendeeRole
            ? DeliveryType.RevokeAttendeeRole
            : DeliveryType.GrantAttendeeRole;

        var pending = await db.Deliveries
            .Where(d => (d.Type == type || d.Type == opposite)
                        && d.Status == DeliveryStatus.Pending
                        && d.PayloadJson == payloadJson)
            .ToListAsync(cancellationToken);
        if (pending.Any(d => d.Type == type))
        {
            return;
        }

        var cancellable = pending.FirstOrDefault(d => d.Type == opposite && d.Attempts == 0);
        if (cancellable is not null)
        {
            db.Deliveries.Remove(cancellable);
            return;
        }

        AddDelivery(db, ev, type, payloadJson, clock.GetCurrentInstant());
    }

    /// <summary>Fans one delivery per user currently on the Going option (no coalescing — used by
    /// the end/delete/skip/cancel/role-change paths, which are one-shot). The role id is passed
    /// explicitly so a role-change edit can revoke the OLD role after the entity was updated.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (Options and Rsvps loaded).</param>
    /// <param name="type">Grant or revoke.</param>
    /// <param name="roleId">The Discord role id to grant or revoke.</param>
    /// <param name="now">The current instant.</param>
    public static void EnqueueRoleFanOut(CalCronyDbContext db, Event ev, DeliveryType type, long roleId, Instant now)
    {
        if (GoingOptionId(ev.Options) is not { } goingId)
        {
            return;
        }

        foreach (var rsvp in ev.Rsvps.Where(r => r.OptionId == goingId))
        {
            AddDelivery(
                db, ev, type,
                JsonSerializer.Serialize(new AttendeeRolePayload(ev.Id, ev.GuildId, roleId, rsvp.UserId)),
                now);
        }
    }

    private static void AddDelivery(CalCronyDbContext db, Event ev, DeliveryType type, string payloadJson, Instant now)
        => db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = type,
            // Roles are guild-level; the required ChannelId column is set for consistency but unused.
            ChannelId = ev.ChannelId,
            PayloadJson = payloadJson,
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
}
