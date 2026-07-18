using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CalCrony.Api.Tests;

/// <summary>ApiFixture with fake Discord auth AND fake calendar provider, plus a helper that
/// drives the real login endpoint dance (start → callback → refresh) and returns a
/// bearer-equipped client for the logged-in user.</summary>
public sealed class WebAuthFixture : ApiFixture
{
    public const string WebOrigin = "https://web.test";

    public FakeDiscordAuthProvider Discord { get; private set; } = null!;

    public FakeCalendarProvider Calendar { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Jwt:SigningKey", "test-signing-key-that-is-long-enough!");
        builder.UseSetting("Auth:Discord:ClientId", "test-discord-client");
        builder.UseSetting("Api:PublicBaseUrl", "http://localhost:8080");
        builder.UseSetting("Web:Origin", WebOrigin);
        builder.UseSetting("Calendar:Google:ClientId", "test-client-id");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IDiscordAuthProvider>();
        Discord = new FakeDiscordAuthProvider();
        services.AddSingleton<IDiscordAuthProvider>(Discord);

        services.RemoveAll<ICalendarProvider>();
        Calendar = new FakeCalendarProvider();
        services.AddSingleton<ICalendarProvider>(Calendar);
    }

    /// <summary>Runs the full login dance for a configured fake user and returns an HttpClient
    /// authenticated with the resulting bearer token (no X-Api-Key header).</summary>
    public async Task<(HttpClient Client, WebSessionResponse Session)> LoginAsync(
        long userId, params (long GuildId, string Name, bool CanManage)[] guilds)
    {
        var code = $"code-{userId}-{Guid.NewGuid():N}";
        Discord.Logins[code] = (
            new DiscordUserInfo(userId, $"user{userId}", $"User {userId}", null),
            [.. guilds.Select(g => new DiscordGuildInfo(g.GuildId, g.Name, null, g.CanManage))]);

        // HandleCookies defaults to true — the refresh cookie round-trips like a real browser.
        var browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await browser.GetAsync("/auth/discord/start?returnUrl=/app");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var state = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"]!;

        var callback = await browser.GetAsync($"/auth/discord/callback?code={code}&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.StartsWith(WebOrigin, callback.Headers.Location!.ToString());

        var refresh = await browser.PostAsync("/auth/refresh", null);
        refresh.EnsureSuccessStatusCode();
        var session = (await refresh.Content.ReadFromJsonAsync<WebSessionResponse>())!;

        browser.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        return (browser, session);
    }
}
