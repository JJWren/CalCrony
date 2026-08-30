using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Pages.App;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>Discord context on the event page (issue #80): server name in the back link,
/// channel chip, and message jump link — every piece omitted gracefully when absent.</summary>
public class EventContextComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Event_detail_shows_server_channel_and_discord_link_when_known()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(messageId: 424242, channelName: "game-night");
        RouteEventPages(handler, ev, guildName: "Wren Den");

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));

        cut.WaitForAssertion(() => Assert.Contains("💬 #game-night", cut.Markup));
        Assert.Contains("Back to Wren Den", cut.Markup);
        var link = cut.Find($"a[href='https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/424242']");
        Assert.Contains("Open in Discord", link.TextContent);
    }

    [Fact]
    public void Event_detail_omits_context_it_does_not_have()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(messageId: null, channelName: null);
        RouteEventPages(handler, ev, guildName: "Wren Den");

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));

        cut.WaitForAssertion(() => Assert.Contains(ev.Title, cut.Markup));
        Assert.DoesNotContain("💬", cut.Markup);
        Assert.DoesNotContain("Open in Discord", cut.Markup);
    }

    private static void RouteEventPages(CapturingHandler handler, EventDto ev, string guildName)
    {
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/availability") =>
                JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb),
            var p when p.EndsWith("/notifications") => "[]",
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, guildName, null, false)]), JsonWeb),
            _ => JsonSerializer.Serialize(ev, JsonWeb),
        };
    }

    private CapturingHandler UseApi()
    {
        var handler = new CapturingHandler();
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return handler;
    }

    private void SetupAuth()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<CalCrony.Web.Auth.ITokenStore, CalCrony.Web.Auth.InMemoryTokenStore>();
        Services.AddSingleton<CalCrony.Web.Auth.JwtAuthenticationStateProvider>();
        Services.AddScoped(sp => new CalCrony.Web.Auth.AuthApiClient(
            new HttpClient { BaseAddress = new Uri("http://localhost") },
            sp.GetRequiredService<CalCrony.Web.Auth.ITokenStore>(),
            sp.GetRequiredService<CalCrony.Web.Auth.JwtAuthenticationStateProvider>()));
        this.AddTestAuthorization();
    }

    private static EventDto SampleEvent(long? messageId, string? channelName)
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Context Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, messageId, null, null, EventStatus.Scheduled, [going], [],
            ChannelName: channelName);
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
