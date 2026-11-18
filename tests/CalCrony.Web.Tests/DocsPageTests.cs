using Bunit;
using CalCrony.Web.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>The user docs page is the single long-form feature reference (the README links to it),
/// so its structure is pinned: the six feature groups, and the newest features by name.</summary>
public class DocsPageTests : TestContext
{
    [Fact]
    public void Docs_page_carries_the_six_feature_groups_in_depth()
    {
        var cut = Render<Docs>();

        // The section heading is the anchor the README's Features section links to…
        Assert.Equal("Features in depth", cut.Find("h2#features").TextContent.Trim());

        // …and the six groups are real headings, not just words in the prose.
        var headings = cut.FindAll("h3").Select(h => h.TextContent.Trim()).ToList();
        foreach (var group in new[] { "Events & RSVPs", "Schedules", "Reminders & calendars", "Polls", "Roles & access", "Web app & admin" })
        {
            Assert.Contains(group, headings);
        }
    }

    [Fact]
    public void A_fresh_load_with_a_fragment_scrolls_to_it_after_the_first_render()
    {
        // The README links straight to /docs#features; the browser's own fragment scroll runs
        // before the app has rendered the heading, so the page asks for it once it has.
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/docs#features");
        JSInterop.SetupVoid("calcronyPage.scrollToFragment", "features").SetVoidResult();

        var cut = Render<Docs>();

        cut.WaitForAssertion(() =>
        {
            var call = JSInterop.VerifyInvoke("calcronyPage.scrollToFragment");
            Assert.Equal("features", call.Arguments[0]);
        });
    }

    [Fact]
    public void A_fresh_load_without_a_fragment_asks_for_no_scroll()
    {
        Render<Docs>();

        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void In_page_links_to_the_reference_stay_on_the_docs_route()
    {
        var cut = Render<Docs>();

        // A fragment-only href ("#features") resolves against <base href="/"> and the router
        // would land on the home page; every in-page link must carry the /docs path.
        var links = cut.FindAll("a").Where(a => a.TextContent.Contains("Features in depth")).ToList();
        Assert.NotEmpty(links);
        Assert.All(links, a => Assert.Equal("/docs#features", a.GetAttribute("href")));
        Assert.Empty(cut.FindAll("a[href^='#']"));
    }

    [Fact]
    public void Docs_page_names_the_rsvp_v2_features_and_their_permissions()
    {
        var cut = Render<Docs>();

        // Each permission is asserted inside its own feature entry (the same names also occur in
        // the first-steps guidance above the reference, so a page-wide search would prove nothing).
        Assert.Contains("multi-rsvp:true", Entry(cut, "Multiple RSVPs per member").TextContent);
        Assert.Contains("fails closed", Entry(cut, "Role-restricted signup").TextContent);
        Assert.Contains("Manage Roles", Entry(cut, "Attendee roles").TextContent);
        Assert.Contains("Manage Events", Entry(cut, "Discord native events").TextContent);
        Assert.Contains("Create Public Threads", Entry(cut, "Event threads").TextContent);
    }

    [Fact]
    public void Docs_page_explains_free_slots_inside_the_availability_entry()
    {
        var cut = Render<Docs>();

        // Where it lives, who may use it and what it assumes, inside the feature's own entry.
        var entry = Flat(Entry(cut, "Google Calendar availability").TextContent);
        Assert.Contains("/availability slots", entry);
        Assert.Contains("Discord only", entry);
        Assert.Contains("any member can run it", entry);
        Assert.Contains("everyone holding a role", entry);
        Assert.Contains("aren't counted", entry);

        // Who can be picked: yourself always, anyone else once they have saved their hours.
        Assert.Contains("you can always pick yourself as who", entry);
        Assert.Contains("another member once they have saved their available hours", entry);

        // What a slot rests on: each member's own hours, and the default until they save any.
        Assert.Contains("inside each member's available hours (8 AM to 11 PM until they save their own)", entry);
        Assert.Contains("counted on saved hours alone", entry);

        // …and on the member's own CalCrony events in the server: whose events count, which RSVPs
        // do, what a repeating event adds, and that events never make a member counted.
        Assert.Contains("with nothing on a linked calendar and none of their CalCrony events in this server", entry);
        Assert.Contains(
            "An event counts for the member who created it and for members holding a seat on its attending option (Going by default)",
            entry);
        Assert.Contains("a waitlisted RSVP or one on another option doesn't count", entry);
        Assert.Contains("whoever created the event stays busy even if they RSVP elsewhere", entry);
        Assert.Contains("A repeating event's later dates count for the member who created it", entry);
        Assert.Contains("aren't counted, whatever events they have", entry);

        // The other availability commands are as they were, and the entry says so.
        Assert.Contains(
            "Only slots counts CalCrony events: /availability role, /availability event and the grid show linked calendars alone",
            entry);

        // …and the command reference lists the subcommand beside the two it joins.
        Assert.Contains(cut.FindAll("table td code"), code => code.TextContent == "/availability role|event|slots");
    }

    [Fact]
    public void Docs_page_says_where_a_scheduled_ping_goes_and_whom_it_mentions()
    {
        var cut = Render<Docs>();

        var entry = Flat(Entry(cut, "Scheduled pings and reminders").TextContent);
        Assert.Contains("(the event's creator or a server manager, on both sides)", entry);
        Assert.Contains("A ping's mention and its channel are set in Discord", entry);
        Assert.Contains("a ping added here goes to the event's channel without a mention", entry);
        Assert.Contains(
            "Only that one mention notifies anyone — a mention typed into a title, a message or a reminder is shown as text.",
            entry);
    }

    [Fact]
    public void Docs_page_explains_available_hours_in_an_entry_of_its_own()
    {
        var cut = Render<Docs>();

        // Where it lives, who may set it, what applies until it is set, and what saving it allows.
        var entry = Flat(Entry(cut, "Available hours").TextContent);
        Assert.Contains("only you can set yours", entry);
        Assert.Contains("/settings available-hours set days:weekdays from:6pm to:11pm", entry);
        Assert.Contains("My settings here", entry);
        Assert.Contains("until you save any, 8 AM to 11 PM every day is assumed", entry);
        Assert.Contains("even without a linked calendar", entry);
        Assert.Contains(
            "Saving hours also lets members of the servers you share with CalCrony list your free slots by name, " +
            "which shows roughly the hours you keep and when you're busy inside them: " +
            "with your CalCrony events in that server and, if your calendar is linked, with what's on your calendar; " +
            "reset takes that back.",
            entry);

        // A first set in Discord starts from an empty week, which the example alone would not tell.
        Assert.Contains("The first set leaves the days you don't name off", entry);
        Assert.Contains("off marks the days you name as not available", entry);

        // The entry sits beside the availability one, and the command reference names the group.
        Assert.Contains(cut.FindAll("table td code"), code => code.TextContent == "available-hours set|off|reset");
    }

    /// <summary>Text with every run of whitespace as one space, so an assertion does not depend on
    /// where the markup breaks its lines.</summary>
    private static string Flat(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The "Features in depth" list entry whose bold lead is <paramref name="name"/> —
    /// scoped to the lists after the <c>#features</c> heading, since the first-steps guidance
    /// above uses some of the same leads (e.g. "Attendee roles").</summary>
    private static AngleSharp.Dom.IElement Entry(IRenderedComponent<Docs> cut, string name) =>
        cut.FindAll("#features ~ ul > li").Single(li => li.QuerySelector("b")?.TextContent.Trim() == name);
}
