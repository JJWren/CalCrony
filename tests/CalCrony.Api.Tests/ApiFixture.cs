using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CalCrony.Api.Tests;

/// <summary>Real Postgres in a container + the API under WebApplicationFactory, shared per test class.
/// Subclass and override <see cref="ConfigureTestServices"/> to substitute fakes for external-service
/// dependencies (e.g. <see cref="CalendarApiFixture"/>).</summary>
public class ApiFixture : IAsyncLifetime
{
    public const string ApiKeyValue = "test-api-key";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:CalCrony", container.GetConnectionString());
            b.UseSetting("Database:AutoMigrate", "true");
            b.UseSetting("Auth:BootstrapApiKey", ApiKeyValue);
            b.UseSetting("Scheduler:Enabled", "false");
            ConfigureWebHost(b);
            b.ConfigureTestServices(ConfigureTestServices);
        });

        Client = Factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Api-Key", ApiKeyValue);
    }

    protected virtual void ConfigureWebHost(IWebHostBuilder builder)
    {
    }

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await container.DisposeAsync();
    }
}
