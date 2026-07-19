using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class NativeEventMirrorApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9600;
    private const long ChannelId = 9601;
    private const long CreatorId = 9602;
    private const long NativeId = 555001;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Guild_settings_flag_defaults_false_roundtrips_and_preserves_others()
    {
        var before = await Client.GetFromJsonAsync<GuildSettingsDto>($"/guilds/{GuildId}/settings");
        Assert.False(before!.MirrorNativeEvents);

        var put = await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings",
            new GuildSettingsDto("America/Chicago", ChannelId, true));
        put.EnsureSuccessStatusCode();

        var after = await Client.GetFromJsonAsync<GuildSettingsDto>($"/guilds/{GuildId}/settings");
        Assert.True(after!.MirrorNativeEvents);
        Assert.Equal("America/Chicago", after.TimeZone);
        Assert.Equal(ChannelId, after.DefaultChannelId);
    }

    [Fact]
    public async Task Web_non_manager_cannot_change_the_flag()
    {
        await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        var (member, _) = await fixture.LoginAsync(9650, (GuildId, "G", false));

        var put = await member.PutAsJsonAsync($"/guilds/{GuildId}/settings",
            new GuildSettingsDto("UTC", ChannelId, true));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Set_native_event_is_bot_only_and_null_clears()
    {
        var ev = await CreateEventAsync("Native id");

        var set = await Client.PutAsJsonAsync($"/events/{ev.Id}/native-event", new SetNativeEventRequest(NativeId));
        set.EnsureSuccessStatusCode();
        var withId = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(NativeId, withId!.NativeEventId);

        var cleared = await Client.PutAsJsonAsync($"/events/{ev.Id}/native-event", new SetNativeEventRequest(null));
        cleared.EnsureSuccessStatusCode();
        Assert.Null((await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.NativeEventId);

        var (member, _) = await fixture.LoginAsync(9651, (GuildId, "G", true));
        var webAttempt = await member.PutAsJsonAsync($"/events/{ev.Id}/native-event", new SetNativeEventRequest(NativeId));
        Assert.Equal(HttpStatusCode.Forbidden, webAttempt.StatusCode);
    }

    [Fact]
    public async Task Web_delete_captures_native_id_even_without_a_posted_embed()
    {
        await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        var (member, session) = await fixture.LoginAsync(9652, (GuildId, "G", false));
        var create = await member.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(0, "Mirror delete", "in 2 hours", 0));
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        // Native id recorded but NO SetMessage: the embed was never posted.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/native-event", new SetNativeEventRequest(NativeId + 1)))
            .EnsureSuccessStatusCode();

        (await member.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var delivery = await db.Deliveries.FirstAsync(d =>
            d.Type == DeliveryType.DeleteEventMessage && d.PayloadJson.Contains((NativeId + 1).ToString()));
        var payload = System.Text.Json.JsonSerializer.Deserialize<DeleteEventMessagePayload>(delivery.PayloadJson)!;
        Assert.Null(payload.MessageId);
        Assert.Equal(GuildId, payload.GuildId);
        Assert.Equal(NativeId + 1, payload.NativeEventId);
        _ = session;
    }

    [Fact]
    public async Task Skip_payload_carries_native_fields()
    {
        var ev = await CreateEventAsync("Skip native", recurring: true);
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/message", new SetEventMessageRequest(ChannelId, 777001)))
            .EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/native-event", new SetNativeEventRequest(NativeId + 2)))
            .EnsureSuccessStatusCode();

        (await Client.PostAsync($"/events/{ev.Id}/skip", null)).EnsureSuccessStatusCode();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var delivery = await db.Deliveries.FirstAsync(d =>
            d.Type == DeliveryType.DeleteEventMessage && d.PayloadJson.Contains((NativeId + 2).ToString()));
        var payload = System.Text.Json.JsonSerializer.Deserialize<DeleteEventMessagePayload>(delivery.PayloadJson)!;
        Assert.Equal(777001, payload.MessageId);
        Assert.Equal(GuildId, payload.GuildId);
        Assert.Equal(NativeId + 2, payload.NativeEventId);
    }

    [Fact]
    public async Task End_transition_enqueues_one_complete_native_event_only_when_mirrored()
    {
        var mirrored = await CreateEventAsync("Ends mirrored");
        (await Client.PutAsJsonAsync($"/events/{mirrored.Id}/native-event", new SetNativeEventRequest(NativeId + 3)))
            .EnsureSuccessStatusCode();
        var plain = await CreateEventAsync("Ends plain");

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(3));
            await db.Events.Where(e => e.Id == mirrored.Id || e.Id == plain.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.StartsAt, past)
                    .SetProperty(e => e.Status, EventStatus.Started));
        }

        await SweepAsync();
        await SweepAsync(); // idempotent — the transition fires once

        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.Equal(1, await verifyDb.Deliveries.CountAsync(d =>
            d.Type == DeliveryType.CompleteNativeEvent && d.PayloadJson.Contains(mirrored.Id.ToString())));
        Assert.Equal(0, await verifyDb.Deliveries.CountAsync(d =>
            d.Type == DeliveryType.CompleteNativeEvent && d.PayloadJson.Contains(plain.Id.ToString())));

        var row = await verifyDb.Deliveries.FirstAsync(d => d.Type == DeliveryType.CompleteNativeEvent
            && d.PayloadJson.Contains(mirrored.Id.ToString()));
        var payload = System.Text.Json.JsonSerializer.Deserialize<CompleteNativeEventPayload>(row.PayloadJson)!;
        Assert.Equal(GuildId, payload.GuildId);
        Assert.Equal(NativeId + 3, payload.NativeEventId);
    }

    [Fact]
    public async Task Start_transition_payload_carries_native_fields()
    {
        var ev = await CreateEventAsync("Starts mirrored");
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/native-event", new SetNativeEventRequest(NativeId + 4)))
            .EnsureSuccessStatusCode();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, past));
        }

        await SweepAsync();

        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var row = await verifyDb.Deliveries.FirstAsync(d =>
            d.Type == DeliveryType.EventStart && d.PayloadJson.Contains(ev.Id.ToString()));
        var payload = System.Text.Json.JsonSerializer.Deserialize<EventStartPayload>(row.PayloadJson)!;
        Assert.Equal(GuildId, payload.GuildId);
        Assert.Equal(NativeId + 4, payload.NativeEventId);
    }

    private async Task<EventDto> CreateEventAsync(string title, bool recurring = false)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 2 hours", ChannelId,
            Recurrence: recurring ? new RecurrenceRuleDto(RecurrenceUnit.Week) : null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }
}
