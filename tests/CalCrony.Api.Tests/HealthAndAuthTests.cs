using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CalCrony.Api.Tests;

public class HealthAndAuthTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:AutoMigrate", "false");
                b.UseSetting("Scheduler:Enabled", "false");
            });

    [Fact]
    public async Task Health_endpoint_is_anonymous_and_returns_ok()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Requests_without_api_key_are_rejected()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/guilds/1/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/oauth/google/start?token=x")]
    [InlineData("/oauth/google/callback?state=x&error=access_denied")]
    public async Task Oauth_routes_are_reachable_without_an_api_key(string path)
    {
        // These routes have no real DB behind them in this factory, so the handler itself may fail —
        // the only thing under test is that ApiKeyMiddleware's "/oauth" allowlist let the request
        // past without a 401, which happens before any DB access.
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/calendar/connections/1")]
    [InlineData("/calendar/connections/1/link-token")]
    [InlineData("/calendar/availability")]
    public async Task Calendar_routes_still_require_an_api_key(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
