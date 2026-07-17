using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CalCrony.Api.Tests;

public class HealthAndAuthTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Database:AutoMigrate", "false"));

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
}
