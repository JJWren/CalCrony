using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace CalCrony.Api.Tests;

/// <summary>Real Postgres in a container + the API under WebApplicationFactory, shared per test class.</summary>
public sealed class ApiFixture : IAsyncLifetime
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
        });

        Client = Factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Api-Key", ApiKeyValue);
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await container.DisposeAsync();
    }
}
