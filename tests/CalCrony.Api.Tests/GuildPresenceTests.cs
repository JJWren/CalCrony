using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>Bot-reported guild presence: joins surface the guild in the web app, leaves hide it
/// (keeping settings for a re-invite), and the Ready-time sync reconciles both directions.</summary>
public class GuildPresenceTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    [Fact]
    public async Task Invited_guild_appears_once_the_bot_reports_presence()
    {
        var (client, _) = await fixture.LoginAsync(7501, (7101, "Fresh Invite", true));

        // No Guilds row yet — the server is invisible even though the user manages it.
        var before = await client.GetFromJsonAsync<WebGuildListResponse>("/me/guilds");
        Assert.Empty(before!.Guilds);

        // The bot's JoinedGuild handler reports presence...
        var report = await fixture.Client.PutAsJsonAsync(
            "/guilds/7101/presence", new GuildPresenceRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);

        // ...and the server shows up without re-login or any command having run.
        var after = await client.GetFromJsonAsync<WebGuildListResponse>("/me/guilds");
        var guild = Assert.Single(after!.Guilds);
        Assert.Equal(7101, guild.Id);
    }

    [Fact]
    public async Task Kicked_guild_disappears_but_settings_survive_a_reinvite()
    {
        var seed = await fixture.Client.PutAsJsonAsync(
            "/guilds/7102/settings", new GuildSettingsDto("America/Chicago", null));
        seed.EnsureSuccessStatusCode();
        var (client, _) = await fixture.LoginAsync(7502, (7102, "Kicked", true));
        Assert.Single((await client.GetFromJsonAsync<WebGuildListResponse>("/me/guilds"))!.Guilds);

        // Bot leaves: the guild vanishes from the list and guild-scoped access is refused.
        var leave = await fixture.Client.PutAsJsonAsync(
            "/guilds/7102/presence", new GuildPresenceRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<WebGuildListResponse>("/me/guilds"))!.Guilds);
        var denied = await client.GetAsync("/guilds/7102/settings");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Re-invite: presence returns and the old settings are intact.
        var rejoin = await fixture.Client.PutAsJsonAsync(
            "/guilds/7102/presence", new GuildPresenceRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, rejoin.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<WebGuildListResponse>("/me/guilds"))!.Guilds);
        var settings = await client.GetFromJsonAsync<GuildSettingsDto>("/guilds/7102/settings");
        Assert.Equal("America/Chicago", settings!.TimeZone);
    }

    [Fact]
    public async Task Ready_sync_creates_missing_guilds_and_marks_departed_ones_absent()
    {
        var seed = await fixture.Client.PutAsJsonAsync(
            "/guilds/7103/settings", new GuildSettingsDto("UTC", null));
        seed.EnsureSuccessStatusCode();

        // The bot reports it is only in 7104 now: 7103 becomes absent, 7104 is created present.
        var sync = await fixture.Client.PutAsJsonAsync(
            "/guilds/presence/sync", new SyncGuildPresenceRequest([new(7104, "Joined Offline")]));
        sync.EnsureSuccessStatusCode();
        var counts = await sync.Content.ReadFromJsonAsync<SyncGuildPresenceResponse>();
        Assert.Equal(1, counts!.Present);

        var (client, _) = await fixture.LoginAsync(7503, (7103, "Departed", true), (7104, "Joined Offline", true));
        var guilds = await client.GetFromJsonAsync<WebGuildListResponse>("/me/guilds");
        var guild = Assert.Single(guilds!.Guilds);
        Assert.Equal(7104, guild.Id);
    }

    [Fact]
    public async Task Presence_reports_maintain_the_guild_name_snapshot()
    {
        var join = await fixture.Client.PutAsJsonAsync(
            "/guilds/7106/presence", new GuildPresenceRequest(true, "Original"));
        Assert.Equal(HttpStatusCode.NoContent, join.StatusCode);
        Assert.Equal("Original", await NameAsync(7106));

        // Leaves send no name — the last-known snapshot survives for historical rendering.
        var leave = await fixture.Client.PutAsJsonAsync(
            "/guilds/7106/presence", new GuildPresenceRequest(false));
        leave.EnsureSuccessStatusCode();
        Assert.Equal("Original", await NameAsync(7106));

        // The Ready-time sync refreshes names for listed guilds (renames missed while offline).
        var sync = await fixture.Client.PutAsJsonAsync(
            "/guilds/presence/sync", new SyncGuildPresenceRequest([new(7106, "Renamed")]));
        sync.EnsureSuccessStatusCode();
        Assert.Equal("Renamed", await NameAsync(7106));
    }

    private async Task<string?> NameAsync(long guildId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return (await db.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guildId))?.Name;
    }

    [Fact]
    public async Task Sync_without_guild_ids_is_a_bad_request()
    {
        var response = await fixture.Client.PutAsJsonAsync("/guilds/presence/sync", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Web_callers_cannot_report_presence()
    {
        var (client, _) = await fixture.LoginAsync(7504, (7105, "No Spoofing", true));

        var put = await client.PutAsJsonAsync("/guilds/7105/presence", new GuildPresenceRequest(true));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var sync = await client.PutAsJsonAsync("/guilds/presence/sync", new SyncGuildPresenceRequest([new(7105)]));
        Assert.Equal(HttpStatusCode.Forbidden, sync.StatusCode);
    }
}
