using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>Web mutations must enqueue SyncEventMessage outbox rows; bot mutations must not.</summary>
public class EmbedSyncTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 7000;
    private const long ChannelId = 7001;

    [Fact]
    public async Task Web_rsvp_enqueues_one_sync_and_coalesces_repeats()
    {
        var ev = await CreateEventWithMessageAsync("Sync Party");
        var going = ev.Options.Single(o => o.SortOrder == 0);
        var maybe = ev.Options.Single(o => o.SortOrder == 2);
        var (member, session) = await fixture.LoginAsync(7101, (GuildId, "Sync", false));

        var put = await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going.Id));
        put.EnsureSuccessStatusCode();
        Assert.Equal(1, await CountPendingSyncsAsync(ev.Id));

        // Switching options while the first sync is still pending coalesces.
        var switchPut = await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(maybe.Id));
        switchPut.EnsureSuccessStatusCode();
        Assert.Equal(1, await CountPendingSyncsAsync(ev.Id));
    }

    [Fact]
    public async Task Bot_rsvp_enqueues_nothing()
    {
        var ev = await CreateEventWithMessageAsync("Bot Territory");
        var going = ev.Options.Single(o => o.SortOrder == 0);

        var put = await fixture.Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/555", new RsvpRequest(going.Id));
        put.EnsureSuccessStatusCode();

        Assert.Equal(0, await CountPendingSyncsAsync(ev.Id));
    }

    [Fact]
    public async Task Web_rsvp_on_event_without_posted_message_enqueues_nothing()
    {
        var ev = await CreateEventAsync("No Message Yet");
        var going = ev.Options.Single(o => o.SortOrder == 0);
        var (member, session) = await fixture.LoginAsync(7102, (GuildId, "Sync", false));

        var put = await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going.Id));
        put.EnsureSuccessStatusCode();

        Assert.Equal(0, await CountPendingSyncsAsync(ev.Id));
    }

    [Fact]
    public async Task Web_rsvp_delete_enqueues_sync()
    {
        var ev = await CreateEventWithMessageAsync("Unrsvp");
        var going = ev.Options.Single(o => o.SortOrder == 0);
        var (member, session) = await fixture.LoginAsync(7103, (GuildId, "Sync", false));
        await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going.Id));
        await MarkAllSyncsSentAsync(ev.Id);

        var delete = await member.DeleteAsync($"/events/{ev.Id}/rsvps/{session.UserId}");
        delete.EnsureSuccessStatusCode();

        Assert.Equal(1, await CountPendingSyncsAsync(ev.Id));
    }

    private async Task<EventDto> CreateEventAsync(string title)
    {
        await fixture.Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", null));
        var response = await fixture.Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events", new CreateEventRequest(300, title, "in 3 hours", ChannelId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> CreateEventWithMessageAsync(string title)
    {
        var ev = await CreateEventAsync(title);
        var set = await fixture.Client.PutAsJsonAsync(
            $"/events/{ev.Id}/message", new SetEventMessageRequest(ChannelId, 987654321));
        set.EnsureSuccessStatusCode();
        return (await set.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<int> CountPendingSyncsAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var payload = System.Text.Json.JsonSerializer.Serialize(new SyncEventMessagePayload(eventId));
        return await db.Deliveries.CountAsync(d =>
            d.Type == DeliveryType.SyncEventMessage && d.Status == DeliveryStatus.Pending && d.PayloadJson == payload);
    }

    private async Task MarkAllSyncsSentAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var payload = System.Text.Json.JsonSerializer.Serialize(new SyncEventMessagePayload(eventId));
        await db.Deliveries
            .Where(d => d.Type == DeliveryType.SyncEventMessage && d.PayloadJson == payload)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Sent));
    }
}
