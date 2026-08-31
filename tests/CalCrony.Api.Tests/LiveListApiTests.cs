using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>Live-list registration CRUD plus the sync fan-out: event changes must enqueue
/// debounced, coalescing SyncLiveList deliveries — for BOTH caller types, unlike the per-event
/// embed sync (the bot doesn't know which channels host live lists).</summary>
public class LiveListApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 9500;
    private const long ChannelId = 9501;
    private const long CreatorId = 9502;
    private const long ListChannelId = 9510;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Create_registers_the_list_and_a_second_in_the_same_channel_conflicts()
    {
        const long channelId = 9520;
        var list = await CreateLiveListAsync(channelId, messageId: 111, limit: 5);
        Assert.Equal(GuildId, list.GuildId);
        Assert.Equal(channelId, list.ChannelId);
        Assert.Equal(111, list.MessageId);
        Assert.Equal(5, list.Limit);
        Assert.Equal(CreatorId, list.CreatorId);

        var duplicate = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/livelists",
            new CreateLiveListRequest(CreatorId, channelId, 222, 10));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var forGuild = await Client.GetFromJsonAsync<List<LiveListDto>>($"/guilds/{GuildId}/livelists");
        Assert.Contains(forGuild!, l => l.Id == list.Id);

        var one = await Client.GetFromJsonAsync<LiveListDto>($"/livelists/{list.Id}");
        Assert.Equal(list, one);
    }

    [Fact]
    public async Task Limit_is_clamped_and_channel_snapshot_is_recorded()
    {
        const long channelId = 9530;
        var list = await CreateLiveListAsync(channelId, messageId: 333, limit: 999, channelName: "events-here");
        Assert.Equal(25, list.Limit);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var snapshot = await db.Channels.SingleAsync(c => c.Id == channelId);
        Assert.Equal("events-here", snapshot.Name);
    }

    [Fact]
    public async Task Delete_removes_the_record()
    {
        var list = await CreateLiveListAsync(9540, messageId: 444, limit: 10);

        var delete = await Client.DeleteAsync($"/livelists/{list.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/livelists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/livelists/{list.Id}")).StatusCode);
    }

    [Fact]
    public async Task All_lists_route_skips_absent_guilds()
    {
        const long absentGuildId = 9600;
        var create = await Client.PostAsJsonAsync(
            $"/guilds/{absentGuildId}/livelists",
            new CreateLiveListRequest(CreatorId, 9601, 555, 10));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var list = (await create.Content.ReadFromJsonAsync<LiveListDto>())!;

        var present = await Client.GetFromJsonAsync<List<LiveListDto>>("/livelists");
        Assert.Contains(present!, l => l.Id == list.Id);

        var leave = await Client.PutAsJsonAsync(
            $"/guilds/{absentGuildId}/presence", new GuildPresenceRequest(false, null));
        leave.EnsureSuccessStatusCode();

        var afterLeave = await Client.GetFromJsonAsync<List<LiveListDto>>("/livelists");
        Assert.DoesNotContain(afterLeave!, l => l.Id == list.Id);

        // The record survives the absence for a re-invite.
        var one = await Client.GetAsync($"/livelists/{list.Id}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
    }

    [Fact]
    public async Task Referenced_channels_include_live_list_channels()
    {
        var list = await CreateLiveListAsync(9550, messageId: 666, limit: 10);

        var referenced = await Client.GetFromJsonAsync<ReferencedChannelsResponse>("/channels/referenced");
        Assert.Contains(referenced!.Channels, c => c.GuildId == GuildId && c.ChannelId == list.ChannelId);
    }

    [Fact]
    public async Task Event_mutations_enqueue_debounced_syncs_that_coalesce_for_both_caller_types()
    {
        var list = await CreateLiveListAsync(ListChannelId, messageId: 777, limit: 10);

        // Bot-created event enqueues (unlike the per-event embed sync, which the bot skips).
        var ev = await CreateEventAsync("List Fodder");
        Assert.Equal(1, await CountPendingSyncsAsync(list.Id));

        // The sync is future-dated (the debounce window) and repeats coalesce onto it.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var payload = SyncPayload(list.Id);
            var delivery = await db.Deliveries.SingleAsync(
                d => d.Type == DeliveryType.SyncLiveList && d.PayloadJson == payload);
            Assert.Equal(delivery.CreatedAt.Plus(Duration.FromSeconds(LiveListSync.DebounceSeconds)), delivery.DueAt);
        }

        await CreateEventAsync("More Fodder");
        Assert.Equal(1, await CountPendingSyncsAsync(list.Id));

        // RSVP after the first sync is "sent": a fresh one is enqueued.
        await MarkSyncsSentAsync(list.Id);
        var going = ev.Options.Single(o => o.SortOrder == 0);
        var rsvp = await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9999", new RsvpRequest(going.Id));
        rsvp.EnsureSuccessStatusCode();
        Assert.Equal(1, await CountPendingSyncsAsync(list.Id));

        // Deleting an event enqueues too.
        await MarkSyncsSentAsync(list.Id);
        var delete = await Client.DeleteAsync($"/events/{ev.Id}");
        delete.EnsureSuccessStatusCode();
        Assert.Equal(1, await CountPendingSyncsAsync(list.Id));
    }

    [Fact]
    public async Task Guild_without_live_list_enqueues_nothing()
    {
        const long otherGuildId = 9700;
        var before = await CountAllSyncsAsync();

        var response = await Client.PostAsJsonAsync(
            $"/guilds/{otherGuildId}/events",
            new CreateEventRequest(CreatorId, "Quiet Event", "in 3 hours", 9701));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Equal(before, await CountAllSyncsAsync());
    }

    [Fact]
    public async Task Scheduler_sweep_enqueues_a_sync_when_the_occurrence_rolls()
    {
        var list = await CreateLiveListAsync(9560, messageId: 888, limit: 10);

        var ev = await CreateSeriesEventAsync("Weekly Roll");
        await MarkSyncsSentAsync(list.Id);

        // Push the occurrence into the past: the sweep starts it (drops off the list) and the
        // slot stays occupied — one coalesced sync for the guild.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, past));
        }

        await SweepAsync();
        Assert.Equal(1, await CountPendingSyncsAsync(list.Id));

        // Sweep past the event's end: it ends and the series rolls its next occurrence — the
        // still-pending sync absorbs it (coalesced, not duplicated).
        await SweepAsync(SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(2)));
        Assert.Equal(1, await CountPendingSyncsAsync(list.Id));
    }

    private async Task<LiveListDto> CreateLiveListAsync(
        long channelId, long messageId, int limit, string? channelName = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/livelists",
            new CreateLiveListRequest(CreatorId, channelId, messageId, limit, channelName));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LiveListDto>())!;
    }

    private async Task<EventDto> CreateEventAsync(string title)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, "in 3 hours", ChannelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> CreateSeriesEventAsync(string title)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(
                CreatorId, title, "in 2 hours", ChannelId,
                Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task SweepAsync(Instant? now = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(now ?? SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }

    private static string SyncPayload(Guid listId) =>
        System.Text.Json.JsonSerializer.Serialize(new SyncLiveListPayload(listId));

    private async Task<int> CountPendingSyncsAsync(Guid listId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var payload = SyncPayload(listId);
        return await db.Deliveries.CountAsync(d =>
            d.Type == DeliveryType.SyncLiveList && d.Status == DeliveryStatus.Pending && d.PayloadJson == payload);
    }

    private async Task<int> CountAllSyncsAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Deliveries.CountAsync(d => d.Type == DeliveryType.SyncLiveList);
    }

    private async Task MarkSyncsSentAsync(Guid listId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var payload = SyncPayload(listId);
        await db.Deliveries
            .Where(d => d.Type == DeliveryType.SyncLiveList && d.PayloadJson == payload)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Sent));
    }
}
