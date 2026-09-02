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

        Assert.Contains("Features in depth", cut.Markup);
        foreach (var group in new[] { "Events &amp; RSVPs", "Schedules", "Reminders &amp; calendars", "Polls", "Roles &amp; access", "Web app &amp; admin" })
        {
            Assert.Contains(group, cut.Markup);
        }

        // The anchor the README's Features section links to.
        Assert.NotNull(cut.Find("#features"));
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
