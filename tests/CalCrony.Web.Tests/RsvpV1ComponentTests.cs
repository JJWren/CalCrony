using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Components;
using CalCrony.Web.Pages.App;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>RSVP v1 web surface: custom option editor, capacity badges, waitlist, closed state.</summary>
public class RsvpV1ComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Closed_rsvps_disable_the_buttons_and_say_so()
    {
        UseApi();
        var ev = SampleEvent(closesAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, 42L));

        Assert.All(cut.FindAll("button.rsvp-btn"), b => Assert.True(b.HasAttribute("disabled")));
        Assert.Contains("RSVPs are closed", cut.Markup);
    }

    [Fact]
    public void Open_rsvps_show_capacity_badges_with_seated_counts_only()
    {
        UseApi();
        var ev = SampleEvent(capacity: 2, waitlistedUserId: 77);

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, 42L));

        // One seat taken (42); the waitlisted 77 must not count toward 2.
        Assert.Contains("(1/2)", cut.Markup);
        Assert.All(cut.FindAll("button.rsvp-btn"), b => Assert.False(b.HasAttribute("disabled")));
    }

    [Fact]
    public void A_waitlisted_viewer_sees_their_queue_position()
    {
        UseApi();
        var ev = SampleEvent(capacity: 1, waitlistedUserId: 42);

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, 42L));

        Assert.Contains("#1 on the waitlist", cut.Markup);
    }

    [Fact]
    public void Event_detail_lists_the_waitlist_in_order()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(capacity: 1, waitlistedUserId: 77);
        RouteEventPages(handler, ev);

        var cut = Render<EventDetail>(p => p.Add(x => x.EventId, ev.Id));

        cut.WaitForAssertion(() => Assert.Contains("Waitlist", cut.Markup));
        Assert.Contains("user 77", cut.Markup);
    }

    [Fact]
    public void Create_form_sends_the_option_rows_and_cutoff()
    {
        var handler = UseApi();
        handler.JsonFor = req => req.RequestUri!.AbsolutePath.EndsWith("/templates") ? "[]" : null;

        var cut = Render<EventForm>(p => p.Add(x => x.GuildId, 1L));
        cut.Find("#ev-title").Change("Capped event");
        cut.Find("#ev-when").Change("friday 6pm");
        cut.Find("#ev-rsvp-close").Change("2h before");

        handler.NextJson = JsonSerializer.Serialize(SampleEvent(), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal("2h before", body.RsvpCloseText);
        Assert.NotNull(body.RsvpOptions);
        Assert.Equal(["Going", "Not going", "Maybe"], body.RsvpOptions!.Select(o => o.Label));
        Assert.True(body.RsvpOptions[0].IsAttending);
    }

    [Fact]
    public void Create_form_capacity_input_rides_along_on_the_attending_row()
    {
        var handler = UseApi();
        handler.JsonFor = req => req.RequestUri!.AbsolutePath.EndsWith("/templates") ? "[]" : null;

        var cut = Render<EventForm>(p => p.Add(x => x.GuildId, 1L));
        cut.Find("#ev-title").Change("Capped event");
        cut.Find("#ev-when").Change("friday 6pm");
        cut.FindAll("input[aria-label='Option capacity']")[0].Change("5");

        handler.NextJson = JsonSerializer.Serialize(SampleEvent(), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal(5, body.RsvpOptions![0].Capacity);
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
        this.AddAuthorization();
    }

    /// <summary>An event with a seated user 42 on the attending option; optionally a capacity, a
    /// waitlisted user, and an RSVP cutoff.</summary>
    private static EventDto SampleEvent(
        int? capacity = null, long? waitlistedUserId = null, DateTimeOffset? closesAt = null)
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, capacity, IsAttending: true);
        var notGoing = new RsvpOptionDto(Guid.NewGuid(), "❌", "Not going", 1, null);
        var rsvps = new List<RsvpDto>();
        if (waitlistedUserId != 42)
        {
            rsvps.Add(new RsvpDto(42, going.Id));
        }

        if (waitlistedUserId is { } queued)
        {
            rsvps.Add(new RsvpDto(queued, going.Id, Waitlisted: true));
        }

        return new EventDto(
            Guid.NewGuid(), 1, 2, "Capped Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled, [going, notGoing], rsvps,
            RsvpClosesAtUtc: closesAt);
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
