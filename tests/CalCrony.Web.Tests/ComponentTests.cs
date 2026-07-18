using Bunit;
using CalCrony.Contracts;
using CalCrony.Web.Components;
using CalCrony.Web.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

public class ComponentTests : TestContext
{
    [Fact]
    public void Landing_carries_the_exact_bot_invite_url()
    {
        var cut = RenderComponent<Landing>();

        // String regression on the locked invite URL — permissions/scopes must not drift.
        Assert.Contains(
            "https://discord.com/oauth2/authorize?client_id=1527749302443835532&permissions=84992&integration_type=0&scope=bot+applications.commands",
            cut.Markup);
    }

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
