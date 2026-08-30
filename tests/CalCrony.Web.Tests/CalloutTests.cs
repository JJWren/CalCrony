using Bunit;
using CalCrony.Web.Components;

namespace CalCrony.Web.Tests;

/// <summary>The design-system callout (issue #93): four semantic variants, each with its own
/// stroke icon and hue class; the body renders arbitrary child content.</summary>
public class CalloutTests : TestContext
{
    [Theory]
    [InlineData(CalloutVariant.Note, "callout-note", "r=\"6.9\"")]
    [InlineData(CalloutVariant.Important, "callout-important", "M10 2.8 L17.2 10")]
    [InlineData(CalloutVariant.Warning, "callout-warning", "M10 3.2 L17.8 16.2")]
    [InlineData(CalloutVariant.Caution, "callout-caution", "M6.7 2.6 H13.3")]
    public void Variant_picks_hue_class_label_and_icon(CalloutVariant variant, string hueClass, string iconSignature)
    {
        var cut = Render<Callout>(p => p
            .Add(c => c.Variant, variant)
            .AddChildContent("<b>body text</b>"));

        var box = cut.Find("div.callout");
        Assert.Contains(hueClass, box.ClassName);
        Assert.Equal(variant.ToString(), cut.Find(".co-head span").TextContent);
        Assert.Contains(iconSignature, cut.Markup);
        Assert.Contains("<b>body text</b>", cut.Find(".co-body").InnerHtml);
        // Stroke SVGs, never emoji — the icon is aria-hidden decoration beside the text label.
        Assert.Equal("true", cut.Find("svg").GetAttribute("aria-hidden"));
    }
}
