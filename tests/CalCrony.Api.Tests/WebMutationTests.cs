using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>Phase B: JWT callers get bot-parity mutations with forced identity, default-channel
/// requirements, and embed-lifecycle deliveries.</summary>
public class WebMutationTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long DefaultChannelId = 8001;

    [Fact]
    public async Task Member_creates_event_with_forced_creator_and_post_delivery()
    {
        var guildId = await SeedGuildAsync(8100, withDefaultChannel: true);
        var (member, session) = await fixture.LoginAsync(8101, (guildId, "G", false));

        var response = await member.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(999999 /* spoof attempt — must be ignored */, "Web Party", "in 3 hours", 424242));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ev = (await response.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(session.UserId, ev.CreatorId);
        Assert.Equal(DefaultChannelId, ev.ChannelId);
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.PostEventMessage, ev.Id));
    }

    [Fact]
    public async Task Create_without_default_channel_is_blocked_with_actionable_error()
    {
        var guildId = await SeedGuildAsync(8110, withDefaultChannel: false);
        var (member, _) = await fixture.LoginAsync(8111, (guildId, "G", false));

        var response = await member.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, "Doomed", "in 3 hours", 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("/settings default-channel", error!.Error);
    }

    [Fact]
    public async Task Non_member_cannot_create()
    {
        var guildId = await SeedGuildAsync(8120, withDefaultChannel: true);
        var (outsider, _) = await fixture.LoginAsync(8121 /* no guilds */);

        var response = await outsider.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, "Nope", "in 3 hours", 0));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Creator_edits_and_sync_is_enqueued_member_cannot_edit()
    {
        var guildId = await SeedGuildAsync(8130, withDefaultChannel: true);
        var (creator, _) = await fixture.LoginAsync(8131, (guildId, "G", false));
        var (member, _) = await fixture.LoginAsync(8132, (guildId, "G", false));
        var ev = await WebCreateWithMessageAsync(creator, guildId, "Editable");

        var memberEdit = await member.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(0, Title: "Hijacked"));
        Assert.Equal(HttpStatusCode.Forbidden, memberEdit.StatusCode);

        var creatorEdit = await creator.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(0, Title: "Renamed"));
        creatorEdit.EnsureSuccessStatusCode();
        Assert.Equal("Renamed", (await creatorEdit.Content.ReadFromJsonAsync<EventDto>())!.Title);
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.SyncEventMessage, ev.Id));
    }

    [Fact]
    public async Task Manager_can_edit_others_events()
    {
        var guildId = await SeedGuildAsync(8140, withDefaultChannel: true);
        var (creator, _) = await fixture.LoginAsync(8141, (guildId, "G", false));
        var (manager, _) = await fixture.LoginAsync(8142, (guildId, "G", true));
        var ev = await WebCreateWithMessageAsync(creator, guildId, "Managed");

        var edit = await manager.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(0, Title: "Manager Renamed"));

        edit.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Web_delete_enqueues_delete_message_with_captured_ids()
    {
        var guildId = await SeedGuildAsync(8150, withDefaultChannel: true);
        var (creator, _) = await fixture.LoginAsync(8151, (guildId, "G", false));
        var ev = await WebCreateWithMessageAsync(creator, guildId, "Doomed Event");

        var delete = await creator.DeleteAsync($"/events/{ev.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.False(await db.Events.AnyAsync(e => e.Id == ev.Id));
        var payload = System.Text.Json.JsonSerializer.Serialize(new DeleteEventMessagePayload(DefaultChannelId, 777000));
        Assert.True(await db.Deliveries.AnyAsync(d =>
            d.Type == DeliveryType.DeleteEventMessage && d.PayloadJson == payload));
    }

    [Fact]
    public async Task Creator_manages_notifications_member_cannot()
    {
        var guildId = await SeedGuildAsync(8160, withDefaultChannel: true);
        var (creator, _) = await fixture.LoginAsync(8161, (guildId, "G", false));
        var (member, _) = await fixture.LoginAsync(8162, (guildId, "G", false));
        var ev = await WebCreateWithMessageAsync(creator, guildId, "Notifiable");

        var memberAdd = await member.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30));
        Assert.Equal(HttpStatusCode.Forbidden, memberAdd.StatusCode);

        var creatorAdd = await creator.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, "soon!"));
        Assert.Equal(HttpStatusCode.Created, creatorAdd.StatusCode);
        var notification = (await creatorAdd.Content.ReadFromJsonAsync<EventNotificationDto>())!;

        var creatorDelete = await creator.DeleteAsync($"/events/{ev.Id}/notifications/{notification.Id}");
        Assert.Equal(HttpStatusCode.NoContent, creatorDelete.StatusCode);
    }

    [Fact]
    public async Task Web_reminder_is_self_forced_into_default_channel()
    {
        var guildId = await SeedGuildAsync(8170, withDefaultChannel: true);
        var (member, session) = await fixture.LoginAsync(8171, (guildId, "G", false));

        var response = await member.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(guildId, 999999, 424242, "in 2 hours", "stretch"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reminder = (await response.Content.ReadFromJsonAsync<ReminderDto>())!;
        Assert.Equal(DefaultChannelId, reminder.ChannelId);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var delivery = await db.Deliveries.SingleAsync(d => d.Id == reminder.Id);
        Assert.Contains($"\"UserId\":{session.UserId}", delivery.PayloadJson);
    }

    [Fact]
    public async Task Reminder_without_default_channel_is_blocked()
    {
        var guildId = await SeedGuildAsync(8180, withDefaultChannel: false);
        var (member, _) = await fixture.LoginAsync(8181, (guildId, "G", false));

        var response = await member.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(guildId, 0, 0, "in 2 hours", "void"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Guild_settings_put_is_manager_only()
    {
        var guildId = await SeedGuildAsync(8190, withDefaultChannel: true);
        var (member, _) = await fixture.LoginAsync(8191, (guildId, "G", false));
        var (manager, _) = await fixture.LoginAsync(8192, (guildId, "G", true));

        var memberPut = await member.PutAsJsonAsync($"/guilds/{guildId}/settings",
            new GuildSettingsDto("America/Chicago", DefaultChannelId));
        Assert.Equal(HttpStatusCode.Forbidden, memberPut.StatusCode);

        var managerPut = await manager.PutAsJsonAsync($"/guilds/{guildId}/settings",
            new GuildSettingsDto("America/Chicago", DefaultChannelId));
        managerPut.EnsureSuccessStatusCode();
        Assert.Equal("America/Chicago", (await managerPut.Content.ReadFromJsonAsync<GuildSettingsDto>())!.TimeZone);
    }

    [Fact]
    public async Task User_settings_put_is_self_only()
    {
        var guildId = await SeedGuildAsync(8200, withDefaultChannel: true);
        var (member, session) = await fixture.LoginAsync(8201, (guildId, "G", false));

        var self = await member.PutAsJsonAsync($"/users/{session.UserId}/settings", new UserSettingsDto("UTC", false));
        self.EnsureSuccessStatusCode();

        var other = await member.PutAsJsonAsync("/users/999999/settings", new UserSettingsDto("UTC", true));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Parse_datetime_is_member_gated_for_web_callers()
    {
        var guildId = await SeedGuildAsync(8210, withDefaultChannel: true);
        var otherGuildId = await SeedGuildAsync(8211, withDefaultChannel: true);
        var (member, _) = await fixture.LoginAsync(8212, (guildId, "G", false));

        var ok = await member.PostAsJsonAsync("/tools/parse-datetime",
            new ParseDateTimeRequest("in 2 hours", null, guildId));
        ok.EnsureSuccessStatusCode();

        var denied = await member.PostAsJsonAsync("/tools/parse-datetime",
            new ParseDateTimeRequest("in 2 hours", null, otherGuildId));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private async Task<long> SeedGuildAsync(long guildId, bool withDefaultChannel)
    {
        var response = await fixture.Client.PutAsJsonAsync($"/guilds/{guildId}/settings",
            new GuildSettingsDto("UTC", withDefaultChannel ? DefaultChannelId : null));
        response.EnsureSuccessStatusCode();
        return guildId;
    }

    /// <summary>Web-creates an event then simulates the bot posting its embed (message id 777000)
    /// so edit/delete flows exercise the sync/delete deliveries.</summary>
    private async Task<EventDto> WebCreateWithMessageAsync(HttpClient creator, long guildId, string title)
    {
        var create = await creator.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, title, "in 3 hours", 0));
        create.EnsureSuccessStatusCode();
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        var set = await fixture.Client.PutAsJsonAsync($"/events/{ev.Id}/message",
            new SetEventMessageRequest(DefaultChannelId, 777000));
        set.EnsureSuccessStatusCode();
        return (await set.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<int> CountDeliveriesAsync(DeliveryType type, Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var needle = eventId.ToString();
        return await db.Deliveries.CountAsync(d => d.Type == type && d.PayloadJson.Contains(needle));
    }
}
