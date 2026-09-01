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

        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/users/{closedDms}/dm-reminders/blocked", null)).StatusCode);
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
    public async Task Offer_and_blocked_are_bot_only()
    {
        var (member, session) = await fixture.LoginAsync(12330, (GuildId, "G", false));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/users/{session.UserId}/dm-reminders/offer", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/users/{session.UserId}/dm-reminders/blocked", null)).StatusCode);
    }

    [Fact]
    public async Task Closed_dms_switch_the_preference_off_with_a_stamp_that_reenabling_clears()
    {
        const long userId = 12340;
        (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/users/{userId}/dm-reminders/blocked", null)).StatusCode);
        var blocked = await ReadAsync<UserSettingsDto>(await Client.GetAsync($"/users/{userId}/settings"));
        Assert.False(blocked.DmReminders);
        Assert.NotNull(blocked.DmRemindersBlockedAtUtc);

        var reenabled = await ReadAsync<UserSettingsDto>(await Client.PutAsJsonAsync(
            $"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)));
        Assert.True(reenabled.DmReminders);
        Assert.Null(reenabled.DmRemindersBlockedAtUtc);
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
