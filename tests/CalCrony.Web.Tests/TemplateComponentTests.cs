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

public class TemplateComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Event_form_prefills_from_url_template_and_submit_carries_template_id()
    {
        var handler = UseApi();
        var template = SampleTemplate("Raid Night", notificationCount: 1);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/templates") => JsonSerializer.Serialize(new List<EventTemplateDto> { template }, JsonWeb),
            _ => "{}",
        };

        var nav = Services.GetRequiredService<Bunit.TestDoubles.FakeNavigationManager>();
        nav.NavigateTo($"/app/guilds/1/events/new?template={template.Id}");
        var cut = RenderComponent<EventForm>(p => p.Add(x => x.GuildId, 1L));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Raid Night Title", cut.Find("#ev-title").GetAttribute("value"));
            Assert.Contains("Includes 1 reminder", cut.Markup);
        });

        cut.Find("#ev-when").Change("friday 6pm");
        handler.NextJson = JsonSerializer.Serialize(SampleEvent(), JsonWeb);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create event")).Click();

        var body = JsonSerializer.Deserialize<CreateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal(template.Id, body.TemplateId);
        Assert.Equal("Raid Night Title", body.Title);
        Assert.False(body.NoRecurrence); // template has no rule; form shows Doesn't repeat but that's not a suppression
    }

    [Fact]
    public void Templates_page_lists_and_deletes_with_confirm()
    {
        var handler = UseApi();
        SetupAuth();
        var template = SampleTemplate("Movies", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week));
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/templates") => JsonSerializer.Serialize(new List<EventTemplateDto> { template }, JsonWeb),
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(1, "G", null, true)]), JsonWeb),
            _ => "{}",
        };

        var cut = RenderComponent<GuildTemplates>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Movies", cut.Markup);
            Assert.Contains("🔁 repeats", cut.Markup);
            Assert.Contains($"/app/guilds/1/events/new?template={template.Id}", cut.Markup);
        });

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete").Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Really delete?")).Click();
        Assert.Equal($"/templates/{template.Id}", handler.LastDeletePath);
    }

    [Fact]
    public void Templates_page_edit_prefills_and_sends_the_full_patch()
    {
        var handler = UseApi();
        SetupAuth();
        var template = SampleTemplate("Editable", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), notificationCount: 1);
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/templates") => JsonSerializer.Serialize(new List<EventTemplateDto> { template }, JsonWeb),
            "/me/guilds" => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(1, "G", null, true)]), JsonWeb),
            _ => JsonSerializer.Serialize(template, JsonWeb),
        };

        var cut = RenderComponent<GuildTemplates>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Editable", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Assert.Equal("Editable", cut.Find("#tpl-name").GetAttribute("value"));

        cut.Find("#tpl-title").Change("Fresh Title");
        cut.Find("#tpl-repeat").Change("None"); // clearing the rule
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();

        cut.WaitForAssertion(() =>
        {
            var body = JsonSerializer.Deserialize<UpdateTemplateRequest>(handler.LastBody!, JsonWeb)!;
            Assert.Equal("Fresh Title", body.Title);
            Assert.True(body.ClearRecurrence);
            Assert.Null(body.Recurrence);
            Assert.Single(body.Notifications!); // existing spec carried into the replacement set
        });
    }

    [Fact]
    public void Event_detail_save_as_template_posts_the_request()
    {
        var handler = UseApi();
        SetupAuth();
        var ev = SampleEvent();
        var now = DateTimeOffset.UtcNow;
        handler.JsonFor = req => (req.Method, req.RequestUri!.AbsolutePath) switch
        {
            (var m, var p) when m == HttpMethod.Post && p.EndsWith("/templates") =>
                JsonSerializer.Serialize(SampleTemplate("Saved"), JsonWeb),
            (_, var p) when p.EndsWith("/availability") =>
                JsonSerializer.Serialize(new AvailabilityResponse(now, now.AddHours(1), []), JsonWeb),
            (_, var p) when p.EndsWith("/notifications") => "[]",
            (_, "/me/guilds") => JsonSerializer.Serialize(
                new WebGuildListResponse(now, [new WebGuildDto(ev.GuildId, "G", null, false)]), JsonWeb),
            _ => JsonSerializer.Serialize(ev, JsonWeb),
        };

        var cut = RenderComponent<EventDetail>(p => p.Add(x => x.EventId, ev.Id));
        cut.WaitForAssertion(() => Assert.Contains("Save as template", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Save as template")).Click();
        cut.Find("#tmpl-name").Change("My Setup");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        var body = JsonSerializer.Deserialize<SaveTemplateRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal("My Setup", body.Name);
        Assert.Equal(ev.Id, body.EventId);
        cut.WaitForAssertion(() => Assert.Contains("Saved template", cut.Markup));
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

    private static EventTemplateDto SampleTemplate(
        string name, RecurrenceRuleDto? recurrence = null, int notificationCount = 0) => new(
        Guid.NewGuid(), 1, 2, name, $"{name} Title", "Desc", 90, "Lounge", null, recurrence,
        [.. Enumerable.Range(0, notificationCount).Select(i => new TemplateNotificationDto(30, null, null, null))],
        DateTimeOffset.UtcNow);

    private static EventDto SampleEvent()
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled, [going], []);
    }

    /// <summary>Routes responses by request; records the last request body and delete path.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public string? LastDeletePath { get; private set; }

        public string? NextJson { get; set; }

        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            if (request.Method == HttpMethod.Delete)
            {
                LastDeletePath = request.RequestUri!.AbsolutePath;
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
