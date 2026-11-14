namespace CalCrony.ReleaseNotes;

/// <summary>What a checkout of the repository holds for a release that has not been made yet: its
/// summary if one is written, what the rules say about it, and summaries that look left behind.</summary>
/// <param name="Version">The release about to be made.</param>
/// <param name="Summary">Its summary, or null when no file is named for the version.</param>
/// <param name="Problems">What the rules found wrong with the summary.</param>
/// <param name="Leftovers">Versions that have a summary, are newer than the last release and are
/// not this one — a file written for a number the release did not get in the end.</param>
public sealed record ReleasePreview(
    ReleaseVersion Version,
    ReleaseSummary? Summary,
    IReadOnlyList<SummaryProblem> Problems,
    IReadOnlyList<ReleaseVersion> Leftovers)
{
    /// <summary>Whether the release has text to carry: a summary file that is not empty.</summary>
    public bool HasSummary => Summary is { Text.Length: > 0 };

    /// <summary>Reads a checkout for a release about to be made.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in, at the commit
    /// the release will be cut from or before it.</param>
    /// <param name="version">The release about to be made.</param>
    /// <returns>What the checkout holds for it.</returns>
    public static ReleasePreview Gather(string repositoryRoot, ReleaseVersion version)
    {
        ReleaseSummary? summary = ReleaseSummary.Find(repositoryRoot, version);
        IReadOnlyList<SummaryProblem> problems = summary is null ? [] : SummaryRules.Check(summary.Text);

        return new ReleasePreview(version, summary, problems, FindLeftovers(repositoryRoot, version));
    }

    /// <summary>Finds the summaries for versions after the last release other than this one. The
    /// last release is the changelog's newest version; without a changelog nothing is reported.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <param name="version">The release about to be made.</param>
    /// <returns>The versions of those summaries, oldest first.</returns>
    private static IReadOnlyList<ReleaseVersion> FindLeftovers(string repositoryRoot, ReleaseVersion version)
    {
        string changelogPath = Path.Combine(repositoryRoot, Changelog.FileName);

        if (!File.Exists(changelogPath)
            || !Changelog.TryReadLatestVersion(File.ReadAllText(changelogPath), out ReleaseVersion lastRelease))
        {
            return [];
        }

        return SummaryFolder.Versions(repositoryRoot)
            .Where(candidate => candidate.CompareTo(lastRelease) > 0 && candidate != version)
            .ToList();
    }
}
