using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>DM reminders (issue #123): the strictly personal opt-in (default off, null-keeps on
/// write), the one-time offer claim, the closed-DMs switch-off, and the outbox fan-out that DMs
/// only opted-in users who hold a SEAT on the attending option.</summary>
public class DmReminderApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 12300;
    private const long ChannelId = 12301;
    private const long CreatorId = 12302;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Opt_in_is_off_by_default_null_keeps_it_and_true_turns_it_on()
    {
        const long userId = 12310;
        var initial = await ReadAsync<UserSettingsDto>(await Client.GetAsync($"/users/{userId}/settings"));
        Assert.False(initial.DmReminders);

        // A writer that doesn't know about the field (the bot's timezone write) must not clobber it.
        var enabled = await ReadAsync<UserSettingsDto>(await Client.PutAsJsonAsync(
            $"/users/{userId}/settings", new UserSettingsDto("UTC", true, DmReminders: true)));
        Assert.True(enabled.DmReminders);

        var kept = await ReadAsync<UserSettingsDto>(await Client.PutAsJsonAsync(
            $"/users/{userId}/settings", new UserSettingsDto("America/Chicago", true)));
        Assert.True(kept.DmReminders);
        Assert.Equal("America/Chicago", kept.TimeZone);

        var disabled = await ReadAsync<UserSettingsDto>(await Client.PutAsJsonAsync(
            $"/users/{userId}/settings", new UserSettingsDto("America/Chicago", true, DmReminders: false)));
        Assert.False(disabled.DmReminders);
    }

    [Fact]
    public async Task The_offer_is_claimed_exactly_once_and_never_while_already_on()
    {
        const long fresh = 12320;
        Assert.True((await ReadAsync<DmReminderOfferResponse>(await Client.PostAsync($"/users/{fresh}/dm-reminders/offer", null))).Offer);
        Assert.False((await ReadAsync<DmReminderOfferResponse>(await Client.PostAsync($"/users/{fresh}/dm-reminders/offer", null))).Offer);

        // Someone who already opted in elsewhere is never nagged — not even after turning it off
        // again: finding the setting once consumes the offer for good.
        const long alreadyOn = 12321;
        (await Client.PutAsJsonAsync($"/users/{alreadyOn}/settings", new UserSettingsDto(null, true, DmReminders: true)))
            .EnsureSuccessStatusCode();
        Assert.False((await ReadAsync<DmReminderOfferResponse>(await Client.PostAsync($"/users/{alreadyOn}/dm-reminders/offer", null))).Offer);
        (await Client.PutAsJsonAsync($"/users/{alreadyOn}/settings", new UserSettingsDto(null, true, DmReminders: false)))
            .EnsureSuccessStatusCode();
        Assert.False((await ReadAsync<DmReminderOfferResponse>(await Client.PostAsync($"/users/{alreadyOn}/dm-reminders/offer", null))).Offer);
    }

    [Fact]
    public async Task Turning_the_opt_in_off_withdraws_queued_dms_by_either_path()
    {
        const long explicitOff = 12361;
        const long closedDms = 12362;
        foreach (var userId in new[] { explicitOff, closedDms })
        {
            (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)))
                .EnsureSuccessStatusCode();
        }

        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Withdrawn DMs", "in 3 hours", ChannelId));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var going = ev.Options.OrderBy(o => o.SortOrder).First();
        foreach (var userId in new[] { explicitOff, closedDms })
        {
            (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        }

        (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200, null, null)))
            .EnsureSuccessStatusCode();
        await SweepAsync(SystemClock.Instance.GetCurrentInstant());
        Assert.Equal(2, (await PendingDmRowsAsync(ev.Id)).Count);

        // Explicit opt-out and a closed-DMs report both withdraw what was queued for that user.
        (await Client.PutAsJsonAsync($"/users/{explicitOff}/settings", new UserSettingsDto(null, true, DmReminders: false)))
            .EnsureSuccessStatusCode();
        var afterExplicit = await PendingDmRowsAsync(ev.Id);
        Assert.Equal([closedDms], afterExplicit.Select(p => p.UserId));

        var closedRow = (await DmRowsAsync(ev.Id)).Single(r => r.Payload.UserId == closedDms);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/deliveries/{closedRow.Id}/dm-refused", null)).StatusCode);
        Assert.Empty(await PendingDmRowsAsync(ev.Id));
    }

    [Fact]
    public async Task One_sweep_fans_out_several_due_events_in_a_single_batch()
    {
        const long userId = 12371;
        (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)))
            .EnsureSuccessStatusCode();

        var eventIds = new List<Guid>();
        foreach (var title in new[] { "Batch A", "Batch B", "Batch C" })
        {
            var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(CreatorId, title, "in 3 hours", ChannelId));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
            eventIds.Add(ev.Id);
            (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(ev.Options.OrderBy(o => o.SortOrder).First().Id))).EnsureSuccessStatusCode();
            (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200, title, null))).EnsureSuccessStatusCode();
        }

        await SweepAsync(SystemClock.Instance.GetCurrentInstant());

        // One DM per due event for the one recipient — and the batch didn't cross-wire messages.
        var rows = new List<DmEventReminderPayload>();
        foreach (var id in eventIds)
        {
            rows.AddRange(await DmDeliveriesAsync(id));
        }

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(userId, r.UserId));
        Assert.Equal(["Batch A", "Batch B", "Batch C"], rows.OrderBy(r => r.Title).Select(r => r.Message));
        Assert.All(rows, r => Assert.Equal(r.Title, r.Message));
    }

    [Fact]
    public async Task Claiming_revalidates_the_seat_and_opt_in_cancels_otherwise_and_parks_the_row()
    {
        const long stillSeated = 12381;
        const long unRsvped = 12382;
        const long waitlistedLater = 12383;
        const long optedOutLater = 12384;
        foreach (var userId in new[] { stillSeated, unRsvped, waitlistedLater, optedOutLater })
        {
            (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)))
                .EnsureSuccessStatusCode();
        }

        // Capacity 4 seats everyone at first; the "waitlisted later" case is produced by
        // lowering the limit after the fact.
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Claim checks", "in 3 hours", ChannelId, AttendeeLimit: 4));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var going = ev.Options.OrderBy(o => o.SortOrder).First();
        foreach (var userId in new[] { stillSeated, unRsvped, optedOutLater, waitlistedLater })
        {
            (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        }

        (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200, null, null)))
            .EnsureSuccessStatusCode();
        await SweepAsync(SystemClock.Instance.GetCurrentInstant());
        var rows = await DmRowsAsync(ev.Id);
        Assert.Equal(4, rows.Count);

        // Things change between enqueue and send.
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/{unRsvped}")).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/users/{optedOutLater}/settings", new UserSettingsDto(null, true, DmReminders: false)))
            .EnsureSuccessStatusCode(); // (this one is withdrawn immediately by the opt-out itself)
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Rsvps.Where(r => r.EventId == ev.Id && r.UserId == waitlistedLater)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Waitlisted, true));
        }

        async Task<DmReminderClaimOutcome> ClaimAsync(long userId)
        {
            var row = rows.Single(r => r.Payload.UserId == userId);
            var response = await Client.PostAsync($"/deliveries/{row.Id}/dm-claim", null);
            return (await ReadAsync<DmReminderClaimResponse>(response)).Outcome;
        }

        Assert.Equal(DmReminderClaimOutcome.Cancelled, await ClaimAsync(unRsvped));
        Assert.Equal(DmReminderClaimOutcome.Cancelled, await ClaimAsync(waitlistedLater));
        // Already withdrawn by the opt-out itself, so the claim finds it non-pending.
        Assert.Equal(DmReminderClaimOutcome.AlreadyClaimed, await ClaimAsync(optedOutLater));
        Assert.Equal(DmReminderClaimOutcome.Claimed, await ClaimAsync(stillSeated));

        // Cancelled rows stay cancelled even if the bot acks them; the claimed row is parked
        // (not re-served) while the attempt is in flight, and a second claim doesn't hand it out again.
        var unRsvpedRow = rows.Single(r => r.Payload.UserId == unRsvped);
        (await Client.PostAsync($"/deliveries/{unRsvpedRow.Id}/ack", null)).EnsureSuccessStatusCode();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var statuses = await db.Deliveries.Where(d => rows.Select(r => r.Id).Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => (d.Status, d.ClaimedAt));
            Assert.Equal(DeliveryStatus.Cancelled, statuses[unRsvpedRow.Id].Status);
            Assert.Equal(DeliveryStatus.Cancelled, statuses[rows.Single(r => r.Payload.UserId == waitlistedLater).Id].Status);
            Assert.Equal(DeliveryStatus.Cancelled, statuses[rows.Single(r => r.Payload.UserId == optedOutLater).Id].Status);
            var claimedRow = statuses[rows.Single(r => r.Payload.UserId == stillSeated).Id];
            Assert.Equal(DeliveryStatus.Pending, claimedRow.Status);
            Assert.NotNull(claimedRow.ClaimedAt);
        }

        var pending = await ReadAsync<List<DeliveryDto>>(await Client.GetAsync("/deliveries/pending?limit=50"));
        Assert.DoesNotContain(pending, d => rows.Select(r => r.Id).Contains(d.Id));
        Assert.Equal(DmReminderClaimOutcome.AlreadyClaimed, await ClaimAsync(stillSeated)); // never handed out twice
    }

    [Fact]
    public async Task Only_one_dm_per_recipient_can_be_in_flight_across_pollers()
    {
        const long userId = 12391;
        (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)))
            .EnsureSuccessStatusCode();

        // Two due events → two pending DM rows for the same person in one sweep.
        var eventIds = new List<Guid>();
        foreach (var title in new[] { "Flight A", "Flight B" })
        {
            var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(CreatorId, title, "in 3 hours", ChannelId));
            var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
            eventIds.Add(ev.Id);
            (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(ev.Options.OrderBy(o => o.SortOrder).First().Id))).EnsureSuccessStatusCode();
            (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200, null, null))).EnsureSuccessStatusCode();
        }

        await SweepAsync(SystemClock.Instance.GetCurrentInstant());
        var rowA = (await DmRowsAsync(eventIds[0])).Single();
        var rowB = (await DmRowsAsync(eventIds[1])).Single();

        async Task<DmReminderClaimOutcome> ClaimAsync(Guid id) =>
            (await ReadAsync<DmReminderClaimResponse>(await Client.PostAsync($"/deliveries/{id}/dm-claim", null))).Outcome;

        // Two pollers claim the two rows AT THE SAME TIME: exactly one wins, the other is parked.
        var outcomes = await Task.WhenAll(ClaimAsync(rowA.Id), ClaimAsync(rowB.Id));
        Assert.Single(outcomes, o => o == DmReminderClaimOutcome.Claimed);
        Assert.Single(outcomes, o => o == DmReminderClaimOutcome.AlreadyClaimed);
        if (outcomes[0] != DmReminderClaimOutcome.Claimed)
        {
            (rowA, rowB) = (rowB, rowA); // continue with whichever row won
        }

        // While parked the loser is not served at all, so polling can't burn its attempt budget.
        var attemptsBefore = await AttemptsAsync(rowB.Id);
        for (var poll = 0; poll < 3; poll++)
        {
            var pending = await ReadAsync<List<DeliveryDto>>(await Client.GetAsync("/deliveries/pending?limit=50"));
            Assert.DoesNotContain(pending, d => d.Id == rowB.Id || d.Id == rowA.Id);
        }

        Assert.Equal(attemptsBefore, await AttemptsAsync(rowB.Id));

        // Once A has settled, B is served again and can be claimed.
        (await Client.PostAsync($"/deliveries/{rowA.Id}/ack", null)).EnsureSuccessStatusCode();
        var served = await ReadAsync<List<DeliveryDto>>(await Client.GetAsync("/deliveries/pending?limit=50"));
        Assert.Contains(served, d => d.Id == rowB.Id);
        Assert.Equal(DmReminderClaimOutcome.Claimed, await ClaimAsync(rowB.Id));
    }

    private async Task<int> AttemptsAsync(Guid deliveryId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return (await db.Deliveries.SingleAsync(d => d.Id == deliveryId)).Attempts;
    }

    [Fact]
    public async Task Reminders_and_start_pings_fan_out_by_dm_to_opted_in_seated_attendees_only()
    {
        const long seatedOptedIn = 12351;
        const long seatedOptedOut = 12352;
        const long waitlistedOptedIn = 12353;
        const long maybeOptedIn = 12354;
        foreach (var userId in new[] { seatedOptedIn, waitlistedOptedIn, maybeOptedIn })
        {
            (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)))
                .EnsureSuccessStatusCode();
        }

        (await Client.PutAsJsonAsync($"/guilds/{GuildId}/presence", new GuildPresenceRequest(true, "The Keep")))
            .EnsureSuccessStatusCode();
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "DM fan-out", "in 3 hours", ChannelId, AttendeeLimit: 2));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var going = ev.Options.OrderBy(o => o.SortOrder).First();
        var maybe = ev.Options.Single(o => o.Label == "Maybe");
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/message", new SetEventMessageRequest(ChannelId, 424243))).EnsureSuccessStatusCode();
        foreach (var (userId, optionId) in new[] { (seatedOptedIn, going.Id), (seatedOptedOut, going.Id), (waitlistedOptedIn, going.Id), (maybeOptedIn, maybe.Id) })
        {
            (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(optionId))).EnsureSuccessStatusCode();
        }

        // A 200-minutes-before notification is already due for an event 3 hours out.
        (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200, "Bring dice", "@here")))
            .EnsureSuccessStatusCode();
        await SweepAsync(SystemClock.Instance.GetCurrentInstant());

        var reminders = await DmDeliveriesAsync(ev.Id);
        var reminder = Assert.Single(reminders);
        Assert.Equal(seatedOptedIn, reminder.UserId); // not the opted-out seat, the waitlister, or the Maybe
        Assert.False(reminder.IsStart);
        Assert.Equal("Bring dice", reminder.Message);
        Assert.Equal("The Keep", reminder.GuildName);
        Assert.Equal(424243, reminder.MessageId);

        // Sweeping past the start adds the start announcement DM for the same single recipient.
        await SweepAsync(SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(4)));
        var all = await DmDeliveriesAsync(ev.Id);
        Assert.Equal(2, all.Count);
        var start = Assert.Single(all, d => d.IsStart);
        Assert.Equal(seatedOptedIn, start.UserId);
        Assert.Null(start.Message);
    }

    // ---------- helpers ----------

    private async Task SweepAsync(Instant now)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(now, CancellationToken.None);
    }

    [Fact]
    public async Task Offer_claim_and_refused_are_bot_only()
    {
        var (member, session) = await fixture.LoginAsync(12330, (GuildId, "G", false));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/users/{session.UserId}/dm-reminders/offer", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/deliveries/{Guid.NewGuid()}/dm-claim", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/deliveries/{Guid.NewGuid()}/dm-refused", null)).StatusCode);
    }

    [Fact]
    public async Task A_refused_dm_switches_the_preference_off_unless_consent_was_renewed_since_the_attempt()
    {
        const long staleConsent = 12341;
        const long renewedConsent = 12342;
        var rows = await SeedClaimedDmRowsAsync("Refusals", staleConsent, renewedConsent);

        // The user whose consent predates the attempt: switched off, stamped, remaining rows withdrawn.
        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/deliveries/{rows[staleConsent]}/dm-refused", null)).StatusCode);
        var stale = await ReadAsync<UserSettingsDto>(await Client.GetAsync($"/users/{staleConsent}/settings"));
        Assert.False(stale.DmReminders);
        Assert.NotNull(stale.DmRemindersBlockedAtUtc);

        // The user who explicitly re-enabled AFTER the attempt began keeps the newer consent.
        (await Client.PutAsJsonAsync($"/users/{renewedConsent}/settings", new UserSettingsDto(null, true, DmReminders: false))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/users/{renewedConsent}/settings", new UserSettingsDto(null, true, DmReminders: true))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/deliveries/{rows[renewedConsent]}/dm-refused", null)).StatusCode);
        var renewed = await ReadAsync<UserSettingsDto>(await Client.GetAsync($"/users/{renewedConsent}/settings"));
        Assert.True(renewed.DmReminders);
        Assert.Null(renewed.DmRemindersBlockedAtUtc);

        // Either way the refused attempt itself is settled, never retried; re-enabling clears the stamp.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            Assert.Equal(DeliveryStatus.Cancelled, (await db.Deliveries.SingleAsync(d => d.Id == rows[staleConsent])).Status);
            Assert.Equal(DeliveryStatus.Cancelled, (await db.Deliveries.SingleAsync(d => d.Id == rows[renewedConsent])).Status);
        }

        var reenabled = await ReadAsync<UserSettingsDto>(await Client.PutAsJsonAsync(
            $"/users/{staleConsent}/settings", new UserSettingsDto(null, true, DmReminders: true)));
        Assert.True(reenabled.DmReminders);
        Assert.Null(reenabled.DmRemindersBlockedAtUtc);
    }

    /// <summary>Opts the users in, seats them on one event with a due notification, sweeps, and
    /// claims each user's DM row; returns the claimed delivery id per user.</summary>
    private async Task<Dictionary<long, Guid>> SeedClaimedDmRowsAsync(string title, params long[] userIds)
    {
        foreach (var userId in userIds)
        {
            (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true))).EnsureSuccessStatusCode();
        }

        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(CreatorId, title, "in 3 hours", ChannelId));
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var going = ev.Options.OrderBy(o => o.SortOrder).First();
        foreach (var userId in userIds)
        {
            (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        }

        (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200, null, null))).EnsureSuccessStatusCode();
        await SweepAsync(SystemClock.Instance.GetCurrentInstant());
        var rows = await DmRowsAsync(ev.Id);
        var claimed = new Dictionary<long, Guid>();
        foreach (var userId in userIds)
        {
            var row = rows.Single(r => r.Payload.UserId == userId);
            var outcome = (await ReadAsync<DmReminderClaimResponse>(await Client.PostAsync($"/deliveries/{row.Id}/dm-claim", null))).Outcome;
            Assert.Equal(DmReminderClaimOutcome.Claimed, outcome);
            claimed[userId] = row.Id;
        }

        return claimed;
    }

    private async Task<List<(Guid Id, DmEventReminderPayload Payload)>> DmRowsAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == DeliveryType.DmEventReminder && d.PayloadJson.Contains(eventId.ToString()))
            .ToListAsync();
        return [.. rows.Select(d => (d.Id, JsonSerializer.Deserialize<DmEventReminderPayload>(d.PayloadJson)!))];
    }

    private async Task<List<DmEventReminderPayload>> PendingDmRowsAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == DeliveryType.DmEventReminder && d.Status == DeliveryStatus.Pending
                        && d.PayloadJson.Contains(eventId.ToString()))
            .ToListAsync();
        return [.. rows.Select(d => JsonSerializer.Deserialize<DmEventReminderPayload>(d.PayloadJson)!)];
    }

    private async Task<List<DmEventReminderPayload>> DmDeliveriesAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == DeliveryType.DmEventReminder && d.PayloadJson.Contains(eventId.ToString()))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
        return [.. rows.Select(d => JsonSerializer.Deserialize<DmEventReminderPayload>(d.PayloadJson)!)];
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
