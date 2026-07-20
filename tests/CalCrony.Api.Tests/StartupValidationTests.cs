using System.Net;
using CalCrony.Api;
using Microsoft.Extensions.Configuration;

namespace CalCrony.Api.Tests;

/// <summary>Boot-time config validation: unusable auth configuration must fail at startup with a
/// clear message, never limp to a runtime 500.</summary>
public class StartupValidationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public void Short_signing_key_fails_fast_with_a_generation_hint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupConfigValidation.Validate(Config(("Auth:Jwt:SigningKey", "too-short"))));
        Assert.Contains("at least 32", ex.Message);
        Assert.Contains("openssl rand", ex.Message);
    }

    [Fact]
    public void Web_login_without_a_signing_key_fails_fast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupConfigValidation.Validate(Config(("Auth:Discord:ClientId", "12345"))));
        Assert.Contains("Auth:Jwt:SigningKey", ex.Message);
    }

    [Fact]
    public void Bot_only_and_fully_configured_deployments_both_pass()
    {
        StartupConfigValidation.Validate(Config()); // both empty = web auth off, legal

        StartupConfigValidation.Validate(Config(
            ("Auth:Discord:ClientId", "12345"),
            ("Auth:Jwt:SigningKey", new string('k', 32))));
    }

    [Fact]
    public async Task Health_ready_reports_ok_when_the_database_is_reachable()
    {
        var response = await fixture.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", await response.Content.ReadAsStringAsync());
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();
}
