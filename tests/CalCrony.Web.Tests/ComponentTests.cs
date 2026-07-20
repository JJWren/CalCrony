using Bunit;
using Bunit.TestDoubles;
using CalCrony.Contracts;
using CalCrony.Web.Auth;
using CalCrony.Web.Components;
using CalCrony.Web.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

public class ComponentTests : TestContext
{
    [Fact]
    public void Landing_carries_the_exact_bot_invite_url_by_default()
    {
        UseConfig();
        var cut = RenderComponent<Landing>();

        // Regression on the locked invite URL — permissions/scopes must not drift, and an
        // unconfigured deployment must advertise the PRODUCTION application. Asserted on the
        // href attribute value, which is encoding-stable.
        Assert.Equal(
            "https://discord.com/oauth2/authorize?client_id=1527749302443835532&permissions=335275969536&integration_type=0&scope=bot+applications.commands",
            cut.Find("a.btn-primary").GetAttribute("href"));
    }

    [Fact]
    public void Landing_names_the_app_and_links_the_legal_pages()
    {
        UseConfig();
        var cut = RenderComponent<Landing>();

        // Google OAuth verification requires the homepage to carry the exact app name, its
        // purpose, and a privacy-policy link on the same domain.
        Assert.Contains("CalCrony", cut.Markup);
        Assert.Contains("free/busy availability", cut.Markup);
        Assert.NotNull(cut.Find("a[href='/privacy']"));
        Assert.NotNull(cut.Find("a[href='/terms']"));
    }

    [Fact]
    public void Legal_pages_render_with_the_limited_use_disclosure()
    {
        var privacy = RenderComponent<Privacy>();
        Assert.Contains("Limited Use", privacy.Markup);
        Assert.Contains("free/busy", privacy.Markup);
        Assert.Contains("Privacy Policy", privacy.Markup);

        var terms = RenderComponent<Terms>();
        Assert.Contains("Terms of Service", terms.Markup);
        Assert.NotNull(terms.Find("a[href='/privacy']"));
    }

    [Fact]
    public void Landing_invite_uses_the_configured_app_id_with_the_same_permissions()
    {
        UseConfig(("Discord:AppId", "999000111"));
        var cut = RenderComponent<Landing>();

        // A test environment's web app must invite the TEST bot, never production's —
        // permissions and scopes stay locked regardless.
        var href = cut.Find("a.btn-primary").GetAttribute("href")!;
        Assert.Contains("client_id=999000111", href);
        Assert.DoesNotContain("1527749302443835532", href);
        Assert.Contains("permissions=335275969536", href);
        Assert.Contains("scope=bot+applications.commands", href);
    }

    private void UseConfig(params (string Key, string Value)[] pairs) =>
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build());

    [Fact]
    public void Theme_toggle_defaults_to_dark()
    {
        JSInterop.Setup<string>("calcronyTheme.getTheme").SetResult("dark");

        var cut = RenderComponent<ThemeToggle>();

        var darkButton = cut.FindAll("button").First(b => b.TextContent.Trim() == "Dark");
        Assert.Contains("active", darkButton.ClassName);
    }

    [Fact]
    public void Availability_grid_renders_all_five_statuses()
    {
        var now = DateTimeOffset.UtcNow;
        var response = new AvailabilityResponse(now, now.AddHours(1),
        [
            new(1, CalendarAvailabilityStatus.Free, []),
            new(2, CalendarAvailabilityStatus.Busy, [new BusyBlockDto(now, now.AddMinutes(30))]),
            new(3, CalendarAvailabilityStatus.NotConnected, []),
            new(4, CalendarAvailabilityStatus.ReconnectRequired, []),
            new(5, CalendarAvailabilityStatus.Error, []),
        ]);

        var cut = RenderComponent<AvailabilityGrid>(p => p.Add(x => x.Response, response));

        Assert.Single(cut.FindAll(".status-free"));
        Assert.Single(cut.FindAll(".status-busy"));
        Assert.Single(cut.FindAll(".status-reconnect"));
        Assert.Equal(2, cut.FindAll(".status-off").Count);
        Assert.Contains("needs to reconnect", cut.Markup);
    }

    [Fact]
    public void Nav_internal_links_never_carry_bootstrap_dismiss()
    {
        // Regression: data-bs-dismiss on anchors makes Bootstrap preventDefault() the click,
        // which silently kills Blazor navigation for every internal link.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ITokenStore, InMemoryTokenStore>();
        Services.AddSingleton<JwtAuthenticationStateProvider>();
        Services.AddScoped(sp => new AuthApiClient(
            new HttpClient { BaseAddress = new Uri("http://localhost") },
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<JwtAuthenticationStateProvider>()));
        this.AddTestAuthorization();

        var cut = RenderComponent<CalCrony.Web.Layout.NavMenu>();

        foreach (var anchor in cut.FindAll("a[href^='/'], a[href='']"))
        {
            Assert.Null(anchor.GetAttribute("data-bs-dismiss"));
        }
    }

    [Fact]
    public void Rsvp_buttons_highlight_the_users_current_choice()
    {
        Services.AddScoped(_ => new CalCrony.Web.Api.CalCronyWebApiClient(new HttpClient { BaseAddress = new Uri("http://localhost") }));
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        var notGoing = new RsvpOptionDto(Guid.NewGuid(), "❌", "Not going", 1, null);
        var ev = new EventDto(
            Guid.NewGuid(), 1, 2, "Test", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled,
            [going, notGoing], [new RsvpDto(42, going.Id)]);

        var cut = RenderComponent<RsvpButtons>(p => p
            .Add(x => x.Event, ev)
            .Add(x => x.UserId, 42));

        var buttons = cut.FindAll("button");
        Assert.Contains("selected", buttons[0].ClassName);
        Assert.DoesNotContain("selected", buttons[1].ClassName ?? "");
    }
}
