using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class EventThreadApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9800;
    private const long ChannelId = 9801;
    private const long CreatorId = 9802;
    private const long ThreadId = 888100;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Wants_thread_roundtrips_for_bot_and_web_callers()
    {
        var botEvent = await CreateEventAsync("Bot thread", wantsThread: true);
        Assert.True(botEvent.WantsThread);
        Assert.Null(botEvent.ThreadId);

        await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        var (member, _) = await fixture.LoginAsync(9850, (GuildId, "G", false));
        var create = await member.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(0, "Web thread", "in 2 hours", 0, WantsThread: true));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        // Unlike AttendeeRoleId, WantsThread is honored for web callers.
        Assert.True((await create.Content.ReadFromJsonAsync<EventDto>())!.WantsThread);
    }

    [Fact]
    public async Task Set_thread_is_bot_only_and_null_clears()
    {
        var ev = await CreateEventAsync("Thread id", wantsThread: true);

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/thread", new SetThreadRequest(ThreadId)))
            .EnsureSuccessStatusCode();
        Assert.Equal(ThreadId, (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.ThreadId);

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/thread", new SetThreadRequest(null)))
            .EnsureSuccessStatusCode();
        Assert.Null((await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.ThreadId);

        var (member, _) = await fixture.LoginAsync(9851, (GuildId, "G", true));
        var webAttempt = await member.PutAsJsonAsync($"/events/{ev.Id}/thread", new SetThreadRequest(ThreadId));
        Assert.Equal(HttpStatusCode.Forbidden, webAttempt.StatusCode);
    }

    [Fact]
    public async Task Going_rsvp_enqueues_member_add_with_dedup_and_no_removal_on_switch_away()
    {
        var ev = await CreateThreadedEventAsync("Thread joins", ThreadId + 1);
        var going = GoingOption(ev);
        var maybe = ev.Options.First(o => o.Id != going.Id);

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9852", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9852", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();

        var adds = await ThreadDeliveriesAsync(ev.Id, DeliveryType.AddThreadMember);
        var add = Assert.Single(adds); // dedup
        Assert.Equal(GuildId, add.GuildId);
        Assert.Equal(ThreadId + 1, add.ThreadId);
        Assert.Equal(9852, add.UserId);

        // Add-only: crossing off Going (or un-RSVPing) produces no thread deliveries at all.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9852", new RsvpRequest(maybe.Id))).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/9852")).EnsureSuccessStatusCode();
        Assert.Single(await ThreadDeliveriesAsync(ev.Id, DeliveryType.AddThreadMember));
        Assert.Empty(await ArchiveDeliveriesAsync(ev.Id));

        // Maybe RSVP never enqueues a member add.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9853", new RsvpRequest(maybe.Id))).EnsureSuccessStatusCode();
        Assert.Single(await ThreadDeliveriesAsync(ev.Id, DeliveryType.AddThreadMember));
    }

    [Fact]
    public async Task Threadless_events_never_enqueue_thread_deliveries()
    {
        var ev = await CreateEventAsync("No thread", wantsThread: true); // wants one, none created yet
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9854", new RsvpRequest(GoingOption(ev).Id)))
            .EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode();

        Assert.Empty(await ThreadDeliveriesAsync(ev.Id, DeliveryType.AddThreadMember));
        Assert.Empty(await ArchiveDeliveriesAsync(ev.Id));
    }

    [Fact]
    public async Task End_sweep_archives_the_thread_once()
    {
        var ev = await CreateThreadedEventAsync("End archive", ThreadId + 2);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(3));
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.StartsAt, past)
                    .SetProperty(e => e.Status, EventStatus.Started));
        }

        await SweepAsync();
        await SweepAsync(); // idempotent — the transition fires once

        var archive = Assert.Single(await ArchiveDeliveriesAsync(ev.Id));
        Assert.Equal(GuildId, archive.GuildId);
        Assert.Equal(ThreadId + 2, archive.ThreadId);
    }

    [Fact]
    public async Task Delete_and_patch_cancel_archive_the_thread()
    {
        var deleted = await CreateThreadedEventAsync("Delete archive", ThreadId + 3);
        (await Client.DeleteAsync($"/events/{deleted.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(ThreadId + 3, Assert.Single(await ArchiveDeliveriesAsync(deleted.Id)).ThreadId);

        var cancelled = await CreateThreadedEventAsync("Cancel archive", ThreadId + 4);
        (await Client.PatchAsJsonAsync($"/events/{cancelled.Id}",
            new UpdateEventRequest(CreatorId, Status: EventStatus.Cancelled))).EnsureSuccessStatusCode();
        Assert.Equal(ThreadId + 4, Assert.Single(await ArchiveDeliveriesAsync(cancelled.Id)).ThreadId);
    }

    [Fact]
    public async Task Skip_archives_and_the_spawned_occurrence_wants_its_own_thread()
    {
        var ev = await CreateEventAsync("Skip thread", wantsThread: true, recurring: true);
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/thread", new SetThreadRequest(ThreadId + 5)))
            .EnsureSuccessStatusCode();

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.True(next.WantsThread);   // inherited template field
        Assert.Null(next.ThreadId);      // its own thread opens when its embed posts

        Assert.Equal(ThreadId + 5, Assert.Single(await ArchiveDeliveriesAsync(ev.Id)).ThreadId);
    }

    private static RsvpOptionDto GoingOption(EventDto ev) => ev.Options.OrderBy(o => o.SortOrder).First();

    private async Task<EventDto> CreateEventAsync(string title, bool wantsThread, bool recurring = false)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 2 hours", ChannelId,
            Recurrence: recurring ? new RecurrenceRuleDto(RecurrenceUnit.Week) : null,
            WantsThread: wantsThread));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> CreateThreadedEventAsync(string title, long threadId)
    {
        var ev = await CreateEventAsync(title, wantsThread: true);
        var set = await Client.PutAsJsonAsync($"/events/{ev.Id}/thread", new SetThreadRequest(threadId));
        set.EnsureSuccessStatusCode();
        return (await set.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<List<ThreadMemberPayload>> ThreadDeliveriesAsync(Guid eventId, DeliveryType type)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString()))
            .ToListAsync();
        return [.. rows.Select(d => System.Text.Json.JsonSerializer.Deserialize<ThreadMemberPayload>(d.PayloadJson)!)];
    }

    private async Task<List<ArchiveThreadPayload>> ArchiveDeliveriesAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == DeliveryType.ArchiveThread && d.PayloadJson.Contains(eventId.ToString()))
            .ToListAsync();
        return [.. rows.Select(d => System.Text.Json.JsonSerializer.Deserialize<ArchiveThreadPayload>(d.PayloadJson)!)];
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }
}
