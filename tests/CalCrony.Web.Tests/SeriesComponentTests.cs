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

public class SeriesComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Create_form_sends_recurrence_rule_and_count()
    {
        var handler = UseApi();

        var cut = RenderComponent<EventForm>(p => p.Add(x => x.GuildId, 1));
        cut.Find("#ev-title").Change("Weekly sync");
        cut.Find("#ev-when").Change("friday 6pm");
        cut.Find("#ev-repeat").Change("Weekly");
        cut.Find("#ev-interval").Change("2");
        cut.Find("#ev-ends-count").Change(true);
        cut.FindAll("input[aria-label='Repeat count']").Single().Change("8");

        handler.NextJson = JsonSerializer.Serialize(SampleEvent(recurrenceSummary: "Repeats every 2 weeks on Friday"), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.NotNull(body.Recurrence);
        Assert.Equal(RecurrenceUnit.Week, body.Recurrence!.Unit);
        Assert.Equal(2, body.Recurrence.Interval);
        Assert.Equal(8, body.RepeatCount);
        Assert.Null(body.RepeatUntilText);
    }

    [Fact]
    public void Create_form_sends_until_text_in_until_mode()
    {
        var handler = UseApi();

        var cut = RenderComponent<EventForm>(p => p.Add(x => x.GuildId, 1));
        cut.Find("#ev-title").Change("Book club");
        cut.Find("#ev-when").Change("sunday 3pm");
        cut.Find("#ev-repeat").Change("MonthlySameDate");
        cut.Find("#ev-ends-until").Change(true);
        // The until preview fires a parse request; give it a canned response first.
        handler.NextJson = JsonSerializer.Serialize(
            new ParseDateTimeResponse(DateTimeOffset.UtcNow.AddMonths(3), 0, "UTC"), JsonWeb);
        cut.FindAll("input[aria-label='Repeat until']").Single().Change("in 3 months");

        handler.NextJson = JsonSerializer.Serialize(SampleEvent(recurrenceSummary: "Repeats monthly on day 1"), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal(MonthlyMode.DayOfMonth, body.Recurrence!.MonthlyMode);
        Assert.Equal("in 3 months", body.RepeatUntilText);
        Assert.Null(body.RepeatCount);
    }

    [Fact]
    public void Editing_a_live_series_occurrence_asks_for_scope_and_sends_it()
    {
        var handler = UseApi();
        var ev = SampleEvent(recurrenceSummary: "Repeats weekly on Friday");
        handler.NextJson = JsonSerializer.Serialize(ev, JsonWeb);

        var cut = RenderComponent<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));

        // Save swaps into the scope ask instead of submitting.
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();
        var seriesButton = cut.FindAll("button").First(b => b.TextContent.Contains("Whole series"));

        handler.NextJson = JsonSerializer.Serialize(ev, JsonWeb);
        seriesButton.Click();

        var body = JsonSerializer.Deserialize<UpdateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal(EditScope.Series, body.Scope);
    }

    [Fact]
    public void Detail_page_skip_confirms_then_hits_the_skip_endpoint()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(recurrenceSummary: "Repeats weekly on Friday");
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/skip") =>
                JsonSerializer.Serialize(new SkipOccurrenceResponse(null, SampleSeries(ev)), JsonWeb),
            var p when p.EndsWith("/availability") =>
                JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb),
            var p when p.EndsWith("/notifications") => "[]",
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, "G", null, true)]), JsonWeb),
            _ => JsonSerializer.Serialize(ev, JsonWeb),
        };

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        cut.WaitForAssertion(() => Assert.Contains("Repeats weekly on Friday", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Skip occurrence")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Really skip?")).Click();
        Assert.Equal($"/events/{ev.Id}/skip", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Detail_edit_schedule_prefills_and_sends_explicit_update()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(recurrenceSummary: "Repeats weekly on Friday");
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.StartsWith("/series/") =>
                JsonSerializer.Serialize(SampleSeries(ev) with { Ended = false, MaxOccurrences = 8, OccurrenceCount = 3 }, JsonWeb),
            var p when p.EndsWith("/availability") =>
                JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb),
            var p when p.EndsWith("/notifications") => "[]",
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, "G", null, true)]), JsonWeb),
            _ => JsonSerializer.Serialize(ev, JsonWeb),
        };

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        cut.WaitForAssertion(() => Assert.Contains("Edit schedule", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit schedule").Click();
        cut.WaitForAssertion(() => cut.Find("#se-repeat"));

        // Prefill: weekly, count mode with the stored count.
        Assert.Equal("Weekly", cut.Find("#se-repeat").GetAttribute("value"));
        Assert.True(cut.Find("#se-ends-count").HasAttribute("checked"));
        Assert.Equal("8", cut.Find("#se-count").GetAttribute("value"));

        cut.Find("#se-repeat").Change("MonthlyNthWeekday");
        cut.Find("#se-ends-never").Change(true);
        cut.FindAll("button").First(b => b.TextContent.Contains("Save schedule")).Click();

        Assert.Equal($"/series/{ev.SeriesId}", handler.PatchRequestPath);
        var body = JsonSerializer.Deserialize<UpdateSeriesRequest>(handler.PatchBody!, JsonWeb)!;
        Assert.Equal(RecurrenceUnit.Month, body.Unit);
        Assert.Equal(MonthlyMode.NthWeekday, body.MonthlyMode);
        Assert.Equal(SeriesEndChoice.Never, body.End);
    }

    [Fact]
    public void Schedule_editor_doesnt_repeat_stops_the_series()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent(recurrenceSummary: "Repeats weekly on Friday");
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/stop") =>
                JsonSerializer.Serialize(SampleSeries(ev), JsonWeb),
            var p when p.StartsWith("/series/") =>
                JsonSerializer.Serialize(SampleSeries(ev) with { Ended = false }, JsonWeb),
            var p when p.EndsWith("/availability") =>
                JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb),
            var p when p.EndsWith("/notifications") => "[]",
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, "G", null, true)]), JsonWeb),
            _ => JsonSerializer.Serialize(ev, JsonWeb),
        };

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        cut.WaitForAssertion(() => Assert.Contains("Edit schedule", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit schedule").Click();
        cut.WaitForAssertion(() => cut.Find("#se-repeat"));

        cut.Find("#se-repeat").Change("None");
        // The card's submit (btn-primary), not the manage cluster's outline "Stop repeating".
        cut.FindAll("button.btn-primary.btn-sm").First(b => b.TextContent.Contains("Stop repeating")).Click();

        Assert.EndsWith($"/series/{ev.SeriesId}/stop", handler.LastPostPath);
    }

    [Fact]
    public void Detail_ended_series_shows_resume_affordance_for_managers_only()
    {
        var handler = UseApi();
        SetupAuth();
        // Ended series: SeriesId present, summary null; creator id 2 ≠ viewer, so access
        // depends on the guild CanManage flag.
        var ev = SampleEvent() with { SeriesId = Guid.NewGuid() };
        var now = DateTimeOffset.UtcNow;
        string Router(HttpRequestMessage req, bool canManage) => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/availability") =>
                JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb),
            var p when p.EndsWith("/notifications") => "[]",
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, "G", null, canManage)]), JsonWeb),
            _ => JsonSerializer.Serialize(ev, JsonWeb),
        };

        handler.JsonFor = req => Router(req, canManage: true);
        var manager = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        manager.WaitForAssertion(() =>
        {
            Assert.Contains("repeating series that has ended", manager.Markup);
            Assert.Contains("Edit schedule &amp; resume", manager.Markup);
        });

        handler.JsonFor = req => Router(req, canManage: false);
        var member = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        member.WaitForAssertion(() =>
        {
            Assert.Contains("repeating series that has ended", member.Markup);
            Assert.DoesNotContain("resume", member.Markup);
        });
    }

    [Fact]
    public void Event_card_shows_repeat_badge_only_for_series()
    {
        UseApi();
        var plain = RenderComponent<EventCard>(p => p.Add(x => x.Event, SampleEvent()));
        Assert.DoesNotContain("🔁", plain.Markup);

        var repeating = RenderComponent<EventCard>(p => p.Add(
            x => x.Event, SampleEvent(recurrenceSummary: "Repeats daily")));
        Assert.Contains("🔁", repeating.Markup);
        Assert.Contains("Repeats daily", repeating.Markup); // title attribute carries the summary
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

    private static EventDto SampleEvent(string? recurrenceSummary = null)
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled, [going], [],
            recurrenceSummary is null ? null : Guid.NewGuid(), recurrenceSummary);
    }

    private static SeriesDto SampleSeries(EventDto ev) => new(
        ev.SeriesId!.Value, ev.GuildId, ev.CreatorId, ev.Title,
        RecurrenceUnit.Week, 1, MonthlyMode.DayOfMonth, "UTC", "2026-07-17", "18:00",
        null, null, 2, true, null, null, 60, ev.ChannelId, null, null,
        "Repeats weekly on Friday", []);

    /// <summary>Records the last request; answers with NextJson (one-shot per set) or JsonFor.
    /// PATCH requests are additionally captured separately so tests can assert them even when a
    /// follow-up GET overwrites LastRequest.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public string? PatchRequestPath { get; private set; }

        public string? PatchBody { get; private set; }

        public string? LastPostPath { get; private set; }

        public string? NextJson { get; set; }

        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (request.Method == HttpMethod.Patch)
            {
                PatchRequestPath = request.RequestUri!.AbsolutePath;
                PatchBody = LastBody;
            }

            if (request.Method == HttpMethod.Post)
            {
                LastPostPath = request.RequestUri!.AbsolutePath;
            }

            var json = NextJson ?? JsonFor?.Invoke(request) ?? "{}";
            NextJson = null; // actually one-shot, so an unexpected extra request can't reuse it
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
