using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>What one user's seat changes mean for their Discord roles, as a difference between the
/// role SET their seats carried before and the set they carry after: a role in neither or both is
/// untouched, so a move between two options carrying the SAME role nets to nothing rather than
/// churning a revoke against a grant, and dropping one of two seats that both grant a role keeps
/// it. Both lists are empty for a change that touches no role.</summary>
/// <param name="Revokes">The roles to take away — held before, held by no seat now.</param>
/// <param name="Grants">The roles to hand out — held by no seat before, held now.</param>
public readonly record struct AttendeeRoleDiff(IReadOnlyList<long> Revokes, IReadOnlyList<long> Grants)
{
    /// <summary>Whether this change asks for any delivery at all.</summary>
    public bool IsNoOp => Revokes.Count == 0 && Grants.Count == 0;
}

/// <summary>Attendee-role outbox logic: pure decisions about which Discord roles a user's seated
/// RSVPs earn or lose, plus the grant/revoke delivery enqueues. Roles hang off the OPTION, not the
/// event, so one event can hand out Tank/Healer/DPS; the attending flag drives seating and
/// threads, never roles. A user holds the UNION of their seated options' roles — one seat in
/// single-choice mode, several when the event allows multiple RSVPs — and every change is the
/// set difference before/after. All Discord role changes flow through the outbox (types 10/11);
/// the handlers are best-effort so ordering holds without retries.</summary>
public static class AttendeeRoleSync
{
    /// <summary>The empty role set, for callers diffing a user who holds no seat on one side.</summary>
    public static readonly IReadOnlySet<long> NoRoles = new HashSet<long>();

    /// <summary>The attending option's id (see <see cref="RsvpPolicy.AttendingOption"/>). Null
    /// when the event has no options.</summary>
    /// <param name="options">The event's RSVP options.</param>
    /// <returns>The attending option's id, or null.</returns>
    public static Guid? AttendingOptionId(IEnumerable<RsvpOption> options) =>
        RsvpPolicy.AttendingOption(options)?.Id;

    /// <summary>The role a user seated on one option holds. Null for no option (no seat at all),
    /// an unknown option, or an option that carries no role.</summary>
    /// <param name="options">The event's RSVP options.</param>
    /// <param name="optionId">The seated option's id, or null for "no seat".</param>
    /// <returns>The Discord role id, or null.</returns>
    public static long? RoleFor(IEnumerable<RsvpOption> options, Guid? optionId) =>
        optionId is { } id ? options.FirstOrDefault(o => o.Id == id)?.AttendeeRoleId : null;

    /// <summary>The roles one user holds through a set of SEATED options: the union of those
    /// options' roles. Callers pass seated options only — a waitlisted RSVP has no seat, so it
    /// holds no role until promoted. Empty for no seats, unknown options, or roleless ones.</summary>
    /// <param name="options">The event's RSVP options.</param>
    /// <param name="seatedOptionIds">The options the user holds a seat on.</param>
    /// <returns>The distinct Discord role ids those seats carry.</returns>
    public static HashSet<long> RolesHeld(IEnumerable<RsvpOption> options, IEnumerable<Guid> seatedOptionIds)
    {
        var materialized = options as IReadOnlyCollection<RsvpOption> ?? [.. options];
        var held = new HashSet<long>();
        foreach (var optionId in seatedOptionIds)
        {
            if (RoleFor(materialized, optionId) is { } roleId)
            {
                held.Add(roleId);
            }
        }

        return held;
    }

    /// <summary>Pure decision for one user's seat changes: every role their seats carried before
    /// and carry no longer is revoked, every role they carry now and did not before is granted,
    /// and a role on both sides is left alone. Covers a fresh RSVP (empty before), an un-RSVP
    /// (empty after), a single-choice switch, and — with multiple RSVPs — adding or dropping one
    /// seat among several: Tank and Healer both granting "Raider" means dropping Tank keeps it.
    /// Lists come out in ascending role order so deliveries are deterministic.</summary>
    /// <param name="before">The roles held through seats before the change.</param>
    /// <param name="after">The roles held through seats after the change.</param>
    /// <returns>The roles to revoke and grant.</returns>
    public static AttendeeRoleDiff Diff(IReadOnlySet<long> before, IReadOnlySet<long> after) =>
        new(
            [.. before.Where(roleId => !after.Contains(roleId)).Order()],
            [.. after.Where(roleId => !before.Contains(roleId)).Order()]);

