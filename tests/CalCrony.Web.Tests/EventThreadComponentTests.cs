using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Pages.App;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

public class EventThreadComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Create_form_checkbox_sends_wants_thread()
    {
        var handler = UseApi();
        handler.JsonFor = req => req.RequestUri!.AbsolutePath.EndsWith("/templates") ? "[]" : null;

        var cut = RenderComponent<EventForm>(p => p.Add(x => x.GuildId, 1L));
        cut.Find("#ev-title").Change("Threaded");
        cut.Find("#ev-when").Change("friday 6pm");
        cut.Find("#ev-thread").Change(true);

        handler.NextJson = JsonSerializer.Serialize(SampleEvent(threadId: null), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.True(body.WantsThread);
    }

    [Fact]
    public void Event_detail_shows_the_thread_chip_only_when_a_thread_exists()
    {
        var handler = UseApi();
        SetupAuth();
        var threaded = SampleEvent(threadId: 888200);
        RouteEventPages(handler, threaded);
        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, threaded.Id));
        cut.WaitForAssertion(() => Assert.Contains("Discussion thread open in Discord", cut.Markup));
    }

    [Fact]
    public void Event_detail_hides_the_thread_chip_without_a_thread()
    {
        var handler = UseApi();
        SetupAuth();
        var plain = SampleEvent(threadId: null);
        RouteEventPages(handler, plain);
        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, plain.Id));
        cut.WaitForAssertion(() => Assert.Contains(plain.Title, cut.Markup));
        Assert.DoesNotContain("Discussion thread", cut.Markup);
    }

    private static void RouteEventPages(CapturingHandler handler, EventDto ev)
    {
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/availability") =>
                JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb),
            var p when p.EndsWith("/notifications") => "[]",
            var p when p.EndsWith("/templates") => "[]",
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, "G", null, false)]), JsonWeb),
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

    private static EventDto SampleEvent(long? threadId)
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Thread Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled, [going], [],
            WantsThread: threadId is not null,
            ThreadId: threadId);
    }

    /// <summary>Routes responses by request; records the last request body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public string? NextJson { get; set; }

        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            var json = NextJson ?? JsonFor?.Invoke(request) ?? "{}";
            NextJson = null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
