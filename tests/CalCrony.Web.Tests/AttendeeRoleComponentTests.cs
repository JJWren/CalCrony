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

public class AttendeeRoleComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Event_detail_shows_the_role_line_only_when_a_role_is_set()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(attendeeRoleId: 555001);
        RouteEventPages(handler, ev);

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        cut.WaitForAssertion(() => Assert.Contains("Going grants role #555001", cut.Markup));
    }

    [Fact]
    public void Event_detail_hides_the_role_line_without_a_role()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(attendeeRoleId: null);
        RouteEventPages(handler, ev);

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        cut.WaitForAssertion(() => Assert.Contains(ev.Title, cut.Markup));
        Assert.DoesNotContain("Going grants role", cut.Markup);
    }

    [Fact]
    public void Edit_form_clear_checkbox_sends_clear_attendee_role()
    {
        var handler = UseApi();
        var ev = SampleEvent(attendeeRoleId: 555002);
        handler.JsonFor = _ => JsonSerializer.Serialize(ev, JsonWeb);

        var cut = RenderComponent<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));
        cut.WaitForAssertion(() => Assert.Contains("Remove attendee role (#555002)", cut.Markup));

        cut.Find("#ev-clear-role").Change(true);
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();

        var body = JsonSerializer.Deserialize<UpdateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.True(body.ClearAttendeeRole);
        Assert.Null(body.AttendeeRoleId);
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

    private static EventDto SampleEvent(long? attendeeRoleId)
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Role Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled, [going], [],
            AttendeeRoleId: attendeeRoleId);
    }

    /// <summary>Routes responses by request; records the last request body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            var json = JsonFor?.Invoke(request) ?? "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
