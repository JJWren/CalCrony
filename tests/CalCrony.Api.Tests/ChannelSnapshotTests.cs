using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>Channel-name snapshots: the referenced-channel inventory the bot reconciles at Ready,
/// the bulk sync that may create rows, the rename route that never does, and the SetMessage
/// upsert from the embed post sites.</summary>
public class ChannelSnapshotTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 800;
    private const long CreatorId = 809;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Referenced_channels_cover_events_series_and_default_channels()
    {
        var oneOff = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "One Off", "in 6 hours", 801));
        oneOff.EnsureSuccessStatusCode();
        var recurring = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Recurring", "in 6 hours", 802, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        recurring.EnsureSuccessStatusCode();
        var settings = await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", 803));
        settings.EnsureSuccessStatusCode();

        var response = await Client.GetFromJsonAsync<ReferencedChannelsResponse>("/channels/referenced");

        Assert.Contains(new ReferencedChannelDto(GuildId, 801), response!.Channels);
        Assert.Contains(new ReferencedChannelDto(GuildId, 802), response.Channels);
        Assert.Contains(new ReferencedChannelDto(GuildId, 803), response.Channels);
    }

    [Fact]
    public async Task Sync_creates_rows_but_renames_update_existing_only()
    {
        var sync = await Client.PutAsJsonAsync("/channels/sync",
            new SyncChannelsRequest([new(811, GuildId, "alpha")]));
        Assert.Equal(HttpStatusCode.NoContent, sync.StatusCode);
        Assert.Equal("alpha", await NameAsync(811));

        // A rename of a tracked channel lands…
        var rename = await Client.PutAsJsonAsync("/channels/811/name", new ChannelNameRequest("beta"));
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        Assert.Equal("beta", await NameAsync(811));

        // …but a rename of a never-referenced channel must not grow the table.
        var untracked = await Client.PutAsJsonAsync("/channels/812/name", new ChannelNameRequest("ghost"));
        Assert.Equal(HttpStatusCode.NoContent, untracked.StatusCode);
        Assert.Null(await NameAsync(812));
    }

    [Fact]
    public async Task Set_message_upserts_the_channel_snapshot()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Posted", "in 6 hours", 821));
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        var posted = await Client.PutAsJsonAsync($"/events/{ev.Id}/message",
            new SetEventMessageRequest(821, 4242, "game-night"));
        posted.EnsureSuccessStatusCode();
        Assert.Equal("game-night", await NameAsync(821));

        // Re-posting into the (renamed) channel refreshes the snapshot.
        var reposted = await Client.PutAsJsonAsync($"/events/{ev.Id}/message",
            new SetEventMessageRequest(821, 4243, "movie-night"));
        reposted.EnsureSuccessStatusCode();
        Assert.Equal("movie-night", await NameAsync(821));
    }

    [Fact]
    public async Task Web_callers_cannot_touch_channel_snapshots()
    {
        var (client, _) = await fixture.LoginAsync(890, (GuildId, "Members Only", true));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/channels/referenced")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync("/channels/sync", new SyncChannelsRequest([new(831, GuildId, "x")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync("/channels/831/name", new ChannelNameRequest("x"))).StatusCode);
    }

    private async Task<string?> NameAsync(long channelId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return (await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId))?.Name;
    }
}
