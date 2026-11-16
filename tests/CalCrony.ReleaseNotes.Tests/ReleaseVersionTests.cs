namespace CalCrony.ReleaseNotes.Tests;

/// <summary>A version is three numbers as release-please writes them, because a summary's file
/// name has to match the version a release gets, character for character.</summary>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.38.0", 0, 38, 0)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("12.345.6789", 12, 345, 6789)]
    public void Three_numbers_are_a_version(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out ReleaseVersion version));

        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v0.38.0")]
    [InlineData("0.38")]
    [InlineData("0.38.0.1")]
    [InlineData("0.38.0-rc.1")]
    [InlineData("0.038.0")]
    [InlineData("0.38.x")]
    [InlineData(" 0.38.0")]
    [InlineData("0.38.0 ")]
    [InlineData("0..0")]
    [InlineData("+1.0.0")]
    [InlineData("1.0.9999999999")]
    [InlineData("١.٠.٠")]
    public void Anything_else_is_not(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("0.37.1", "0.38.0")]
    [InlineData("0.99.99", "1.0.0")]
    [InlineData("0.37.0", "0.37.1")]
    public void Versions_order_by_number_not_by_text(string older, string newer)
    {
        Assert.True(ReleaseVersion.TryParse(older, out ReleaseVersion first));
        Assert.True(ReleaseVersion.TryParse(newer, out ReleaseVersion second));

        Assert.True(first.CompareTo(second) < 0);
        Assert.True(second.CompareTo(first) > 0);
        Assert.Equal(0, first.CompareTo(first));
    }

    [Theory]
    [InlineData("0.38.0.md", true)]
    [InlineData("README.md", false)]
    [InlineData("v0.38.0.md", false)]
    [InlineData("0.38.md", false)]
    [InlineData("0.38.0.MD", false)]
    [InlineData("0.38.0.txt", false)]
    [InlineData("0.38.0", false)]
    public void A_summary_file_is_named_for_its_version(string fileName, bool isSummary)
    {
        Assert.Equal(isSummary, ReleaseSummary.TryParseFileName(fileName, out _));
    }

    [Fact]
    public void A_version_has_one_path_in_the_repository()
    {
        Assert.Equal("docs/release-notes/0.38.0.md", ReleaseSummary.PathFor(new ReleaseVersion(0, 38, 0)));
    }
}
