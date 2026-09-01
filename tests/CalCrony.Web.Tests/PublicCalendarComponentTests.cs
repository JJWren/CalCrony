using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Pages;
using CalCrony.Web.Pages.App;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>The public calendar page (/c/{slug}) and its Server-settings card (issue #121).</summary>
public class PublicCalendarComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Month_grid_renders_events_as_discord_jump_links_and_marks_projections()
    {
        var handler = UseApi();
        handler.Respond = req => req.RequestUri!.AbsolutePath.StartsWith("/public/calendars/")
            ? (HttpStatusCode.OK, JsonSerializer.Serialize(SampleMonth(), JsonWeb))
            : (HttpStatusCode.NotFound, null);

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "abc"));

        cut.WaitForAssertion(() => Assert.Contains("Test Guild", cut.Markup));
        Assert.Contains("September 2026", cut.Markup);
        // ARIA grid pattern: header and week rows own the cells.
        Assert.Equal(6, cut.FindAll(".pub-cal-grid [role='row']").Count); // 1 header + 5 weeks
        Assert.Empty(cut.FindAll(".pub-cal-grid > [role='gridcell']"));
        Assert.Contains("times in America/Chicago", cut.Markup);

        // The posted event links to its Discord message (once in the grid, once in the agenda).
        var links = cut.FindAll("a.pub-cal-chip");
        Assert.Equal(2, links.Count);
        Assert.All(links, a => Assert.Equal("https://discord.com/channels/1/2/3", a.GetAttribute("href")));
        Assert.All(links, a => Assert.Equal("noopener", a.GetAttribute("rel")));

        // The projected occurrence is a plain, dashed chip with nothing to link to yet.
        var projected = cut.FindAll(".pub-cal-chip.projected");
        Assert.Equal(2, projected.Count);
        Assert.All(projected, chip => Assert.Equal("span", chip.TagName.ToLowerInvariant()));
        Assert.Contains("haven't been posted to Discord yet", cut.Markup);

        // Nothing private leaks into the page — the DTO can't even carry it.
        Assert.DoesNotContain("rsvp", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inactive_links_show_a_friendly_message_instead_of_an_error()
    {
        var handler = UseApi();
        handler.Respond = _ => (HttpStatusCode.NotFound, null);

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "gone"));

        cut.WaitForAssertion(() => Assert.Contains("This calendar link isn't active", cut.Markup));
        Assert.Empty(cut.FindAll(".pub-cal-grid"));
    }

    [Fact]
    public void Month_navigation_moves_through_the_query_string()
    {
        var handler = UseApi();
        handler.Respond = req => req.RequestUri!.AbsolutePath.StartsWith("/public/calendars/")
            ? (HttpStatusCode.OK, JsonSerializer.Serialize(SampleMonth(), JsonWeb))
            : (HttpStatusCode.NotFound, null);
        var nav = Services.GetRequiredService<NavigationManager>();

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "abc"));
        cut.WaitForAssertion(() => Assert.Contains("September 2026", cut.Markup));

        cut.Find("button[aria-label='Next month']").Click();
        Assert.EndsWith("/c/abc?m=2026-10", nav.Uri);

        cut.Find("button[aria-label='Previous month']").Click();
        Assert.EndsWith("/c/abc?m=2026-08", nav.Uri);
    }

    [Fact]
    public void Settings_card_lets_a_manager_turn_the_calendar_on_and_shows_the_link()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = UseApi();
        var now = DateTimeOffset.UtcNow;
        var enabled = false;
        handler.Respond = req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/guilds/1/public-calendar" && req.Method == HttpMethod.Put)
            {
                enabled = true;
                return (HttpStatusCode.OK, JsonSerializer.Serialize(
                    new PublicCalendarSettingsDto(true, "abc123", "/c/abc123", null), JsonWeb));
            }

            return path switch
            {
                "/guilds/1/public-calendar" => (HttpStatusCode.OK, JsonSerializer.Serialize(
                    enabled
                        ? new PublicCalendarSettingsDto(true, "abc123", "/c/abc123", null)
                        : new PublicCalendarSettingsDto(false, null, null, null), JsonWeb)),
                "/guilds/1/settings" => (HttpStatusCode.OK, JsonSerializer.Serialize(new GuildSettingsDto("UTC", 5), JsonWeb)),
                "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(
                    new WebGuildListResponse(now, [new WebGuildDto(1, "G", null, true)]), JsonWeb)),
                "/guilds/1/feed-token" => (HttpStatusCode.OK, JsonSerializer.Serialize(new FeedTokenDto("tok", "/feeds/tok.ics"), JsonWeb)),
                _ => (HttpStatusCode.OK, "[]"),
            };
        };

        var cut = Render<GuildSettings>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => cut.Find("#gs-public-cal"));
        Assert.DoesNotContain("/c/abc123", cut.Markup);

        cut.Find("#gs-public-cal").Change(true);

        var body = JsonSerializer.Deserialize<PublicCalendarRequest>(handler.LastBody!, JsonWeb)!;
        Assert.True(body.Enabled);
        Assert.False(body.Regenerate);
        cut.WaitForAssertion(() => Assert.Contains("/c/abc123", cut.Markup));
        Assert.Contains("New link", cut.Markup); // regenerate is offered, behind an inline confirm
    }

    [Fact]
    public void A_rejected_month_shows_the_message_instead_of_claiming_the_link_is_dead()
    {
        var handler = UseApi();
        handler.Respond = _ => (HttpStatusCode.BadRequest, JsonSerializer.Serialize(
            new ErrorResponse("Pick a month within 2 years of today (month 1-12)."), JsonWeb));

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "abc"));

        cut.WaitForAssertion(() => Assert.Contains("Couldn't load this calendar", cut.Markup));
        Assert.Contains("Pick a month within 2 years", cut.Markup);
        Assert.DoesNotContain("isn't active", cut.Markup);
    }

    [Fact]
    public void Navigation_stops_at_the_served_range()
    {
        var handler = UseApi();
        var atLatest = SampleMonth() with { Year = 2028, Month = 8 }; // LatestMonth in SampleMonth
        handler.Respond = _ => (HttpStatusCode.OK, JsonSerializer.Serialize(atLatest, JsonWeb));

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "abc"));

        cut.WaitForAssertion(() => Assert.Contains("August 2028", cut.Markup));
        Assert.True(cut.Find("button[aria-label='Next month']").HasAttribute("disabled"));
        Assert.False(cut.Find("button[aria-label='Previous month']").HasAttribute("disabled"));
    }

    [Fact]
    public void Agenda_shows_metadata_visibly_and_grid_chips_expose_it_to_assistive_tech()
    {
        var handler = UseApi();
        handler.Respond = _ => (HttpStatusCode.OK, JsonSerializer.Serialize(SampleMonth(), JsonWeb));

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "abc"));
        cut.WaitForAssertion(() => Assert.Contains("Test Guild", cut.Markup));

        // Agenda (mobile): duration, place, and channel are visible text, not a tooltip.
        var agendaMeta = cut.FindAll(".d-md-none .pub-cal-meta");
        Assert.Equal(2, agendaMeta.Count);
        Assert.Contains("1 hr 30 min", agendaMeta[0].TextContent);
        Assert.Contains("Voice", agendaMeta[0].TextContent);
        Assert.Contains("#events", agendaMeta[0].TextContent);
        Assert.Contains("not posted yet", agendaMeta[1].TextContent);

        // Grid: compact chips carry the same details as screen-reader text.
        var hidden = cut.FindAll(".pub-cal-grid .pub-cal-chip .visually-hidden");
        Assert.Equal(2, hidden.Count);
        Assert.Contains("Voice", hidden[0].TextContent);
    }

    [Fact]
    public void Switching_slugs_never_shows_the_previous_calendars_events()
    {
        var handler = UseApi();
        handler.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/public/calendars/abc")
            ? (HttpStatusCode.OK, JsonSerializer.Serialize(SampleMonth(), JsonWeb))
            : (HttpStatusCode.BadRequest, JsonSerializer.Serialize(new ErrorResponse("nope"), JsonWeb));

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "abc"));
        cut.WaitForAssertion(() => Assert.Contains("Raid Night", cut.Markup));

        // Blazor reuses the component for a new slug on the same route; calendar A must not
        // linger under calendar B's URL when B's request fails.
        cut.Render(p => p.Add(x => x.Slug, "other"));

        cut.WaitForAssertion(() => Assert.Contains("Couldn't load this calendar", cut.Markup));
        Assert.DoesNotContain("Raid Night", cut.Markup);
    }

    [Fact]
    public async Task A_slow_response_for_an_earlier_slug_never_overwrites_the_current_calendar()
    {
        var handler = UseApi();
        var slowFirst = new TaskCompletionSource();
        handler.Respond = req => (HttpStatusCode.OK, JsonSerializer.Serialize(
            req.RequestUri!.AbsolutePath.EndsWith("/abc")
                ? SampleMonth()
                : SampleMonth() with { GuildName = "Other Guild" }, JsonWeb));
        handler.Delay = req => req.RequestUri!.AbsolutePath.EndsWith("/abc") ? slowFirst.Task : Task.CompletedTask;

        var cut = Render<PublicCalendar>(p => p.Add(x => x.Slug, "abc"));   // request A: pending
        cut.Render(p => p.Add(x => x.Slug, "other"));                        // request B: completes first
        cut.WaitForAssertion(() => Assert.Contains("Other Guild", cut.Markup));

        slowFirst.SetResult();                                                // A finishes last…
        await Task.Delay(50);
        cut.WaitForAssertion(() => Assert.Contains("Other Guild", cut.Markup)); // …and is ignored
        Assert.DoesNotContain("Test Guild", cut.Markup);
    }

    [Fact]
    public void Settings_card_clears_the_previous_guilds_link_before_the_next_guild_loads()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = UseApi();
        var now = DateTimeOffset.UtcNow;
        var secondGuildGate = new TaskCompletionSource();
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/guilds/1/public-calendar" => (HttpStatusCode.OK, JsonSerializer.Serialize(new PublicCalendarSettingsDto(true, "guild1", "/c/guild1", null), JsonWeb)),
            "/guilds/2/public-calendar" => (HttpStatusCode.OK, JsonSerializer.Serialize(new PublicCalendarSettingsDto(false, null, null, null), JsonWeb)),
            var p when p.EndsWith("/settings") => (HttpStatusCode.OK, JsonSerializer.Serialize(new GuildSettingsDto("UTC", 5), JsonWeb)),
            "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(new WebGuildListResponse(now, [new WebGuildDto(1, "G", null, true), new WebGuildDto(2, "H", null, true)]), JsonWeb)),
            var p when p.EndsWith("/feed-token") => (HttpStatusCode.OK, JsonSerializer.Serialize(new FeedTokenDto("tok", "/feeds/tok.ics"), JsonWeb)),
            _ => (HttpStatusCode.OK, "[]"),
        };
        handler.Delay = req => req.RequestUri!.AbsolutePath == "/guilds/2/public-calendar" ? secondGuildGate.Task : Task.CompletedTask;

        var cut = Render<GuildSettings>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("/c/guild1", cut.Markup));

        cut.Render(p => p.Add(x => x.GuildId, 2L)); // guild 2's public-calendar read is held open

        // Guild 1's link (a credential) must be gone immediately, not after guild 2's load completes.
        cut.WaitForAssertion(() => Assert.DoesNotContain("/c/guild1", cut.Markup));
        secondGuildGate.SetResult();
        cut.WaitForAssertion(() => Assert.Contains("gs-public-cal", cut.Markup));
        Assert.DoesNotContain("/c/guild1", cut.Markup);
    }

    [Fact]
    public void A_regenerate_that_finishes_after_a_guild_switch_never_installs_the_old_guilds_link()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = UseApi();
        var now = DateTimeOffset.UtcNow;
        var putGate = new TaskCompletionSource();
        handler.Respond = req => (req.Method == HttpMethod.Put, req.RequestUri!.AbsolutePath) switch
        {
            (true, "/guilds/1/public-calendar") => (HttpStatusCode.OK, JsonSerializer.Serialize(new PublicCalendarSettingsDto(true, "regenerated1", "/c/regenerated1", null), JsonWeb)),
            (_, "/guilds/1/public-calendar") => (HttpStatusCode.OK, JsonSerializer.Serialize(new PublicCalendarSettingsDto(true, "guild1", "/c/guild1", null), JsonWeb)),
            (_, "/guilds/2/public-calendar") => (HttpStatusCode.OK, JsonSerializer.Serialize(new PublicCalendarSettingsDto(false, null, null, null), JsonWeb)),
            (_, var p) when p.EndsWith("/settings") => (HttpStatusCode.OK, JsonSerializer.Serialize(new GuildSettingsDto("UTC", 5), JsonWeb)),
            (_, "/me/guilds") => (HttpStatusCode.OK, JsonSerializer.Serialize(new WebGuildListResponse(now, [new WebGuildDto(1, "G", null, true), new WebGuildDto(2, "H", null, true)]), JsonWeb)),
            (_, var p) when p.EndsWith("/feed-token") => (HttpStatusCode.OK, JsonSerializer.Serialize(new FeedTokenDto("tok", "/feeds/tok.ics"), JsonWeb)),
            _ => (HttpStatusCode.OK, "[]"),
        };
        handler.Delay = req => req.Method == HttpMethod.Put ? putGate.Task : Task.CompletedTask;

        var cut = Render<GuildSettings>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("/c/guild1", cut.Markup));

        // Start a regenerate on guild 1 (held open), then move the page to guild 2.
        cut.FindAll("button").First(b => b.TextContent.Contains("New link")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Generate new link")).Click();
        cut.Render(p => p.Add(x => x.GuildId, 2L));
        cut.WaitForAssertion(() => Assert.Contains("gs-public-cal", cut.Markup));

        putGate.SetResult(); // guild 1's regenerate completes late…

        cut.WaitForAssertion(() => Assert.DoesNotContain("/c/regenerated1", cut.Markup)); // …and is discarded
        Assert.DoesNotContain("/c/guild1", cut.Markup);
    }

    [Fact]
    public void Manager_controls_are_withheld_while_the_next_guild_is_still_loading()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = UseApi();
        var now = DateTimeOffset.UtcNow;
        var guild2Gate = new TaskCompletionSource();
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/public-calendar") => (HttpStatusCode.OK, JsonSerializer.Serialize(new PublicCalendarSettingsDto(false, null, null, null), JsonWeb)),
            "/guilds/1/settings" => (HttpStatusCode.OK, JsonSerializer.Serialize(new GuildSettingsDto("America/Chicago", 5, true), JsonWeb)),
            "/guilds/2/settings" => (HttpStatusCode.OK, JsonSerializer.Serialize(new GuildSettingsDto("Europe/Berlin", 5), JsonWeb)),
            // Manager of guild 1 only.
            "/me/guilds" => (HttpStatusCode.OK, JsonSerializer.Serialize(new WebGuildListResponse(now, [new WebGuildDto(1, "G", null, true), new WebGuildDto(2, "H", null, false)]), JsonWeb)),
            var p when p.EndsWith("/feed-token") => (HttpStatusCode.OK, JsonSerializer.Serialize(new FeedTokenDto("tok", "/feeds/tok.ics"), JsonWeb)),
            _ => (HttpStatusCode.OK, "[]"),
        };
        handler.Delay = req => req.RequestUri!.AbsolutePath == "/guilds/2/settings" ? guild2Gate.Task : Task.CompletedTask;

        var cut = Render<GuildSettings>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => cut.Find("#gs-public-cal")); // manager switch for guild 1

        cut.Render(p => p.Add(x => x.GuildId, 2L)); // guild 2's first read is held open

        // Guild 1's manager role — and its timezone / native-events state — must not leak into
        // guild 2's loading window, and loading is shown as loading, not as a member view…
        cut.WaitForAssertion(() => Assert.Contains("Loading server settings", cut.Markup));
        Assert.Empty(cut.FindAll("#gs-public-cal"));
        Assert.Empty(cut.FindAll("#gs-native"));
        Assert.DoesNotContain("America/Chicago", cut.Markup);
        Assert.DoesNotContain("Read-only", cut.Markup);
        guild2Gate.SetResult();

        // …and only once guild 2's OWN data is in does the member (non-manager) view render.
        cut.WaitForAssertion(() => Assert.Contains("Europe/Berlin", cut.Markup));
        Assert.Contains("Read-only", cut.Markup);
        Assert.Empty(cut.FindAll("#gs-public-cal"));
        Assert.DoesNotContain("Loading server settings", cut.Markup);
    }

    private static PublicCalendarDto SampleMonth() =>
        new("Test Guild", "America/Chicago", 2026, 9,
        [
            new PublicCalendarEventDto(
                "Raid Night", new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero), new DateTime(2026, 9, 5, 20, 0, 0),
                90, "Voice", "events", "https://discord.com/channels/1/2/3", Projected: false),
            new PublicCalendarEventDto(
                "Raid Night", new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero), new DateTime(2026, 9, 12, 20, 0, 0),
                90, "Voice", "events", null, Projected: true),
        ],
        EarliestMonth: new DateTime(2024, 8, 1),
        LatestMonth: new DateTime(2028, 8, 1));

    private RoutingHandler UseApi()
    {
        var handler = new RoutingHandler();
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return handler;
    }

    /// <summary>Answers each request via <see cref="Respond"/> (status + JSON body) and records
    /// the last request body sent.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public Func<HttpRequestMessage, (HttpStatusCode Status, string? Json)> Respond { get; set; } =
            _ => (HttpStatusCode.OK, "{}");

        /// <summary>Optional per-request hold, so tests can control completion order.</summary>
        public Func<HttpRequestMessage, Task>? Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            if (Delay is not null)
            {
                await Delay(request);
            }

            var (status, json) = Respond(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json ?? "{\"error\":\"nope\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
