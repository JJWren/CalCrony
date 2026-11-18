using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>Two rules for a request to a route that reads a JSON body: a body in a character set
/// that can't be read is refused with a 415 and a sentence, and a body of more than 1 MB from any
/// caller but the bot is refused with a 413 and a sentence when it states its length. Every body
/// that is read today is read as before. A body that does not state its length is covered by
/// <see cref="RequestBodyServerTests"/>, on the real server.</summary>
public class RequestBodyApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const string CharsetRefusal = "The request body is in a character set that can't be read — send it as UTF-8.";

    private const string SizeRefusal = "The request body must be at most 1 MB.";

    private const int Limit = 1_048_576;

    /// <summary>JSON content types whose character set can't be read.</summary>
    private static readonly string[] UnreadableContentTypes =
    [
        "application/json; charset=x-no-such",
        "application/json; charset=utf-7",
        "application/json; charset=utf8",
        "application/json; charset=windows-1252",
        "application/json; charset=\"utf-8\"",
        "application/json; charset=\"utf-16\"",
        "application/json; charset=",
        "application/json; charset=\"\"",
        "APPLICATION/JSON; CHARSET=X-NO-SUCH",
        "application/problem+json; charset=x-no-such",
        "application/merge-patch+json; charset=utf-7",
    ];

    /// <summary>JSON content types whose character set is read, each with the encoding of the body
    /// sent under it.</summary>
    private static readonly (string ContentType, Encoding Encoding)[] ReadableContentTypes =
    [
        ("application/json", Encoding.UTF8),
        ("application/json; charset=utf-8", Encoding.UTF8),
        ("application/json; charset=UTF-8", Encoding.UTF8),
        ("application/json;charset=utf-8", Encoding.UTF8),
        ("application/json; charset=utf-16", Encoding.Unicode),
        ("application/json; charset=utf-16le", Encoding.Unicode),
        ("application/json; charset=utf-16be", Encoding.BigEndianUnicode),
        ("application/json; charset=unicode", Encoding.Unicode),
        ("application/json; charset=utf-32", Encoding.UTF32),
        ("application/json; charset=us-ascii", Encoding.ASCII),
        ("application/json; charset=iso-8859-1", Encoding.Latin1),
        ("application/json; charset=latin1", Encoding.Latin1),
        ("application/merge-patch+json; charset=utf-8", Encoding.UTF8),
    ];

    /// <summary>A different zone for each readable content type, so each write is seen to land.</summary>
    private static readonly string[] Zones =
    [
        "Europe/Paris", "Asia/Tokyo", "America/New_York", "Australia/Sydney", "Africa/Cairo", "Europe/Berlin",
        "America/Denver", "Asia/Kolkata", "Europe/London", "America/Sao_Paulo", "Pacific/Auckland", "Asia/Dubai",
        "Europe/Madrid",
    ];

    // Each test works with a user of its own.
    private static long nextUser = 9_810_000;

    public static TheoryData<string, int> CharsetCases => new()
    {
        { "bot", 0 },
        { "bot", 1 },
        { "bot", 2 },
        { "web", 0 },
        { "web", 1 },
    };

    [Theory]
    [MemberData(nameof(CharsetCases))]
    public async Task A_body_in_a_character_set_that_cannot_be_read_is_refused(string caller, int route)
    {
        var (userId, client) = await NewCallerAsync(caller);
        await PutZoneAsync(userId, "America/Chicago");
        var (method, path, json) = route switch
        {
            0 => (HttpMethod.Post, "/tools/parse-datetime", """{"text":"tomorrow 6pm"}"""),
            1 => (HttpMethod.Put, $"/users/{userId}/settings", """{"timeZone":"Europe/Paris","dmConfirmations":false}"""),
            _ => (HttpMethod.Put, "/channels/sync", """{"channels":[]}"""),
        };

        foreach (var contentType in UnreadableContentTypes)
        {
            using var response = await client.SendAsync(Request(method, path, Encoding.UTF8.GetBytes(json), contentType));
            await AssertRefusedAsync(response, HttpStatusCode.UnsupportedMediaType, CharsetRefusal);
        }

        Assert.Equal("America/Chicago", await ZoneAsync(userId));
    }

    [Theory]
    [InlineData("bot")]
    [InlineData("web")]
    public async Task A_body_in_a_character_set_that_can_be_read_is_read_as_before(string caller)
    {
        var (userId, client) = await NewCallerAsync(caller);

        for (var i = 0; i < ReadableContentTypes.Length; i++)
        {
            var (contentType, encoding) = ReadableContentTypes[i];
            var json = $$"""{"timeZone":"{{Zones[i]}}","dmConfirmations":false}""";

            using var response = await client.SendAsync(
                Request(HttpMethod.Put, $"/users/{userId}/settings", encoding.GetBytes(json), contentType));

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{contentType}: {(int)response.StatusCode}");
            Assert.Equal(Zones[i], await ZoneAsync(userId));
        }

        using var parsed = await client.SendAsync(Request(
            HttpMethod.Post, "/tools/parse-datetime", Encoding.Unicode.GetBytes("""{"text":"tomorrow 6pm"}"""),
            "application/json; charset=utf-16"));
        Assert.Equal(HttpStatusCode.OK, parsed.StatusCode);
    }

    [Fact]
    public async Task A_content_type_that_is_not_json_is_answered_as_before()
    {
        var (userId, client) = await NewCallerAsync("web");
        var json = Encoding.UTF8.GetBytes("""{"timeZone":"Europe/Paris","dmConfirmations":false}""");

        foreach (var contentType in new[] { "text/plain; charset=x-no-such", "text/plain", "application/xml; charset=utf-7" })
        {
            using var response = await client.SendAsync(Request(HttpMethod.Put, $"/users/{userId}/settings", json, contentType));

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_body_over_1_MB_that_is_not_json_is_answered_as_before()
    {
        var (userId, client) = await NewCallerAsync("web");
        await PutZoneAsync(userId, "America/Chicago");
        var body = SettingsBody("Europe/Paris", Limit + 1);

        foreach (var contentType in new[] { null, "application/json; =x", "text/plain" })
        {
            var content = new ByteArrayContent(body);
            if (contentType is not null)
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, $"/users/{userId}/settings") { Content = content });

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.DoesNotContain(SizeRefusal, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal("America/Chicago", await ZoneAsync(userId));
    }

    [Fact]
    public async Task A_request_without_a_body_is_answered_as_before()
    {
        var (userId, client) = await NewCallerAsync("web");

        using var response = await client.SendAsync(
            Request(HttpMethod.Put, $"/users/{userId}/settings", [], UnreadableContentTypes[0]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_route_that_reads_no_body_is_answered_as_before()
    {
        var (userId, client) = await NewCallerAsync("web");

        foreach (var contentType in UnreadableContentTypes)
        {
            using var response = await client.SendAsync(
                Request(HttpMethod.Get, $"/users/{userId}/settings", Encoding.UTF8.GetBytes("{}"), contentType));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var large = await client.SendAsync(
            Request(HttpMethod.Get, $"/users/{userId}/settings", SettingsBody("Europe/Paris", Limit + 1), "application/json"));
        Assert.Equal(HttpStatusCode.OK, large.StatusCode);
    }

    [Fact]
    public async Task A_request_without_credentials_is_answered_401_as_before()
    {
        using var anonymous = fixture.Factory.CreateClient();

        using var unreadable = await anonymous.SendAsync(Request(
            HttpMethod.Put, "/users/1/settings", SettingsBody("Europe/Paris", 64), UnreadableContentTypes[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, unreadable.StatusCode);

        using var large = await anonymous.SendAsync(Request(
            HttpMethod.Put, "/users/1/settings", SettingsBody("Europe/Paris", Limit + 1), "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, large.StatusCode);
    }

    [Fact]
    public async Task A_web_caller_of_a_route_only_the_bot_may_use_is_answered_403_as_before()
    {
        var (_, client) = await NewCallerAsync("web");

        using var unreadable = await client.SendAsync(Request(
            HttpMethod.Put, "/channels/sync", Encoding.UTF8.GetBytes("""{"channels":[]}"""), UnreadableContentTypes[0]));
        Assert.Equal(HttpStatusCode.Forbidden, unreadable.StatusCode);

        var large = new byte[Limit + 1];
        Encoding.UTF8.GetBytes("""{"channels":[]}""").CopyTo(large, 0);
        large.AsSpan("""{"channels":[]}""".Length).Fill((byte)' ');
        using var oversized = await client.SendAsync(Request(HttpMethod.Put, "/channels/sync", large, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, oversized.StatusCode);
    }

    [Fact]
    public async Task A_web_callers_body_of_more_than_1_MB_is_refused()
    {
        var (userId, client) = await NewCallerAsync("web");
        await PutZoneAsync(userId, "America/Chicago");

        using var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/users/{userId}/settings", SettingsBody("Europe/Paris", Limit + 1), "application/json"));

        await AssertRefusedAsync(response, HttpStatusCode.RequestEntityTooLarge, SizeRefusal);
        Assert.Equal("America/Chicago", await ZoneAsync(userId));
    }

    [Fact]
    public async Task A_web_callers_body_of_1_MB_is_read()
    {
        var (userId, client) = await NewCallerAsync("web");

        using var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/users/{userId}/settings", SettingsBody("Europe/Paris", Limit), "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Europe/Paris", await ZoneAsync(userId));
    }

    [Fact]
    public async Task The_bots_body_of_more_than_1_MB_is_read()
    {
        var (userId, client) = await NewCallerAsync("bot");

        using var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/users/{userId}/settings", SettingsBody("Europe/Paris", 2 * Limit), "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Europe/Paris", await ZoneAsync(userId));
    }

    /// <summary>A settings body that is read as it is today, padded with spaces to
    /// <paramref name="size"/> bytes.</summary>
    internal static byte[] SettingsBody(string zone, int size)
    {
        var json = Encoding.UTF8.GetBytes($$"""{"timeZone":"{{zone}}","dmConfirmations":false}""");
        var body = new byte[size];
        json.CopyTo(body, 0);
        body.AsSpan(json.Length).Fill((byte)' ');
        return body;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return new HttpRequestMessage(method, path) { Content = content };
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string sentence)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        Assert.Equal(sentence, JsonSerializer.Deserialize<ErrorResponse>(body, JsonSerializerOptions.Web)!.Error);
    }

    /// <summary>A user of its own, and the caller: the bot, or that user signed in on the web.</summary>
    private async Task<(long UserId, HttpClient Client)> NewCallerAsync(string caller)
    {
        var userId = Interlocked.Increment(ref nextUser);
        var client = caller == "bot" ? fixture.Client : (await fixture.LoginAsync(userId)).Client;
        return (userId, client);
    }

    private async Task PutZoneAsync(long userId, string zone) =>
        (await fixture.Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(zone, false)))
            .EnsureSuccessStatusCode();

    private async Task<string?> ZoneAsync(long userId) =>
        (await fixture.Client.GetFromJsonAsync<UserSettingsDto>($"/users/{userId}/settings"))!.TimeZone;
}
