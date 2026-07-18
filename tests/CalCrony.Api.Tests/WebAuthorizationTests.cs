using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>The Phase A enforcement matrix: what JWT web callers can and cannot do.</summary>
public class WebAuthorizationTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 5000;
    private const long OtherGuildId = 5001;
    private const long ChannelId = 5002;

    [Fact]
    public async Task Member_can_list_events_non_member_cannot()
    {
        await SeedGuildAsync(GuildId);
        await SeedGuildAsync(OtherGuildId);
        var (member, _) = await fixture.LoginAsync(6001, (GuildId, "Mine", false));

        var allowed = await member.GetAsync($"/guilds/{GuildId}/events");
        allowed.EnsureSuccessStatusCode();

        var denied = await member.GetAsync($"/guilds/{OtherGuildId}/events");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Event_reads_return_404_for_non_members()
    {
        await SeedGuildAsync(GuildId);
        var ev = await CreateEventAsync(GuildId, "Secret Meetup");
        var (outsider, _) = await fixture.LoginAsync(6002 /* no guilds */);

        var response = await outsider.GetAsync($"/events/{ev.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Member_can_rsvp_self_but_not_others()
    {
        await SeedGuildAsync(GuildId);
        var ev = await CreateEventAsync(GuildId, "RSVP Rules");
        var going = ev.Options.Single(o => o.SortOrder == 0);
        var (member, session) = await fixture.LoginAsync(6003, (GuildId, "Mine", false));

        var self = await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going.Id));
        self.EnsureSuccessStatusCode();

        var other = await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/999999", new RsvpRequest(going.Id));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Theory]
    [InlineData("PATCH", "/events/{eventId}")]
    [InlineData("DELETE", "/events/{eventId}")]
    [InlineData("POST", "/events/{eventId}/notifications")]
    public async Task Event_mutations_require_creator_or_manager(string method, string pathTemplate)
    {
        // Phase B: a plain member who is neither creator nor manager is refused.
        // (Creator/manager success paths are covered in WebMutationTests.)
        await SeedGuildAsync(GuildId);
        var ev = await CreateEventAsync(GuildId, $"Guarded {method} {pathTemplate.GetHashCode()}");
        var (member, _) = await fixture.LoginAsync(6004, (GuildId, "Mine", false));
        var path = pathTemplate.Replace("{eventId}", ev.Id.ToString());

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PATCH" or "POST")
        {
            request.Content = JsonContent.Create(new { minutesBefore = 30 });
        }

        var response = await member.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/deliveries/pending")]
    public async Task Outbox_is_bot_only(string path)
    {
        var (member, _) = await fixture.LoginAsync(6005, (GuildId, "Mine", true));
        var response = await member.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Generic_availability_is_bot_only()
    {
        var (member, _) = await fixture.LoginAsync(6006, (GuildId, "Mine", false));

        var response = await member.PostAsJsonAsync("/calendar/availability",
            new AvailabilityRequest([123], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Settings_are_self_only_for_web_callers()
    {
        var (member, session) = await fixture.LoginAsync(6007, (GuildId, "Mine", false));

        var self = await member.GetAsync($"/users/{session.UserId}/settings");
        self.EnsureSuccessStatusCode();

        var other = await member.GetAsync("/users/424242/settings");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        // Phase B: writing your OWN settings is allowed; another user's is not.
        var selfWrite = await member.PutAsJsonAsync($"/users/{session.UserId}/settings", new UserSettingsDto("UTC", true));
        selfWrite.EnsureSuccessStatusCode();
        var otherWrite = await member.PutAsJsonAsync("/users/424242/settings", new UserSettingsDto("UTC", true));
        Assert.Equal(HttpStatusCode.Forbidden, otherWrite.StatusCode);
    }

    [Fact]
    public async Task Calendar_connections_are_self_only_for_web_callers()
    {
        var (member, session) = await fixture.LoginAsync(6008, (GuildId, "Mine", false));

        var self = await member.GetAsync($"/calendar/connections/{session.UserId}");
        self.EnsureSuccessStatusCode();

        var other = await member.GetAsync("/calendar/connections/424242");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Member_can_mint_feed_token()
    {
        await SeedGuildAsync(GuildId);
        var (member, _) = await fixture.LoginAsync(6009, (GuildId, "Mine", false));

        var response = await member.PostAsync($"/guilds/{GuildId}/feed-token", null);

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<FeedTokenDto>();
        Assert.NotNull(dto!.Token);
    }

    private async Task SeedGuildAsync(long guildId)
    {
        var response = await fixture.Client.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", null));
        response.EnsureSuccessStatusCode();
    }

    private async Task<EventDto> CreateEventAsync(long guildId, string title)
    {
        var response = await fixture.Client.PostAsJsonAsync(
            $"/guilds/{guildId}/events",
            new CreateEventRequest(300, title, "in 3 hours", ChannelId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }
}
