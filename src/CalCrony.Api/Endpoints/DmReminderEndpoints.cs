using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>The bot-only DM-reminder signals that originate in Discord: the one-time opt-in offer
/// after a first attending RSVP, the pre-send claim of a DM row, and the "Discord refused this DM"
/// report from a failed send. The preference itself is an ordinary user setting (see
/// SettingsEndpoints) and is only ever turned ON by the user — never by a creator, a server, or
/// these routes.</summary>
public static class DmReminderEndpoints
{
    /// <summary>Maps the offer, claim, and refused routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapDmReminderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/users/{userId:long}/dm-reminders/offer", Offer);
        app.MapPost("/deliveries/{id:guid}/dm-claim", Claim);
        app.MapPost("/deliveries/{id:guid}/dm-refused", Refused);
    }

    /// <summary>Claims the one-time opt-in prompt for a user: true exactly once, and only while the
    /// preference is off (a user who already opted in elsewhere is never nagged). The claim is a
    /// conditional UPDATE so two near-simultaneous RSVP interactions can't both win.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> Offer(
        HttpContext context, long userId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot())
        {
            return GuildAccessService.Forbidden();
        }

        if (await db.UserProfiles.FindAsync([userId], cancellationToken) is null)
        {
            db.UserProfiles.Add(new UserProfile { Id = userId });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Created concurrently by another request — the conditional claim below decides.
                db.ChangeTracker.Clear();
            }
        }

        var claimed = await db.UserProfiles
            .Where(u => u.Id == userId && !u.DmRemindersOffered && !u.DmReminders)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DmRemindersOffered, true), cancellationToken);
        return Results.Ok(new DmReminderOfferResponse(claimed == 1));
    }

    /// <summary>The bot's pre-send claim of a DM reminder. Eligibility is re-validated NOW, inside
    /// the claim's own transaction and under the same event-row lock the RSVP mutations take, so
    /// a concurrent un-RSVP, option switch, or drop to the waitlist either commits before the
    /// check (and the row is cancelled) or waits behind the stamp. Claims are exclusive PER
    /// RECIPIENT (a per-user advisory lock serializes the check): with several pollers only one DM
    /// for a person is in flight, so closed DMs are discovered by exactly one attempt. A claimed
    /// row is not re-served while the claim lives, which is what keeps a crash between a Discord
    /// refusal and its recorded outcome from producing a second attempt.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="id">The delivery id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> Claim(
        HttpContext context, Guid id, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot())
        {
            return GuildAccessService.Forbidden();
        }

        var delivery = await db.Deliveries.FindAsync([id], cancellationToken);
        if (delivery is null || delivery.Type != DeliveryType.DmEventReminder || delivery.RecipientUserId is not { } userId)
        {
            return Results.NotFound();
        }

        var payload = JsonSerializer.Deserialize<DmEventReminderPayload>(delivery.PayloadJson)!;
        var now = clock.GetCurrentInstant();
        var claimCutoff = now.Minus(DmReminderFanOut.ClaimTtl);

        // Lock order: recipient (advisory) then event row — the RSVP paths take only the event
        // row and nothing takes them the other way round, so there is no cycle.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({userId})", cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"""SELECT "Id" FROM "Events" WHERE "Id" = {payload.EventId} FOR UPDATE""", cancellationToken);

        await db.Entry(delivery).ReloadAsync(cancellationToken);
        if (delivery.Status != DeliveryStatus.Pending)
        {
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new DmReminderClaimResponse(DmReminderClaimOutcome.AlreadyClaimed));
        }

        var anotherInFlight = await db.Deliveries.AnyAsync(
            d => d.Id != id && d.RecipientUserId == userId && d.Status == DeliveryStatus.Pending
                 && d.ClaimedAt != null && d.ClaimedAt >= claimCutoff,
            cancellationToken);
        if (anotherInFlight || (delivery.ClaimedAt is { } held && held >= claimCutoff))
        {
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new DmReminderClaimResponse(DmReminderClaimOutcome.AlreadyClaimed));
        }

        var optedIn = await db.UserProfiles.AnyAsync(u => u.Id == userId && u.DmReminders, cancellationToken);
        var options = await db.RsvpOptions.Where(o => o.EventId == payload.EventId).ToListAsync(cancellationToken);
        var attendingId = RsvpPolicy.AttendingOption(options)?.Id;
        var seated = attendingId is { } attending && await db.Rsvps.AnyAsync(
            r => r.EventId == payload.EventId && r.UserId == userId && r.OptionId == attending && !r.Waitlisted,
            cancellationToken);
        if (!optedIn || !seated)
        {
            delivery.Status = DeliveryStatus.Cancelled;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new DmReminderClaimResponse(DmReminderClaimOutcome.Cancelled));
        }

        delivery.ClaimedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new DmReminderClaimResponse(DmReminderClaimOutcome.Claimed));
    }

    /// <summary>Records that Discord refused the DM for a claimed delivery (closed DMs or a blocked
    /// bot). The preference is switched off — and everything still queued for the user withdrawn
    /// — only if the user has not renewed their opt-in since that attempt began (a late report
    /// must never override a newer, explicit "enabled"). Either way the refused delivery itself is
    /// settled as cancelled: it is never retried. The user can turn reminders back on any time,
    /// which clears the stamp.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="id">The delivery id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> Refused(
        HttpContext context, Guid id, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot())
        {
            return GuildAccessService.Forbidden();
        }

        var delivery = await db.Deliveries.FindAsync([id], cancellationToken);
        if (delivery is null || delivery.Type != DeliveryType.DmEventReminder || delivery.RecipientUserId is not { } userId)
        {
            return Results.NotFound();
        }

        var now = clock.GetCurrentInstant();
        var attemptedAt = delivery.ClaimedAt ?? delivery.CreatedAt;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({userId})", cancellationToken);

        // Correlated switch-off: only when the consent in force is the one the attempt ran under.
        var switchedOff = await db.UserProfiles
            .Where(u => u.Id == userId && u.DmReminders
                        && (u.DmRemindersEnabledAt == null || u.DmRemindersEnabledAt <= attemptedAt))
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.DmReminders, false).SetProperty(u => u.DmRemindersBlockedAt, now),
                cancellationToken);
        if (switchedOff == 1)
        {
            await DmReminderFanOut.CancelPendingAsync(db, userId, cancellationToken);
        }

        // The refused attempt itself is done regardless.
        await db.Deliveries
            .Where(d => d.Id == id && d.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Cancelled), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }
}
