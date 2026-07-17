using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

public class FeedTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 700;
    private const long ChannelId = 701;
    private const long CreatorId = 702;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Feed_token_is_stable_and_serves_ics_anonymously()
    {
        var create = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Feed Party", "in 6 hours", ChannelId, Description: "bring dip", Location: "The couch"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var first = await ReadTokenAsync();
        var second = await ReadTokenAsync();
        Assert.Equal(first.Token, second.Token);

        // The feed itself requires no API key.
        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(first.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/calendar", response.Content.Headers.ContentType!.MediaType);
        var ics = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", ics);
        Assert.Contains("SUMMARY:Feed Party", ics);
        Assert.Contains("LOCATION:The couch", ics);
        Assert.Contains("END:VCALENDAR", ics);
    }

    [Fact]
    public async Task Unknown_feed_token_is_not_found()
    {
        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync("/feeds/deadbeefdeadbeefdeadbeefdeadbeefdeadbeef.ics");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<FeedTokenDto> ReadTokenAsync()
    {
        var response = await Client.PostAsync($"/guilds/{GuildId}/feed-token", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedTokenDto>())!;
    }
}
