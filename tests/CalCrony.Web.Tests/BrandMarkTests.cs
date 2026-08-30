using Bunit;
using CalCrony.Web.Components;

namespace CalCrony.Web.Tests;

/// <summary>The d20 brand mark from the design canvas: currentColor strokes under the accent
/// class, five struts at navbar sizes, all nine for the full (favicon) variant.</summary>
public class BrandMarkTests : TestContext
{
    // The exact strut paths from the design canvas — five segments for navbar lockups,
    // nine for the full (favicon) mark.
    private const string NavbarStruts =
        "M10 2 L10 5.6 M16.93 6 L14.8 13 M3.07 6 L5.2 13 M10 18 L14.8 13 M10 18 L5.2 13";

    private const string FullStruts =
        "M10 2 L10 5.6 M16.93 6 L14.8 13 M3.07 6 L5.2 13 M16.93 6 L10 5.6 M3.07 6 L10 5.6 "
        + "M10 18 L14.8 13 M10 18 L5.2 13 M16.93 14 L14.8 13 M3.07 14 L5.2 13";

    [Fact]
    public void Default_mark_is_the_simplified_navbar_variant()
    {
        var cut = RenderComponent<BrandMark>();

        var svg = cut.Find("svg.brand-mark");
        Assert.Equal("20", svg.GetAttribute("width"));
        Assert.Equal("20", svg.GetAttribute("height"));
        Assert.Equal("currentColor", svg.GetAttribute("stroke"));
        var paths = cut.FindAll("path");
        Assert.Equal(3, paths.Count);
        Assert.Equal(NavbarStruts, paths[2].GetAttribute("d"));
    }

    [Fact]
    public void Full_mark_renders_all_struts_at_the_requested_size()
    {
        var cut = RenderComponent<BrandMark>(p => p
            .Add(m => m.Full, true)
            .Add(m => m.Size, 32));

        var svg = cut.Find("svg.brand-mark");
        Assert.Equal("32", svg.GetAttribute("width"));
        Assert.Equal("32", svg.GetAttribute("height"));
        var paths = cut.FindAll("path");
        Assert.Equal(3, paths.Count);
        Assert.Equal(FullStruts, paths[2].GetAttribute("d"));
    }
}
