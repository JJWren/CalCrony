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

        Assert.Contains("Multiple RSVPs per member", cut.Markup);
        Assert.Contains("multi-rsvp:true", cut.Markup);
        Assert.Contains("Role-restricted signup", cut.Markup);
        Assert.Contains("Attendee roles", cut.Markup);
        Assert.Contains("Manage Roles", cut.Markup);
        Assert.Contains("Manage Events", cut.Markup);
        Assert.Contains("Create Public Threads", cut.Markup);
    }
}