    /// <summary>Whether role deliveries may fire at all: at least one option carries a role and
    /// the event is live. RSVPs on non-live events succeed but never touch roles.</summary>
    /// <param name="ev">The event (Options loaded).</param>
    /// <returns>True when grant/revoke deliveries apply.</returns>
    public static bool IsRoleActive(Event ev) =>
        ev.Options.Any(o => o.AttendeeRoleId is not null)
        && ev.Status is EventStatus.Scheduled or EventStatus.Started;

    /// <summary>The exact payload a grant/revoke for one user carries — the value the coalescing
    /// lookups match on and the enqueued row stores.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="roleId">The Discord role id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <returns>The serialized <see cref="AttendeeRolePayload"/>.</returns>
    private static string RolePayloadJson(Event ev, long roleId, long userId) =>
        JsonSerializer.Serialize(new AttendeeRolePayload(ev.Id, ev.GuildId, roleId, userId));

    /// <summary>Loads every pending grant/revoke addressed to any of <paramref name="userIds"/> on
    /// ONE role in a single query, so a caller enqueuing for many users at once (waitlist
    /// promotion, which only ever seats users onto the attending option) coalesces against an
    /// in-memory set instead of a lookup per user. Feed the result to
    /// <see cref="EnqueueRoleChange"/>, which keeps it in step with what it enqueues.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="roleId">The Discord role id the batch is about.</param>
    /// <param name="userIds">The Discord user ids about to be enqueued for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The pending grant/revoke rows for those users; the enqueue mutates it.</returns>
    public static Task<List<Delivery>> LoadPendingRoleChangesAsync(
        CalCronyDbContext db, Event ev, long roleId, IEnumerable<long> userIds,
        CancellationToken cancellationToken) =>
        LoadPendingRoleChangesAsync(
            db, ev, userIds.Select(userId => (roleId, userId)), cancellationToken);

    /// <summary>The many-roles form: one lookup covering an arbitrary set of (role, user) changes.
    /// Per-option roles make the role vary within a single pass — an edit can revoke Tank from one
    /// user while granting Healer to another — so the diff loads every payload it may need at once
    /// rather than one query per user (the scaling problem #143 removed).</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="changes">The (role, user) pairs about to be enqueued.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The pending grant/revoke rows for those payloads.</returns>
    public static async Task<List<Delivery>> LoadPendingRoleChangesAsync(
        CalCronyDbContext db, Event ev, IEnumerable<(long RoleId, long UserId)> changes,
        CancellationToken cancellationToken)
    {
        var payloads = changes.Select(c => RolePayloadJson(ev, c.RoleId, c.UserId)).Distinct().ToList();
        return payloads.Count == 0
            ? []
            : await db.Deliveries
                .Where(d => (d.Type == DeliveryType.GrantAttendeeRole || d.Type == DeliveryType.RevokeAttendeeRole)
                            && d.Status == DeliveryStatus.Pending
                            && payloads.Contains(d.PayloadJson))
                .ToListAsync(cancellationToken);
    }

    /// <summary>Enqueues one grant or revoke against an already-loaded pending set — the batch
    /// form of <see cref="EnqueueRoleChangeAsync"/>, applying the identical coalescing rule.
    /// <paramref name="pending"/> is updated in place (a cancelled row drops out, a new row goes
    /// in) so repeated calls within one pass see each other's work — which the per-call query form
    /// cannot do, since EF never queries un-saved additions.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="type">Grant or revoke.</param>
    /// <param name="roleId">The Discord role id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="pending">The rows from <see cref="LoadPendingRoleChangesAsync"/>.</param>
    /// <param name="now">The current instant.</param>
    public static void EnqueueRoleChange(
        CalCronyDbContext db, Event ev, DeliveryType type, long roleId, long userId,
        List<Delivery> pending, Instant now)
    {
        var payloadJson = RolePayloadJson(ev, roleId, userId);
        var opposite = type == DeliveryType.GrantAttendeeRole
            ? DeliveryType.RevokeAttendeeRole
            : DeliveryType.GrantAttendeeRole;

        if (pending.Any(d => d.Type == type && d.PayloadJson == payloadJson))
        {
            return;
        }

        var cancellable = pending.FirstOrDefault(
            d => d.Type == opposite && d.Attempts == 0 && d.PayloadJson == payloadJson);
        if (cancellable is not null)
        {
            db.Deliveries.Remove(cancellable);
            pending.Remove(cancellable);
            return;
        }

        pending.Add(AddDelivery(db, ev, type, payloadJson, now));
    }

