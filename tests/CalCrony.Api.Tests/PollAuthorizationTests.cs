using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

public class PollAuthorizationTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9500;
    private const long ChannelId = 9501;

    [Fact]
    public async Task Non_member_gets_404_on_poll_and_403_on_list()
    {
        await SeedGuildAsync();
        var poll = await BotCreatePollAsync("Hidden?");
        var (outsider, _) = await fixture.LoginAsync(9601 /* no guilds */);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/polls/{poll.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/guilds/{GuildId}/polls")).StatusCode);
    }

    [Fact]
    public async Task Votes_are_self_only_for_web_callers()
    {
        await SeedGuildAsync();
        var poll = await BotCreatePollAsync("Self votes?");
        var (member, session) = await fixture.LoginAsync(9602, (GuildId, "G", false));

        var self = await member.PutAsJsonAsync($"/polls/{poll.Id}/votes/{session.UserId}",
            new PutPollVotesRequest([poll.Options[0].Id]));
        self.EnsureSuccessStatusCode();

        var other = await member.PutAsJsonAsync($"/polls/{poll.Id}/votes/999999",
            new PutPollVotesRequest([poll.Options[0].Id]));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Close_convert_delete_require_creator_or_manager()
    {
        await SeedGuildAsync();
        var (creator, _) = await fixture.LoginAsync(9603, (GuildId, "G", false));
        var (member, _) = await fixture.LoginAsync(9604, (GuildId, "G", false));
        var (manager, _) = await fixture.LoginAsync(9605, (GuildId, "G", true));

        var create = await creator.PostAsJsonAsync($"/guilds/{GuildId}/polls",
            new CreatePollRequest(0, "Guarded?", 0, ["a", "b"]));
        create.EnsureSuccessStatusCode();
        var poll = (await create.Content.ReadFromJsonAsync<PollDto>())!;

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/polls/{poll.Id}/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/polls/{poll.Id}")).StatusCode);

        (await manager.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();
        (await creator.DeleteAsync($"/polls/{poll.Id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Add_option_gating_flag_for_members_owner_always_allowed()
    {
        await SeedGuildAsync();
        var (creator, _) = await fixture.LoginAsync(9606, (GuildId, "G", false));
        var (member, _) = await fixture.LoginAsync(9607, (GuildId, "G", false));

        var create = await creator.PostAsJsonAsync($"/guilds/{GuildId}/polls",
            new CreatePollRequest(0, "No adds?", 0, ["a", "b"], AllowUserOptions: false));
        var poll = (await create.Content.ReadFromJsonAsync<PollDto>())!;

        var memberAdd = await member.PostAsJsonAsync($"/polls/{poll.Id}/options", new AddPollOptionRequest(0, "c"));
        Assert.Equal(HttpStatusCode.Forbidden, memberAdd.StatusCode);

        var creatorAdd = await creator.PostAsJsonAsync($"/polls/{poll.Id}/options", new AddPollOptionRequest(0, "c"));
        Assert.Equal(HttpStatusCode.Created, creatorAdd.StatusCode);
    }

    [Fact]
    public async Task Anonymous_poll_dto_reveals_only_own_votes_to_web_callers()
    {
        await SeedGuildAsync();
        var poll = await BotCreatePollAsync("Secret ballot?", anonymous: true);
        var (alice, aliceSession) = await fixture.LoginAsync(9608, (GuildId, "G", false));
        var (bob, _) = await fixture.LoginAsync(9609, (GuildId, "G", false));

        await alice.PutAsJsonAsync($"/polls/{poll.Id}/votes/{aliceSession.UserId}", new PutPollVotesRequest([poll.Options[0].Id]));
        await bob.PutAsJsonAsync($"/polls/{poll.Id}/votes/9609", new PutPollVotesRequest([poll.Options[0].Id]));

        var seenByAlice = await alice.GetFromJsonAsync<PollDto>($"/polls/{poll.Id}");
        Assert.Equal(2, seenByAlice!.Options[0].VoteCount);                    // counts complete
        var vote = Assert.Single(seenByAlice.Votes);                          // only her own row
        Assert.Equal(aliceSession.UserId, vote.UserId);

        // The bot still sees everything (its embed builder does the hiding).
        var seenByBot = await fixture.Client.GetFromJsonAsync<PollDto>($"/polls/{poll.Id}");
        Assert.Equal(2, seenByBot!.Votes.Count);
    }

    private async Task SeedGuildAsync()
    {
        var response = await fixture.Client.PutAsJsonAsync($"/guilds/{GuildId}/settings",
            new GuildSettingsDto("UTC", ChannelId));
        response.EnsureSuccessStatusCode();
    }

    private async Task<PollDto> BotCreatePollAsync(string question, bool anonymous = false)
    {
        var response = await fixture.Client.PostAsJsonAsync($"/guilds/{GuildId}/polls",
            new CreatePollRequest(300, question, ChannelId, ["a", "b"], Anonymous: anonymous));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PollDto>())!;
    }
}
