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

/// <summary>RSVP v2 §3.3 web surface: several selected buttons, the option-scoped withdrawal, the
/// helper line, the form checkbox (create and edit), and the detail chip.</summary>
public class MultiRsvpComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Several_buttons_are_selected_and_the_helper_line_shows_in_multi_mode()
    {
        UseApi();
        var ev = SampleEvent(multi: true, myOptions: ["Going", "Maybe"]);

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, 42L));

        var selected = cut.FindAll("button.rsvp-btn.selected");
        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, b => b.TextContent.Contains("Going"));
        Assert.Contains(selected, b => b.TextContent.Contains("Maybe"));
        Assert.Contains("Pick every option that applies — click a selected one to remove it", cut.Markup);
    }

    [Fact]
    public void Single_choice_mode_keeps_one_selection_and_no_helper_line()
    {
        UseApi();
        var ev = SampleEvent(multi: false, myOptions: ["Going"]);

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, 42L));

        Assert.Single(cut.FindAll("button.rsvp-btn.selected"));
        Assert.DoesNotContain("Pick every option that applies", cut.Markup);
    }

    [Fact]
    public void Clicking_a_selected_button_sends_the_option_scoped_delete()
    {
        var handler = UseApi();
        var ev = SampleEvent(multi: true, myOptions: ["Going", "Maybe"]);
        var maybe = ev.Options.Single(o => o.Label == "Maybe");
        handler.NextJson = JsonSerializer.Serialize(ev, JsonWeb);

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, 42L));
        cut.FindAll("button.rsvp-btn").First(b => b.TextContent.Contains("Maybe")).Click();

        cut.WaitForAssertion(() => Assert.NotNull(handler.LastRequest));
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"/events/{ev.Id}/rsvps/42/options/{maybe.Id}", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Clicking_an_unselected_button_still_puts()
    {
        var handler = UseApi();
        var ev = SampleEvent(multi: true, myOptions: ["Going"]);
        handler.NextJson = JsonSerializer.Serialize(ev, JsonWeb);

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, 42L));
        cut.FindAll("button.rsvp-btn").First(b => b.TextContent.Contains("Maybe")).Click();

        cut.WaitForAssertion(() => Assert.NotNull(handler.LastRequest));
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/events/{ev.Id}/rsvps/42", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Create_form_checkbox_sends_the_flag()
    {
        var handler = UseApi();
        handler.JsonFor = req => req.RequestUri!.AbsolutePath.EndsWith("/templates") ? "[]" : null;

        var cut = Render<EventForm>(p => p.Add(x => x.GuildId, 1L));
        cut.Find("#ev-title").Change("Pick several");
        cut.Find("#ev-when").Change("friday 6pm");
        cut.Find("#ev-multi-rsvp").Change(true);

        handler.NextJson = JsonSerializer.Serialize(SampleEvent(multi: true), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.True(body.AllowMultipleRsvps);
    }

    [Fact]
    public void Create_form_leaves_the_flag_off_by_default()
    {
        var handler = UseApi();
        handler.JsonFor = req => req.RequestUri!.AbsolutePath.EndsWith("/templates") ? "[]" : null;

        var cut = Render<EventForm>(p => p.Add(x => x.GuildId, 1L));
        cut.Find("#ev-title").Change("One choice");
        cut.Find("#ev-when").Change("friday 6pm");

        handler.NextJson = JsonSerializer.Serialize(SampleEvent(), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.False(body.AllowMultipleRsvps);
    }

    [Fact]
    public void Edit_form_prefills_the_checkbox_and_sends_the_flag_only_when_it_changed()
    {
        var handler = UseApi();
        var ev = SampleEvent(multi: true);
        handler.JsonFor = _ => JsonSerializer.Serialize(ev, JsonWeb);

        // Untouched: the update says nothing about the switch (null = unchanged).
        var untouched = Render<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));
        untouched.WaitForAssertion(() => Assert.True(untouched.Find("#ev-multi-rsvp").HasAttribute("checked")));
        Assert.Contains("Turning it off is refused while anyone holds more than one RSVP", untouched.Markup);
        untouched.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();
        var unchanged = JsonSerializer.Deserialize<UpdateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Null(unchanged.AllowMultipleRsvps);

        // Unticked: the update carries false.
        var toggled = Render<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));
        toggled.WaitForAssertion(() => Assert.True(toggled.Find("#ev-multi-rsvp").HasAttribute("checked")));
        toggled.Find("#ev-multi-rsvp").Change(false);
        toggled.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();
        var turnedOff = JsonSerializer.Deserialize<UpdateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.False(turnedOff.AllowMultipleRsvps);
    }

    [Fact]
    public void A_reused_form_instance_starts_a_fresh_create_route_with_the_flag_off()
    {
        var handler = UseApi();
        var edited = SampleEvent(multi: true);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath.EndsWith("/templates")
            ? "[]"
            : JsonSerializer.Serialize(edited, JsonWeb);

        // The routable component is reused across navigations. Create for guild G first, so the
        // per-guild template cache is warm and cannot be what resets the flag…
        var cut = Render<EventForm>(p => p.Add(x => x.GuildId, edited.GuildId));
        cut.WaitForAssertion(() => Assert.False(cut.Find("#ev-multi-rsvp").HasAttribute("checked")));

        // …then an edit in G that loads `true`…
        cut.Render(p => p.Add(x => x.EventId, (Guid?)edited.Id));
        cut.WaitForAssertion(() => Assert.True(cut.Find("#ev-multi-rsvp").HasAttribute("checked")));

        // …then G's create route again must not submit that value.
        cut.Render(p => p.Add(x => x.EventId, (Guid?)null).Add(x => x.GuildId, edited.GuildId));
        cut.WaitForAssertion(() => Assert.False(cut.Find("#ev-multi-rsvp").HasAttribute("checked")));
        cut.Find("#ev-title").Change("Fresh event");
        cut.Find("#ev-when").Change("friday 6pm");
        handler.NextJson = JsonSerializer.Serialize(SampleEvent(), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.False(body.AllowMultipleRsvps);
    }

    [Fact]
    public void Event_detail_shows_the_chip_only_in_multi_mode()
    {
        var handler = UseApi();
        SetupAuth();
        var multi = SampleEvent(multi: true);
        RouteEventPages(handler, multi);
        var cut = Render<EventDetail>(p => p.Add(x => x.EventId, multi.Id));
        cut.WaitForAssertion(() => Assert.Contains("☑️ multiple RSVPs", cut.Markup));

        var single = SampleEvent();
        RouteEventPages(handler, single);
        var plain = Render<EventDetail>(p => p.Add(x => x.EventId, single.Id));
        plain.WaitForAssertion(() => Assert.Contains(single.Title, plain.Markup));
        Assert.DoesNotContain("multiple RSVPs", plain.Markup);
    }

    // ---------- helpers ----------

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

    /// <summary>Going / Not going / Maybe, with user 42 holding the named options.</summary>
    private static EventDto SampleEvent(bool multi = false, string[]? myOptions = null)
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null, IsAttending: true);
        var notGoing = new RsvpOptionDto(Guid.NewGuid(), "❌", "Not going", 1, null);
        var maybe = new RsvpOptionDto(Guid.NewGuid(), "🤔", "Maybe", 2, null);
        RsvpOptionDto[] options = [going, notGoing, maybe];
        var rsvps = (myOptions ?? []).Select(label => new RsvpDto(42, options.Single(o => o.Label == label).Id)).ToList();
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Multi Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled, options, rsvps,
            AllowMultipleRsvps: multi);
    }

    /// <summary>Routes responses by request; records the last request and its body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? NextJson { get; set; }

        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
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
