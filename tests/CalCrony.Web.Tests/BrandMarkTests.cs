using Bunit;
using CalCrony.Web.Components;

namespace CalCrony.Web.Tests;

/// <summary>The d20 brand mark from the design canvas: currentColor strokes under the accent
/// class, five struts at navbar sizes, all nine for the full (favicon) variant.</summary>
public class BrandMarkTests : TestContext
{
    // Present only in the full nine-strut variant (an upper connector segment).
    private const string FullOnlySegment = "M16.93 6 L10 5.6";

    [Fact]
    public void Default_mark_is_the_simplified_navbar_variant()
    {
        var cut = RenderComponent<BrandMark>();

        var svg = cut.Find("svg.brand-mark");
        Assert.Equal("20", svg.GetAttribute("width"));
        Assert.Equal("currentColor", svg.GetAttribute("stroke"));
        Assert.Equal(3, cut.FindAll("path").Count);
        Assert.DoesNotContain(FullOnlySegment, cut.Markup);
    }

    [Fact]
    public void Full_mark_renders_all_struts_at_the_requested_size()
    {
        var cut = RenderComponent<BrandMark>(p => p
            .Add(m => m.Full, true)
            .Add(m => m.Size, 32));

        Assert.Equal("32", cut.Find("svg.brand-mark").GetAttribute("width"));
        Assert.Contains(FullOnlySegment, cut.Markup);
    }
}
