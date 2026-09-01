using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>The two bot-only DM-reminder signals that originate in Discord: the one-time opt-in
/// offer after a first attending RSVP, and "this user's DMs are closed" feedback from a failed
/// send. The preference itself is an ordinary user setting (see SettingsEndpoints) and is only
/// ever turned ON by the user — never by a creator, a server, or these routes.</summary>
public static class DmReminderEndpoints
{
    /// <summary>Maps the offer and blocked routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapDmReminderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/users/{userId:long}/dm-reminders/offer", Offer);
        app.MapPost("/users/{userId:long}/dm-reminders/blocked", Blocked);
        app.MapPost("/deliveries/{id:guid}/dm-claim", Claim);
    }

    /// <summary>The bot's pre-send claim of a DM reminder. Eligibility is re-validated NOW, not at
    /// enqueue time: the recipient must still be opted in and still hold a seat on the event's
    /// attending option (un-RSVPing, switching option, or dropping to the waitlist all revoke it).
    /// An ineligible row is cancelled here; an eligible one is stamped so it is not re-served
    /// while the attempt — and, after a Discord refusal, the switch-off report — is in flight,
    /// which is what keeps a crash in that window from producing a second attempt.</summary>
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
        if (delivery is null || delivery.Type != DeliveryType.DmEventReminder)
        {
            return Results.NotFound();
        }

        if (delivery.Status != DeliveryStatus.Pending)
        {
            return Results.Ok(new DmReminderClaimResponse(false));
        }

        var payload = System.Text.Json.JsonSerializer.Deserialize<DmEventReminderPayload>(delivery.PayloadJson)!;
        var optedIn = await db.UserProfiles.AnyAsync(u => u.Id == payload.UserId && u.DmReminders, cancellationToken);
        var options = await db.RsvpOptions.Where(o => o.EventId == payload.EventId).ToListAsync(cancellationToken);
        var attendingId = RsvpPolicy.AttendingOption(options)?.Id;
        var seated = attendingId is { } attending && await db.Rsvps.AnyAsync(
            r => r.EventId == payload.EventId && r.UserId == payload.UserId && r.OptionId == attending && !r.Waitlisted,
            cancellationToken);
        if (!optedIn || !seated)
        {
            delivery.Status = DeliveryStatus.Cancelled;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new DmReminderClaimResponse(false));
        }

        // Conditional stamp: a row another attempt already holds is not handed out twice.
        var now = clock.GetCurrentInstant();
        var claimCutoff = now.Minus(DmReminderFanOut.ClaimTtl);
        var claimed = await db.Deliveries
            .Where(d => d.Id == id && d.Status == DeliveryStatus.Pending
                        && (d.ClaimedAt == null || d.ClaimedAt < claimCutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ClaimedAt, now), cancellationToken);
        return Results.Ok(new DmReminderClaimResponse(claimed == 1));
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

    /// <summary>Records that Discord refused a DM (closed DMs or a blocked bot): the preference is
    /// switched off and stamped, so deliveries stop instead of retrying into a wall. The user can
    /// turn it back on any time, which clears the stamp.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> Blocked(
        HttpContext context, long userId, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot())
        {
            return GuildAccessService.Forbidden();
        }

        // One transaction: the switch-off and the withdrawal of everything still queued for the
        // user land together, so a batch that already held several rows can't keep DMing a wall.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        await db.UserProfiles
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.DmReminders, false).SetProperty(u => u.DmRemindersBlockedAt, now),
                cancellationToken);
        await DmReminderFanOut.CancelPendingAsync(db, userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }
}
