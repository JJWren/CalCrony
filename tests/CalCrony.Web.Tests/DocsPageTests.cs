using Bunit;
using CalCrony.Web.Pages;

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

    /// <summary>The "Features in depth" list entry whose bold lead is <paramref name="name"/> —
    /// scoped to the lists after the <c>#features</c> heading, since the first-steps guidance
    /// above uses some of the same leads (e.g. "Attendee roles").</summary>
    private static AngleSharp.Dom.IElement Entry(IRenderedComponent<Docs> cut, string name) =>
        cut.FindAll("#features ~ ul > li").Single(li => li.QuerySelector("b")?.TextContent.Trim() == name);
}
