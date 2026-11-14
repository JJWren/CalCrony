namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The version a release pull request is for, read from the title release-please gives
/// it.</summary>
public class ReleasePullRequestTests
{
    [Theory]
    [InlineData("chore(master): release 0.38.0", "0.38.0")]
    [InlineData("chore: release 1.0.0", "1.0.0")]
    [InlineData("chore(master): release 0.37.1 ", "0.37.1")]
    public void A_release_title_ends_in_release_and_the_version(string title, string expected)
    {
        Assert.True(ReleasePullRequest.TryReadVersion(title, out ReleaseVersion version));

        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.38.0")]
    [InlineData("chore(master): release v0.38.0")]
    [InlineData("chore(master): release 0.38")]
    [InlineData("docs: notes for 0.38.0")]
    [InlineData("feat: release 0.38.0 early")]
    public void Any_other_title_is_not_a_release_title(string title)
    {
        Assert.False(ReleasePullRequest.TryReadVersion(title, out _));
    }
}
