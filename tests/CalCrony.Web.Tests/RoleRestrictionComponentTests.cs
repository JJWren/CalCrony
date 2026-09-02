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

/// <summary>RSVP v2 §3.5 on the web: restriction chips read the API's name snapshots with an id
/// fallback, the RSVP buttons surface the API's refusal unchanged (no client-side prediction —
/// the web can't see the caller's roles), the edit form offers only "remove", and a refused poll
/// vote hides the add-option form.</summary>
public class RoleRestrictionComponentTests : TestContext
{
    private const long UserId = 42;
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);
    private static readonly RoleRefDto Raiders = new(9001, "Raiders");
    private static readonly RoleRefDto Unnamed = new(9002, null);

    [Fact]
    public void Event_detail_renders_one_chip_for_a_shared_restriction_with_names_and_an_id_fallback()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(
            new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null, IsAttending: true, AllowedRoles: [Raiders, Unnamed]),
            new RsvpOptionDto(Guid.NewGuid(), "❌", "Out", 1, null, AllowedRoles: [Unnamed, Raiders]));
        RouteEventPages(handler, ev);

        var cut = Render<EventDetail>(p => p.Add(x => x.EventId, ev.Id));

        cut.WaitForAssertion(() => Assert.Contains("🔒 limited to @Raiders, role #9002", cut.Markup));
        Assert.DoesNotContain("Going limited to", cut.Markup);
    }

    [Fact]
    public void Event_detail_renders_a_chip_per_option_when_restrictions_differ_and_names_the_attendee_role()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(
            new RsvpOptionDto(Guid.NewGuid(), "🛡️", "Tank", 0, null, IsAttending: true,
                AttendeeRoleId: 555, AllowedRoles: [Raiders], AttendeeRoleName: "Tank Squad"),
            new RsvpOptionDto(Guid.NewGuid(), "❌", "Out", 1, null));
        RouteEventPages(handler, ev);

        var cut = Render<EventDetail>(p => p.Add(x => x.EventId, ev.Id));

        cut.WaitForAssertion(() => Assert.Contains("🔒 Tank limited to @Raiders", cut.Markup));
        // The §3.6 chip reads the name snapshot now, and still falls back to the id without one.
        Assert.Contains("Tank grants @Tank Squad", cut.Markup);
        Assert.DoesNotContain("grants role #555", cut.Markup);
    }

    [Fact]
    public async Task Rsvp_buttons_keep_a_restricted_option_enabled_and_surface_the_apis_refusal_unchanged()
    {
        var handler = UseApi();
        var ev = SampleEvent(
            new RsvpOptionDto(Guid.NewGuid(), "🛡️", "Tank", 0, null, IsAttending: true, AllowedRoles: [Raiders]));
        handler.Respond = req => req.Method == HttpMethod.Put
            ? (HttpStatusCode.Forbidden, JsonSerializer.Serialize(new ErrorResponse("This option is limited to @Raiders."), JsonWeb))
            : (HttpStatusCode.OK, JsonSerializer.Serialize(ev, JsonWeb));

        var cut = Render<RsvpButtons>(p => p.Add(x => x.Event, ev).Add(x => x.UserId, UserId));

        var button = Assert.Single(cut.FindAll("button"));
        Assert.False(button.HasAttribute("disabled"));
        Assert.Contains("🔒", button.TextContent);
        Assert.Equal("Limited to @Raiders", button.GetAttribute("title"));

        await button.ClickAsync(new());

        Assert.Contains("This option is limited to @Raiders.", cut.Markup);
    }

    [Fact]
    public void Edit_form_offers_only_to_remove_a_restriction_and_sends_clear_allowed_roles()
    {
        var handler = UseApi();
        var ev = SampleEvent(
            new RsvpOptionDto(Guid.NewGuid(), "🛡️", "Tank", 0, null, IsAttending: true, AllowedRoles: [Raiders]),
            new RsvpOptionDto(Guid.NewGuid(), "❌", "Out", 1, null));
        handler.Respond = _ => (HttpStatusCode.OK, JsonSerializer.Serialize(ev, JsonWeb));

        var cut = Render<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));
        cut.WaitForAssertion(() => Assert.Contains("Remove signup restriction — Tank limited to @Raiders", cut.Markup));

        cut.Find("#ev-clear-restriction").Change(true);
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();

        var body = JsonSerializer.Deserialize<UpdateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.True(body.ClearAllowedRoles);
        Assert.Null(body.AllowedRoleIds);
        // An untouched option editor submits no options — carrying the restriction through the
        // rows must not read as a change.
        Assert.Null(body.RsvpOptions);
    }

    [Fact]
    public void Edit_form_hides_the_remove_checkbox_for_an_unrestricted_event()
    {
        var handler = UseApi();
        var ev = SampleEvent(new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null, IsAttending: true));
        handler.Respond = _ => (HttpStatusCode.OK, JsonSerializer.Serialize(ev, JsonWeb));

        var cut = Render<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));

        cut.WaitForAssertion(() => Assert.Contains("Save changes", cut.Markup));
        Assert.Empty(cut.FindAll("#ev-clear-restriction"));
    }

    [Fact]
    public async Task A_refused_poll_vote_shows_the_reason_and_hides_the_add_option_form()
    {
        var handler = UseApi();
        SetupAuth();
        var poll = SamplePoll(allowUserOptions: true, allowedRoles: [Raiders]);
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(
                new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(poll.GuildId, "G", null, false)]), JsonWeb)),
            var p when p.Contains("/votes/") => (HttpStatusCode.Conflict, JsonSerializer.Serialize(
                new ErrorResponse("We can't confirm your roles right now — vote from Discord.", ErrorCodes.RoleRestricted), JsonWeb)),
            _ => (HttpStatusCode.OK, JsonSerializer.Serialize(poll, JsonWeb)),
        };

        var cut = Render<PollDetail>(p => p.Add(x => x.PollId, poll.Id));
        cut.WaitForAssertion(() => Assert.Contains("🔒 limited to @Raiders", cut.Markup));
        Assert.Contains("Add an option", cut.Markup);

        await cut.FindAll("button").First(b => b.TextContent.Contains("a")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("We can't confirm your roles right now — vote from Discord.", cut.Markup));
        Assert.DoesNotContain("Add an option", cut.Markup);

        // A successful CLEAR is not entry (withdrawing is never gated): the form stays hidden...
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(
                new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(poll.GuildId, "G", null, false)]), JsonWeb)),
            _ => (HttpStatusCode.OK, JsonSerializer.Serialize(poll, JsonWeb)),
        };
        await cut.FindAll("button").First(b => b.TextContent.Contains("a")).ClickAsync(new());
        cut.WaitForAssertion(() => Assert.DoesNotContain("We can't confirm", cut.Markup));
        Assert.DoesNotContain("Add an option", cut.Markup);

        // ...whereas a vote that now lands (the snapshot caught up, or the role arrived) shows it again.
        // The page reads its user id from the session, which this harness leaves unset (0), so the
        // vote that proves entry belongs to user 0.
        var voted = poll with { Votes = [new PollVoteDto(0, poll.Options[0].Id)] };
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(
                new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(poll.GuildId, "G", null, false)]), JsonWeb)),
            _ => (HttpStatusCode.OK, JsonSerializer.Serialize(voted, JsonWeb)),
        };
        await cut.FindAll("button").First(b => b.TextContent.Contains("a")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Add an option", cut.Markup));
    }

    [Fact]
    public async Task A_vote_refused_for_another_reason_keeps_the_add_option_form()
    {
        var handler = UseApi();
        SetupAuth();
        var poll = SamplePoll(allowUserOptions: true, allowedRoles: [Raiders]);
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(
                new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(poll.GuildId, "G", null, false)]), JsonWeb)),
            // The same 409 status, but a vote race — not a role refusal, and carrying no code.
            var p when p.Contains("/votes/") => (HttpStatusCode.Conflict, JsonSerializer.Serialize(
                new ErrorResponse("Your vote changed at the same time — try again."), JsonWeb)),
            _ => (HttpStatusCode.OK, JsonSerializer.Serialize(poll, JsonWeb)),
        };

        var cut = Render<PollDetail>(p => p.Add(x => x.PollId, poll.Id));
        cut.WaitForAssertion(() => Assert.Contains("Add an option", cut.Markup));

        await cut.FindAll("button").First(b => b.TextContent.Contains("a")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("try again", cut.Markup));
        Assert.Contains("Add an option", cut.Markup);
    }

    private static EventDto SampleEvent(params RsvpOptionDto[] options) => new(
        Guid.NewGuid(), 1, 2, "Restricted Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
        3, null, null, null, EventStatus.Scheduled, options, [],
        AttendeeRoleId: options.FirstOrDefault(o => o.IsAttending)?.AttendeeRoleId);

    private static PollDto SamplePoll(bool allowUserOptions, IReadOnlyList<RoleRefDto> allowedRoles)
    {
        var options = new List<PollOptionDto>
        {
            new(Guid.NewGuid(), "a", null, null, 0, 0),
            new(Guid.NewGuid(), "b", null, null, 1, 0),
        };
        return new PollDto(
            Guid.NewGuid(), 1, 2, "Raid night?", false, false, false, allowUserOptions, 3, 4,
            PollStatus.Open, null, null, "UTC", null, options, [], allowedRoles);
    }

    private static void RouteEventPages(CapturingHandler handler, EventDto ev)
    {
        var now = DateTimeOffset.UtcNow;
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/availability") =>
                (HttpStatusCode.OK, JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb)),
            var p when p.EndsWith("/notifications") => (HttpStatusCode.OK, "[]"),
            "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, "G", null, false)]), JsonWeb)),
            _ => (HttpStatusCode.OK, JsonSerializer.Serialize(ev, JsonWeb)),
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

    /// <summary>Routes responses (status + body) by request; records the last request body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public Func<HttpRequestMessage, (HttpStatusCode Status, string Json)>? Respond { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            var (status, json) = Respond?.Invoke(request) ?? (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
