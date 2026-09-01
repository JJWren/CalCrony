using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>The server-context line above the Events | Polls | Templates | Activity switcher:
/// it names the server the view belongs to, omits itself when the name is unknown rather than
/// printing a placeholder or a snowflake, and never carries one server's name into the next.</summary>
public class GuildHeadingTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Names_the_current_server()
    {
        var handler = UseApi();
        handler.JsonFor = _ => GuildsJson((1, "Wren Den"), (2, "Game Night"));

        var cut = Render<GuildHeading>(p => p.Add(x => x.GuildId, 1L));

        cut.WaitForAssertion(() => Assert.Contains("Wren Den", cut.Markup));
        Assert.DoesNotContain("Game Night", cut.Markup);
        Assert.Single(cut.FindAll("p.guild-context"));
    }

    [Fact]
    public void Renders_nothing_when_the_name_is_unknown()
    {
        var handler = UseApi();
        // A guild the membership snapshot doesn't cover: no line at all — never the raw id.
        handler.JsonFor = _ => GuildsJson((2, "Game Night"));

        var cut = Render<GuildHeading>(p => p.Add(x => x.GuildId, 1L));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("p.guild-context")));
        Assert.DoesNotContain("1", cut.Markup);
    }

    [Fact]
    public void Switching_servers_replaces_the_name()
    {
        var handler = UseApi();
        handler.JsonFor = _ => GuildsJson((1, "Wren Den"), (2, "Game Night"));
        var cut = Render<GuildHeading>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Wren Den", cut.Markup));

        cut.Render(p => p.Add(x => x.GuildId, 2L));

        // The previous server's name must not survive the switch — that reading would be wrong
        // for every row rendered underneath it (issue #136's lesson, in miniature).
        cut.WaitForAssertion(() => Assert.Contains("Game Night", cut.Markup));
        Assert.DoesNotContain("Wren Den", cut.Markup);
    }

    private static string GuildsJson(params (long Id, string Name)[] guilds) => JsonSerializer.Serialize(
        new WebGuildListResponse(
            DateTimeOffset.UtcNow,
            [.. guilds.Select(g => new WebGuildDto(g.Id, g.Name, null, false))]),
        JsonWeb);

    private CapturingHandler UseApi()
    {
        var handler = new CapturingHandler();
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return handler;
    }

    /// <summary>Routes responses by request path.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = JsonFor?.Invoke(request) ?? "{}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
