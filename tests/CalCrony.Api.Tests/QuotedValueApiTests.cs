using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>Five refusals quote back a value that was sent — an unknown time zone on the parse
/// route, a server's settings and a user's settings, an unknown theme, and an unknown action in the
/// activity log's filter. A value of up to 64 characters is quoted whole; a longer one as its first
/// 64 and "…", or its first 63 where the 64th and 65th places hold the two halves of one
/// character.</summary>
public class QuotedValueApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    // Each test case works with ids of its own.
    private static long nextId = 9_840_000;

    public static TheoryData<int> Refusals => new() { 0, 1, 2, 3, 4 };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_value_of_64_characters_is_quoted_whole(int refusal)
    {
        var value = new string('z', 64);

        await AssertQuotedAsync(refusal, value, value);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_longer_value_is_quoted_as_its_first_64_characters(int refusal)
    {
        await AssertQuotedAsync(refusal, new string('z', 65), new string('z', 64) + "…");
        await AssertQuotedAsync(refusal, new string('z', 5000), new string('z', 64) + "…");
        await AssertQuotedAsync(refusal, new string('z', 62) + "😀" + "zzz", new string('z', 62) + "😀" + "…");
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_cut_that_would_split_a_character_stops_after_the_63rd(int refusal)
    {
        await AssertQuotedAsync(refusal, new string('z', 63) + "😀", new string('z', 63) + "…");
        await AssertQuotedAsync(refusal, new string('z', 63) + "😀" + "zzz", new string('z', 63) + "…");
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_known_value_is_accepted_as_before(int refusal)
    {
        var value = refusal switch
        {
            3 => InterfaceThemes.All[1],
            4 => Enum.GetNames<ActionLogAction>()[0],
            _ => "America/Chicago",
        };

        using var response = await SendAsync(refusal, value);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task AssertQuotedAsync(int refusal, string value, string quoted)
    {
        using var response = await SendAsync(refusal, value);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}");
        var sentence = refusal switch
        {
            3 => $"Unknown theme \"{quoted}\". Valid themes: {string.Join(", ", InterfaceThemes.All)}.",
            4 => $"Unknown action \"{quoted}\". Valid actions: {string.Join(", ", Enum.GetNames<ActionLogAction>())}.",
            _ => $"Unknown time zone \"{quoted}\". Use an IANA id like America/Chicago.",
        };
        Assert.Equal(sentence, JsonSerializer.Deserialize<ErrorResponse>(body, JsonSerializerOptions.Web)!.Error);
    }

    /// <summary>Sends the value, as the bot, where the refusal reads it.</summary>
    private Task<HttpResponseMessage> SendAsync(int refusal, string value)
    {
        var id = Interlocked.Increment(ref nextId);
        var bot = fixture.Client;
        return refusal switch
        {
            0 => bot.PostAsJsonAsync("/tools/parse-datetime", new ParseDateTimeRequest("tomorrow 6pm", TimeZone: value)),
            1 => bot.PutAsJsonAsync($"/guilds/{id}/settings", new GuildSettingsDto(value, null)),
            2 => bot.PutAsJsonAsync($"/users/{id}/settings", new UserSettingsDto(value, false)),
            3 => bot.PutAsJsonAsync($"/users/{id}/settings", new UserSettingsDto(null, false, Theme: value)),
            _ => bot.GetAsync($"/guilds/{id}/actions?action={Uri.EscapeDataString(value)}"),
        };
    }
}
