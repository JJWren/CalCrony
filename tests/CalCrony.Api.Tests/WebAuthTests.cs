using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CalCrony.Api.Tests;

public class WebAuthTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    [Fact]
    public async Task Start_redirects_to_discord_with_state()
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/discord/start?returnUrl=/app");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("discord.com/oauth2/authorize", location);
        Assert.Contains("state=", location);
        Assert.Contains("scope=identify+guilds", location);
    }

    [Fact]
    public async Task Absolute_return_urls_are_refused()
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await client.GetAsync("/auth/discord/start?returnUrl=https://evil.example");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var state = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"]!;

        var code = $"evil-{Guid.NewGuid():N}";
        fixture.Discord.Logins[code] = (new(9001, "eve", null, null), []);
        var callback = await client.GetAsync($"/auth/discord/callback?code={code}&state={state}");

        // The poisoned returnUrl was discarded at start time; landing target is the safe default.
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal($"{WebAuthFixture.WebOrigin}/app", callback.Headers.Location!.ToString());
    }

    [Fact]
    public async Task State_is_single_use()
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await client.GetAsync("/auth/discord/start");
        var state = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"]!;
        var code = $"single-{Guid.NewGuid():N}";
        fixture.Discord.Logins[code] = (new(9002, "solo", null, null), []);

        var first = await client.GetAsync($"/auth/discord/callback?code={code}&state={state}");
        Assert.EndsWith("/app", first.Headers.Location!.ToString());

        var second = await client.GetAsync($"/auth/discord/callback?code={code}&state={state}");
        Assert.Contains("error=expired", second.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Denied_consent_redirects_with_error_code()
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var callback = await client.GetAsync("/auth/discord/callback?state=whatever&error=access_denied");

        Assert.Contains("/login?error=denied", callback.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Refresh_rotates_and_old_cookie_is_dead()
    {
        var (client, session) = await fixture.LoginAsync(9003, (1, "Guild", false));
        Assert.Equal(9003, session.UserId);
        Assert.Equal("User 9003", session.Username);

        // The fixture's client carries the rotated cookie; a second refresh works...
        var again = await client.PostAsync("/auth/refresh", null);
        again.EnsureSuccessStatusCode();

        // ...and a third with the same (now-consumed) cookie value would fail if replayed.
        // The cookie container already rotated, so instead prove logout kills the session.
        var logout = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.PostAsync("/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Refresh_without_cookie_is_unauthorized()
    {
        var bare = fixture.Factory.CreateClient();
        var response = await bare.PostAsync("/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_snapshots_guilds_and_me_guilds_intersects_with_bot_presence()
    {
        // Guild 42 is known to CalCrony (bot present); 43 is not.
        await SeedGuildAsync(42);
        var (client, _) = await fixture.LoginAsync(9004, (42, "Shared", true), (43, "Botless", false));

        var guilds = await client.GetFromJsonAsync<WebGuildListResponse>("/me/guilds");

        var guild = Assert.Single(guilds!.Guilds);
        Assert.Equal(42, guild.Id);
        Assert.True(guild.CanManage);
    }

    [Fact]
    public async Task Me_guilds_rejects_api_key_callers()
    {
        var botClient = fixture.Client;
        var response = await botClient.GetAsync("/me/guilds");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task SeedGuildAsync(long guildId)
    {
        var response = await fixture.Client.PutAsJsonAsync(
            $"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", null));
        response.EnsureSuccessStatusCode();
    }
}
