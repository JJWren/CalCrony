using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

public class EventAvailabilityTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 7500;
    private const long ChannelId = 7501;

    [Fact]
    public async Task Grid_covers_going_members_only_with_event_window()
    {
        var ev = await CreateEventAsync("Availability Event");
        var going = ev.Options.Single(o => o.SortOrder == 0);
        var maybe = ev.Options.Single(o => o.SortOrder == 2);
        var (member, session) = await fixture.LoginAsync(7601, (GuildId, "Avail", false));

        // Member RSVPs Going; another user (via bot) is only Maybe — must not appear.
        await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going.Id));
        await fixture.Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/7699", new RsvpRequest(maybe.Id));

        var response = await member.GetFromJsonAsync<AvailabilityResponse>($"/events/{ev.Id}/availability");

        var row = Assert.Single(response!.Results);
        Assert.Equal(session.UserId, row.UserId);
        Assert.Equal(CalendarAvailabilityStatus.NotConnected, row.Status);
        Assert.Equal(ev.StartsAtUtc, response.StartsAtUtc);
        Assert.Equal(ev.StartsAtUtc.AddMinutes(60), response.EndsAtUtc);
    }

    [Fact]
    public async Task Non_member_gets_404()
    {
        var ev = await CreateEventAsync("Hidden Availability");
        var (outsider, _) = await fixture.LoginAsync(7602 /* no guilds */);

        var response = await outsider.GetAsync($"/events/{ev.Id}/availability");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Empty_going_list_returns_empty_results()
    {
        var ev = await CreateEventAsync("Lonely Event");
        var (member, _) = await fixture.LoginAsync(7603, (GuildId, "Avail", false));

        var response = await member.GetFromJsonAsync<AvailabilityResponse>($"/events/{ev.Id}/availability");

        Assert.Empty(response!.Results);
    }

    private async Task<EventDto> CreateEventAsync(string title)
    {
        await fixture.Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", null));
        var response = await fixture.Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events", new CreateEventRequest(300, title, "in 3 hours", ChannelId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }
}