    /// <summary>Enqueues one grant or revoke with rapid-toggle coalescing: an identical pending
    /// payload of the same type dedups; an identical pending payload of the OPPOSITE type that the
    /// bot has never been served (Attempts == 0) is deleted instead — the pair nets to a no-op.
    /// An in-flight opposite (Attempts &gt; 0) always acks first (best-effort handler), so
    /// enqueueing normally keeps last-write-wins ordering. Enqueuing one role for a whole set of
    /// users at once? Use <see cref="LoadPendingRoleChangesAsync"/> with
    /// <see cref="EnqueueRoleChange"/> — same rule, one lookup for the pass instead of one per
    /// user.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="type">Grant or revoke.</param>
    /// <param name="roleId">The Discord role id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task EnqueueRoleChangeAsync(
        CalCronyDbContext db, Event ev, DeliveryType type, long roleId, long userId, IClock clock,
        CancellationToken cancellationToken)
    {
        var pending = await LoadPendingRoleChangesAsync(db, ev, roleId, [userId], cancellationToken);
        EnqueueRoleChange(db, ev, type, roleId, userId, pending, clock.GetCurrentInstant());
    }

    /// <summary>Applies the <see cref="Diff"/> of one user's role sets, revoking before granting
    /// so a move between two role-bearing options lands in the order the bot will serve. Each
    /// delivery coalesces against its own pending rows (<see cref="EnqueueRoleChangeAsync"/>) —
    /// an RSVP touches one user, so one lookup per role is the right grain here; the edit path,
    /// which touches many, batches through <see cref="LoadPendingRoleChangesAsync"/> instead.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="before">The roles the user's seats carried before the change.</param>
    /// <param name="after">The roles the user's seats carry after it.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task ApplyRoleDiffAsync(
        CalCronyDbContext db, Event ev, IReadOnlySet<long> before, IReadOnlySet<long> after, long userId,
        IClock clock, CancellationToken cancellationToken)
    {
        var diff = Diff(before, after);
        foreach (var revoked in diff.Revokes)
        {
            await EnqueueRoleChangeAsync(
                db, ev, DeliveryType.RevokeAttendeeRole, revoked, userId, clock, cancellationToken);
        }

        foreach (var granted in diff.Grants)
        {
            await EnqueueRoleChangeAsync(
                db, ev, DeliveryType.GrantAttendeeRole, granted, userId, clock, cancellationToken);
        }
    }

    /// <summary>Fans one delivery per seated user over EVERY role-bearing option — the event-wide
    /// sweep used when an event leaves the live states (end / delete / skip / cancel) and every
    /// role it handed out has to come back. Waitlisted users never held a role, so they're
    /// skipped, and there is no coalescing: these paths are one-shot.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (Options and Rsvps loaded).</param>
    /// <param name="type">Grant or revoke.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>How many deliveries were enqueued.</returns>
    public static int EnqueueRoleFanOutAll(CalCronyDbContext db, Event ev, DeliveryType type, Instant now)
    {
        var count = 0;
        foreach (var option in ev.Options.Where(o => o.AttendeeRoleId is not null))
        {
            count += EnqueueRoleFanOutForOption(db, ev, type, option.AttendeeRoleId!.Value, option.Id, now);
        }

        return count;
    }

    /// <summary>Fan-out over one option's seated RSVPs. The role id is passed explicitly so a
    /// role-change edit can revoke the OLD role after the option row was already updated.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (Rsvps loaded).</param>
    /// <param name="type">Grant or revoke.</param>
    /// <param name="roleId">The Discord role id to grant or revoke.</param>
    /// <param name="optionId">The option whose seated RSVPs are fanned over.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>How many deliveries were enqueued.</returns>
    public static int EnqueueRoleFanOutForOption(
        CalCronyDbContext db, Event ev, DeliveryType type, long roleId, Guid optionId, Instant now)
    {
        var count = 0;
        foreach (var rsvp in ev.Rsvps.Where(r => r.OptionId == optionId && !r.Waitlisted))
        {
            AddDelivery(db, ev, type, RolePayloadJson(ev, roleId, rsvp.UserId), now);
            count++;
        }

        return count;
    }

    private static Delivery AddDelivery(
        CalCronyDbContext db, Event ev, DeliveryType type, string payloadJson, Instant now)
    {
        var delivery = new Delivery
        {
            Id = Guid.NewGuid(),
            Type = type,
            // Roles are guild-level; the required ChannelId column is set for consistency but unused.
            ChannelId = ev.ChannelId,
            PayloadJson = payloadJson,
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        };
        db.Deliveries.Add(delivery);
        return delivery;
    }
}
