using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

public class EventApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 100;
    private const long ChannelId = 200;
    private const long CreatorId = 300;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Timezone_list_returns_canonical_zones_with_offsets()
    {
        var zones = (await Client.GetFromJsonAsync<List<TimeZoneOptionDto>>("/tools/timezones"))!;

        var chicago = Assert.Single(zones, z => z.Id == "America/Chicago");
        Assert.Matches(@"^America/Chicago — UTC-0[56]:00$", chicago.Label); // CDT or CST
        Assert.Contains(zones, z => z.Id == "UTC");
        Assert.DoesNotContain(zones, z => z.Id.StartsWith("Etc/"));
        Assert.DoesNotContain(zones, z => z.Id == "US/Central"); // alias, not canonical
        Assert.True(zones.Count > 300);
    }

    [Fact]
    public async Task Create_get_list_delete_roundtrip()
    {
        var created = await CreateEventAsync("Raid Night", "in 3 hours");
        Assert.Equal("Raid Night", created.Title);
        Assert.Equal(3, created.Options.Count);
        Assert.True(created.StartsAtUtc > DateTimeOffset.UtcNow);

        var got = await ReadAsync<EventDto>(await Client.GetAsync($"/events/{created.Id}"));
        Assert.Equal(created.Id, got.Id);

        var list = await ReadAsync<List<EventDto>>(await Client.GetAsync($"/guilds/{GuildId}/events"));
        Assert.Contains(list, e => e.Id == created.Id);

        var delete = await Client.DeleteAsync($"/events/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/events/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Unparseable_datetime_is_rejected()
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Bad", "flurble wumpus", ChannelId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Rsvp_set_switch_and_clear()
    {
        var ev = await CreateEventAsync("Movie Night", "in 4 hours");
        var going = ev.Options.Single(o => o.SortOrder == 0);
        var maybe = ev.Options.Single(o => o.SortOrder == 2);
        const long userId = 42;

        var afterPut = await ReadAsync<EventDto>(
            await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(going.Id)));
        Assert.Single(afterPut.Rsvps);
        Assert.Equal(going.Id, afterPut.Rsvps[0].OptionId);

        var afterSwitch = await ReadAsync<EventDto>(
            await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{userId}", new RsvpRequest(maybe.Id)));
        Assert.Single(afterSwitch.Rsvps);
        Assert.Equal(maybe.Id, afterSwitch.Rsvps[0].OptionId);

        var afterDelete = await ReadAsync<EventDto>(
            await Client.DeleteAsync($"/events/{ev.Id}/rsvps/{userId}"));
        Assert.Empty(afterDelete.Rsvps);
    }

    [Fact]
    public async Task Update_reparses_time_and_changes_fields()
    {
        var ev = await CreateEventAsync("Old Title", "in 5 hours");

        var updated = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, Title: "New Title", WhenText: "in 6 hours")));

        Assert.Equal("New Title", updated.Title);
        Assert.True(updated.StartsAtUtc > ev.StartsAtUtc);
    }

    [Fact]
    public async Task Guild_timezone_settings_validate_iana_ids()
    {
        var bad = await Client.PutAsJsonAsync(
            $"/guilds/{GuildId}/settings", new GuildSettingsDto("Not/AZone", null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await ReadAsync<GuildSettingsDto>(await Client.PutAsJsonAsync(
            $"/guilds/{GuildId}/settings", new GuildSettingsDto("America/Chicago", ChannelId)));
        Assert.Equal("America/Chicago", good.TimeZone);
    }

    private async Task<EventDto> CreateEventAsync(string title, string when)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, when, ChannelId, Description: "test event"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
