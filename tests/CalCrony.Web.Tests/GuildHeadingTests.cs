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
    public void Switching_servers_clears_the_old_name_before_the_new_one_lands()
    {
        var handler = UseApi();
        handler.JsonFor = _ => GuildsJson((1, "Wren Den"), (2, "Game Night"));
        var cut = Render<GuildHeading>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Wren Den", cut.Markup));

        // Hold the switch's lookup open: the window between the switch and its answer is exactly
        // where the previous server's name must not sit above the next server's rows (issue
        // #136's lesson, in miniature).
        var gate = new TaskCompletionSource();
        handler.WaitFor = gate.Task;
        cut.Render(p => p.Add(x => x.GuildId, 2L));
        Assert.Empty(cut.FindAll("p.guild-context"));
        Assert.DoesNotContain("Wren Den", cut.Markup);

        gate.SetResult();

        cut.WaitForAssertion(() => Assert.Contains("Game Night", cut.Markup));
        Assert.DoesNotContain("Wren Den", cut.Markup);
    }

    [Fact]
    public void A_late_answer_from_before_the_switch_cannot_overwrite_the_new_name()
    {
        var handler = UseApi();
        // The first lookup stalls; the switch's lookup answers at once.
        var stall = new TaskCompletionSource();
        handler.WaitFor = stall.Task;
        handler.JsonFor = _ => GuildsJson((1, "Wren Den"));
        var cut = Render<GuildHeading>(p => p.Add(x => x.GuildId, 1L));

        handler.WaitFor = null;
        handler.JsonFor = _ => GuildsJson((1, "Wren Den"), (2, "Game Night"));
        cut.Render(p => p.Add(x => x.GuildId, 2L));
        cut.WaitForAssertion(() => Assert.Contains("Game Night", cut.Markup));

        // Now the stale lookup completes with a list that has no entry for guild 2. Without a
        // generation stamp it would pass the "still the same guild" check and blank the heading.
        stall.SetResult();

        cut.WaitForAssertion(() => Assert.Contains("Game Night", cut.Markup));
        Assert.Single(cut.FindAll("p.guild-context"));
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

    /// <summary>Routes responses by request path; a request captures the routing and the gate in
    /// force when it is SENT, so a later change to either only affects later requests.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        /// <summary>When set, a request sent now waits for this task before it answers.</summary>
        public Task? WaitFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = JsonFor?.Invoke(request) ?? "{}";
            if (WaitFor is { } gate)
            {
                await gate;
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
