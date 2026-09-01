using CalCrony.Api.Auth;
using CalCrony.Api.Data;
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

        var now = clock.GetCurrentInstant();
        await db.UserProfiles
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.DmReminders, false).SetProperty(u => u.DmRemindersBlockedAt, now),
                cancellationToken);
        return Results.NoContent();
    }
}
