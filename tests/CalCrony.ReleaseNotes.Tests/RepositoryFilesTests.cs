namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The repository's own release summaries. This is the check every pull request gets: a
/// summary that would not fit the announcement, would render differently in the two places, or
/// would mention someone fails here, on the pull request that adds it.</summary>
public class RepositoryFilesTests
{
    /// <summary>Lists the versions that have a summary in this checkout.</summary>
    /// <returns>One row per version, as text.</returns>
    public static TheoryData<string> SummaryVersions()
    {
        TheoryData<string> versions = [];

        foreach (ReleaseVersion version in SummaryFolder.Versions(Repository.Root))
        {
            versions.Add(version.ToString());
        }

        return versions;
    }

    [Theory]
    [MemberData(nameof(SummaryVersions))]
    public void Every_release_summary_keeps_the_rules(string versionText)
    {
        Assert.True(ReleaseVersion.TryParse(versionText, out ReleaseVersion version));
        ReleaseSummary? summary = ReleaseSummary.Find(Repository.Root, version);

        Assert.NotNull(summary);
        Assert.Empty(SummaryRules.Check(summary.Text));
    }

    [Fact]
    public void Nothing_is_in_the_folder_that_a_release_would_never_read()
    {
        Assert.Empty(SummaryFolder.UnexpectedNames(Repository.Root));
    }

    [Fact]
    public void The_folder_explains_itself()
    {
        Assert.Contains(SummaryFolder.ReadMeFileName, SummaryFolder.EntryNames(Repository.Root));
    }

    [Fact]
    public void The_check_command_passes_on_this_checkout()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Cli.Run(["check", "--root", Repository.Root], output, error);

        Assert.True(exitCode == 0, output.ToString());
    }
}
