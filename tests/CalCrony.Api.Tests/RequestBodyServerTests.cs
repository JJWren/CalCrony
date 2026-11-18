using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CalCrony.Api.Tests;

/// <summary>The size limit of a request body on the real server, where it applies to a body that
/// does not state its length as well: from a web caller such a body is answered 413 once it runs
/// past 1 MB, and from the bot it is read.</summary>
public class RequestBodyServerTests(RequestBodyServerTests.ServerFixture fixture)
    : IClassFixture<RequestBodyServerTests.ServerFixture>
{
    private const int Limit = 1_048_576;

    // Each test works with a user of its own.
    private static long nextUser = 9_820_000;

    [Fact]
    public async Task A_web_callers_body_that_states_no_length_and_runs_past_1_MB_is_answered_413()
    {
        var userId = Interlocked.Increment(ref nextUser);
        await PutZoneAsync(userId, "America/Chicago");
        var body = RequestBodyApiTests.SettingsBody("Europe/Paris", Limit + 1);

        // The body goes out as one chunk, and the request is left open after it: the server has to
        // answer from what it has read.
        var head =
            $"PUT /users/{userId}/settings HTTP/1.1\r\n" +
            $"Host: {fixture.Address.Authority}\r\n" +
            $"Authorization: Bearer {fixture.WebToken(userId)}\r\n" +
            "Content-Type: application/json\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n" +
            $"{body.Length:X}\r\n";
        var statusLine = await SendRawAsync(Encoding.ASCII.GetBytes(head), body);

        Assert.StartsWith("HTTP/1.1 413", statusLine);
        Assert.Equal("America/Chicago", await ZoneAsync(userId));
    }

    [Fact]
    public async Task A_web_callers_body_that_states_no_length_and_stays_under_1_MB_is_read()
    {
        var userId = Interlocked.Increment(ref nextUser);
        using var client = fixture.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.WebToken(userId));

        // The server counts such a body as it is sent, the framing of its chunks included, so this
        // one stays clear of the limit.
        using var response = await client.PutAsync(
            $"/users/{userId}/settings", new UnsizedContent(RequestBodyApiTests.SettingsBody("Europe/Paris", 1_000_000)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Europe/Paris", await ZoneAsync(userId));
    }

    [Fact]
    public async Task A_web_callers_body_that_states_a_length_of_more_than_1_MB_is_refused()
    {
        var userId = Interlocked.Increment(ref nextUser);
        await PutZoneAsync(userId, "America/Chicago");
        using var client = fixture.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.WebToken(userId));
        using var content = new ByteArrayContent(RequestBodyApiTests.SettingsBody("Europe/Paris", Limit + 1));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PutAsync($"/users/{userId}/settings", content);

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"{(int)response.StatusCode}: {text}");
        Assert.Equal(
            "The request body must be at most 1 MB.",
            JsonSerializer.Deserialize<ErrorResponse>(text, JsonSerializerOptions.Web)!.Error);
        Assert.Equal("America/Chicago", await ZoneAsync(userId));
    }

    [Fact]
    public async Task A_web_callers_body_over_1_MB_that_states_no_length_and_is_not_json_is_answered_as_before()
    {
        var userId = Interlocked.Increment(ref nextUser);
        await PutZoneAsync(userId, "America/Chicago");
        using var client = fixture.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.WebToken(userId));

        using var response = await client.PutAsync(
            $"/users/{userId}/settings",
            new UnsizedContent(RequestBodyApiTests.SettingsBody("Europe/Paris", 2 * Limit), contentType: null));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("America/Chicago", await ZoneAsync(userId));
    }

    [Fact]
    public async Task The_bots_body_that_states_no_length_and_runs_past_1_MB_is_read()
    {
        var userId = Interlocked.Increment(ref nextUser);
        using var client = fixture.NewClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFixture.ApiKeyValue);

        using var response = await client.PutAsync(
            $"/users/{userId}/settings", new UnsizedContent(RequestBodyApiTests.SettingsBody("Europe/Paris", 2 * Limit)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Europe/Paris", await ZoneAsync(userId));
    }

    /// <summary>Writes the request over a connection of its own and returns the status line of the
    /// answer, or an empty string when none comes within 30 seconds.</summary>
    private async Task<string> SendRawAsync(byte[] head, byte[] body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(fixture.Address.Host, fixture.Address.Port, timeout.Token);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(head, timeout.Token);
        await stream.WriteAsync(body, timeout.Token);

        var received = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (!Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }

        var text = Encoding.ASCII.GetString(received.ToArray());
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private async Task PutZoneAsync(long userId, string zone)
    {
        using var client = fixture.NewClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFixture.ApiKeyValue);
        (await client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(zone, false))).EnsureSuccessStatusCode();
    }

    private async Task<string?> ZoneAsync(long userId)
    {
        using var client = fixture.NewClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFixture.ApiKeyValue);
        return (await client.GetFromJsonAsync<UserSettingsDto>($"/users/{userId}/settings"))!.TimeZone;
    }

    /// <summary>A body sent without a stated length: JSON, unless another content type is given.</summary>
    private sealed class UnsizedContent : HttpContent
    {
        private readonly byte[] body;

        public UnsizedContent(byte[] body, string? contentType = "application/json")
        {
            this.body = body;
            if (contentType is not null)
            {
                Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>The API on the real server, over a database of its own.</summary>
    public sealed class ServerFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

        private WebApplicationFactory<Program> factory = null!;

        public Uri Address { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await container.StartAsync();

            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseUrls("http://127.0.0.1:0");
                b.UseSetting("ConnectionStrings:CalCrony", container.GetConnectionString());
                b.UseSetting("Database:AutoMigrate", "true");
                b.UseSetting("Auth:BootstrapApiKey", ApiFixture.ApiKeyValue);
                b.UseSetting("Auth:Jwt:SigningKey", "test-signing-key-that-is-long-enough!");
                b.UseSetting("Scheduler:Enabled", "false");
                b.UseSetting("Web:Origin", "https://web.test");
            });
            factory.UseKestrel();
            factory.StartServer();
            Address = new Uri(factory.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First());
        }

        /// <summary>A client of the real server, with no credentials.</summary>
        /// <returns>The client.</returns>
        public HttpClient NewClient() => new() { BaseAddress = Address };

        /// <summary>A web sign-in token for the user, as the sign-in route issues it.</summary>
        /// <param name="userId">The Discord user id.</param>
        /// <returns>The access token.</returns>
        public string WebToken(long userId)
        {
            using var scope = factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<WebTokenService>()
                .IssueAccessToken(userId, $"user{userId}", null).Value;
        }

        public async Task DisposeAsync()
        {
            await factory.DisposeAsync();
            await container.DisposeAsync();
        }
    }
}
