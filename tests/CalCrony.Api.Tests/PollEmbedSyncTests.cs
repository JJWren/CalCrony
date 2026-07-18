using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class PollEmbedSyncTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9700;
    private const long ChannelId = 9701;

    [Fact]
    public async Task Web_vote_enqueues_one_coalesced_sync_bot_vote_none()
    {
        var poll = await CreatePollWithMessageAsync("Sync votes?");
        var (member, session) = await fixture.LoginAsync(9801, (GuildId, "G", false));

        await member.PutAsJsonAsync($"/polls/{poll.Id}/votes/{session.UserId}", new PutPollVotesRequest([poll.Options[0].Id]));
        Assert.Equal(1, await CountPendingSyncsAsync(poll.Id));

        await member.PutAsJsonAsync($"/polls/{poll.Id}/votes/{session.UserId}", new PutPollVotesRequest([poll.Options[1].Id]));
        Assert.Equal(1, await CountPendingSyncsAsync(poll.Id)); // coalesced

        await fixture.Client.PutAsJsonAsync($"/polls/{poll.Id}/votes/555", new PutPollVotesRequest([poll.Options[0].Id]));
        Assert.Equal(1, await CountPendingSyncsAsync(poll.Id)); // bot adds nothing
    }

    [Fact]
    public async Task Web_close_enqueues_sync_and_web_delete_captures_message_ids()
    {
        var poll = await CreatePollWithMessageAsync("Close and delete?");
        var (creator, _) = await fixture.LoginAsync(9802, (GuildId, "G", true));

        await creator.PostAsync($"/polls/{poll.Id}/close", null);
        Assert.Equal(1, await CountPendingSyncsAsync(poll.Id));

        await creator.DeleteAsync($"/polls/{poll.Id}");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var payload = System.Text.Json.JsonSerializer.Serialize(new DeletePollMessagePayload(ChannelId, 888000));
        Assert.True(await db.Deliveries.AnyAsync(d => d.Type == DeliveryType.DeletePollMessage && d.PayloadJson == payload));
        Assert.False(await db.Polls.AnyAsync(p => p.Id == poll.Id));
    }

    [Fact]
    public async Task Sweep_closes_due_polls_and_enqueues_one_sync()
    {
        var poll = await CreatePollWithMessageAsync("Deadline?");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(1));
            await db.Polls.Where(p => p.Id == poll.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClosesAt, past));
        }

        await SweepAsync();
        var afterFirst = await fixture.Client.GetFromJsonAsync<PollDto>($"/polls/{poll.Id}");
        Assert.Equal(PollStatus.Closed, afterFirst!.Status);
        Assert.Equal(1, await CountPendingSyncsAsync(poll.Id));

        await SweepAsync();
        Assert.Equal(1, await CountPendingSyncsAsync(poll.Id)); // idempotent
    }

    [Fact]
    public async Task Polls_without_deadline_never_auto_close()
    {
        var poll = await CreatePollWithMessageAsync("Forever open?");
        await SweepAsync();
        var after = await fixture.Client.GetFromJsonAsync<PollDto>($"/polls/{poll.Id}");
        Assert.Equal(PollStatus.Open, after!.Status);
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<CalCrony.Api.Services.DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }

    private async Task<PollDto> CreatePollWithMessageAsync(string question)
    {
        await fixture.Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        var create = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/polls",
            new CreatePollRequest(300, question, ChannelId, ["a", "b"]));
        create.EnsureSuccessStatusCode();
        var poll = (await create.Content.ReadFromJsonAsync<PollDto>())!;
        var set = await fixture.Client.PutAsJsonAsync($"/polls/{poll.Id}/message",
            new SetPollMessageRequest(ChannelId, 888000));
        set.EnsureSuccessStatusCode();
        return (await set.Content.ReadFromJsonAsync<PollDto>())!;
    }

    private async Task<int> CountPendingSyncsAsync(Guid pollId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var payload = System.Text.Json.JsonSerializer.Serialize(new SyncPollMessagePayload(pollId));
        return await db.Deliveries.CountAsync(d =>
            d.Type == DeliveryType.SyncPollMessage && d.Status == DeliveryStatus.Pending && d.PayloadJson == payload);
    }
}
