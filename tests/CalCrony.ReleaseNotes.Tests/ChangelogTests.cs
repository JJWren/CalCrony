namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The last release, read from the changelog release-please writes.</summary>
public class ChangelogTests
{
    [Theory]
    [InlineData("# Changelog\n\n## [0.37.0](https://example.test/compare/v0.36.4...v0.37.0) (2026-11-13)\n\n### Features\n", "0.37.0")]
    [InlineData("# Changelog\n\n## 0.1.0 (2026-07-17)\n\n### Features\n", "0.1.0")]
    [InlineData("# Changelog\r\n\r\n## [1.2.3](https://example.test) (2027-01-01)\r\n\r\n## [1.2.2](https://example.test) (2026-12-01)\r\n", "1.2.3")]
    [InlineData("# Changelog\n\n## Unreleased\n\n## [0.5.0](https://example.test) (2026-08-01)\n", "0.5.0")]
    public void The_newest_version_is_the_first_version_heading(string changelog, string expected)
    {
        Assert.True(Changelog.TryReadLatestVersion(changelog, out ReleaseVersion version));

        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Changelog\n\nNothing released yet.\n")]
    [InlineData("# Changelog\n\n### [0.37.0] is a third-level heading\n")]
    public void A_changelog_without_a_version_heading_names_no_version(string changelog)
    {
        Assert.False(Changelog.TryReadLatestVersion(changelog, out _));
    }

    [Fact]
    public void The_repositorys_own_changelog_names_a_version()
    {
        string changelog = File.ReadAllText(Path.Combine(Repository.Root, Changelog.FileName));

        Assert.True(Changelog.TryReadLatestVersion(changelog, out ReleaseVersion version));
        Assert.True(version.CompareTo(new ReleaseVersion(0, 37, 0)) >= 0);
    }
}
