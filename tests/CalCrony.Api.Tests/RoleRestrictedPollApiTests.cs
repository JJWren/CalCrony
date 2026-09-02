using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Services;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>RSVP v2 §3.5 on polls: a poll-level restriction gates voting and voter-added options
/// for web callers from the role snapshot, with the same bypass, fail-closed, and bot-only
/// configuration rules as events. Clearing votes never needs the role.</summary>
public class RoleRestrictedPollApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 12300;
    private const long ChannelId = 12301;
    private const long CreatorId = 12302;
    private const long RaiderRole = 994300;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Votes_and_added_options_need_the_role_on_the_web_and_withdrawing_never_does()
    {
        var poll = await CreateAsync("Raid night?", [RaiderRole], allowUserOptions: true);
        var (alice, aliceSession) = await fixture.LoginAsync(12311, (GuildId, "G", false));
        var (bob, bobSession) = await fixture.LoginAsync(12312, (GuildId, "G", false));
        await SyncAsync(GuildId, [(RaiderRole, "Raider")], [(aliceSession.UserId, [RaiderRole])]);

        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{aliceSession.UserId}", new PutPollVotesRequest([poll.Options[0].Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync(
            $"/polls/{poll.Id}/options", new AddPollOptionRequest(0, "c"))).StatusCode);

        var vote = await bob.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{bobSession.UserId}", new PutPollVotesRequest([poll.Options[0].Id]));
        Assert.Equal(HttpStatusCode.Forbidden, vote.StatusCode);
        Assert.Equal("This poll is limited to @Raider.", (await Error(vote)).Error);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync(
            $"/polls/{poll.Id}/options", new AddPollOptionRequest(0, "d"))).StatusCode);

        // Withdrawing is not entry — nor is removing one choice of several, which is how a
        // toggle-button client gets a roleless member down to nothing. Bob holds a, b (given via
        // the trusted bot path): dropping b passes, adding a fresh choice is refused, clearing passes.
        (await Client.PutAsJsonAsync($"/polls/{poll.Id}/votes/{bobSession.UserId}",
            new PutPollVotesRequest([poll.Options[0].Id, poll.Options[1].Id]))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{bobSession.UserId}", new PutPollVotesRequest([poll.Options[0].Id]))).StatusCode);
        var added = await alice.GetFromJsonAsync<PollDto>($"/polls/{poll.Id}");
        var fresh = added!.Options.Single(o => o.Text == "c").Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{bobSession.UserId}", new PutPollVotesRequest([poll.Options[0].Id, fresh]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{bobSession.UserId}", new PutPollVotesRequest([]))).StatusCode);

        // A replacement decided from a stale set is refused rather than committed blind — the
        // entry-only rule is only sound against the set actually being replaced.
        (await Client.PutAsJsonAsync($"/polls/{poll.Id}/votes/{bobSession.UserId}",
            new PutPollVotesRequest([poll.Options[0].Id, poll.Options[1].Id]))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{bobSession.UserId}",
            new PutPollVotesRequest([poll.Options[0].Id], ExpectedOptionIds: [poll.Options[0].Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{bobSession.UserId}",
            new PutPollVotesRequest([poll.Options[0].Id], ExpectedOptionIds: [poll.Options[0].Id, poll.Options[1].Id]))).StatusCode);

        // The single-poll DTO names the role; the mirror is poll-level, not per option.
        var named = await alice.GetFromJsonAsync<PollDto>($"/polls/{poll.Id}");
        Assert.True(named!.IsRestricted);
        Assert.Equal("Raider", Assert.Single(named.AllowedRoles!).Name);
    }

    [Fact]
    public async Task An_unsynced_guild_refuses_the_web_vote_but_trusts_the_bot()
    {
        const long guild = 12320;
        var poll = await CreateAsync("Unsynced?", [RaiderRole], guildId: guild);
        var (carol, session) = await fixture.LoginAsync(12321, (guild, "G", false));

        var web = await carol.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{session.UserId}", new PutPollVotesRequest([poll.Options[0].Id]));
        Assert.Equal(HttpStatusCode.Conflict, web.StatusCode);
        Assert.Equal("We can't confirm your roles right now — vote from Discord.", (await Error(web)).Error);

        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{session.UserId}", new PutPollVotesRequest([poll.Options[0].Id]))).StatusCode);
    }

    [Fact]
    public async Task The_creator_and_server_managers_bypass_the_restriction()
    {
        var (dave, daveSession) = await fixture.LoginAsync(12331, (GuildId, "G", false));
        var (erin, erinSession) = await fixture.LoginAsync(12332, (GuildId, "G", true));
        var poll = await CreateAsync("Bypass?", [RaiderRole], creatorId: daveSession.UserId);
        await SyncAsync(GuildId, [(RaiderRole, "Raider")], []);

        Assert.Equal(HttpStatusCode.OK, (await dave.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{daveSession.UserId}", new PutPollVotesRequest([poll.Options[0].Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await erin.PutAsJsonAsync(
            $"/polls/{poll.Id}/votes/{erinSession.UserId}", new PutPollVotesRequest([poll.Options[1].Id]))).StatusCode);
    }

    [Fact]
    public async Task A_web_create_cannot_restrict_and_a_bot_create_is_capped()
    {
        (await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId)))
            .EnsureSuccessStatusCode();
        var (frank, _) = await fixture.LoginAsync(12341, (GuildId, "G", false));

        var web = await frank.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            0, "Web poll?", 0, ["a", "b"], AllowedRoleIds: [RaiderRole]));
        Assert.Equal(HttpStatusCode.Created, web.StatusCode);
        Assert.False((await web.Content.ReadFromJsonAsync<PollDto>())!.IsRestricted);

        var wide = await Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            CreatorId, "Wide?", ChannelId, ["a", "b"], AllowedRoleIds: [1, 2, 3, 4, 5, 6]));
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        Assert.Contains($"at most {RsvpPolicy.MaxAllowedRoles}", (await Error(wide)).Error);
    }

    private async Task<PollDto> CreateAsync(
        string question, long[] allowedRoleIds, long guildId = GuildId, long creatorId = CreatorId,
        bool allowUserOptions = false)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/polls", new CreatePollRequest(
            creatorId, question, ChannelId, ["a", "b"], AllowUserOptions: allowUserOptions,
            AllowedRoleIds: allowedRoleIds));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PollDto>())!;
    }

    private async Task SyncAsync(long guildId, (long RoleId, string? Name)[] roles, (long UserId, long[] RoleIds)[] members)
    {
        var response = await Client.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest(
            [.. roles.Select(r => new RoleNameDto(r.RoleId, r.Name))],
            [.. members.Select(m => new MemberRolesDto(m.UserId, m.RoleIds))]));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ErrorResponse> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
}
