namespace CalCrony.ReleaseNotes;

/// <summary>The pull request release-please keeps open for the next release.</summary>
public static class ReleasePullRequest
{
    private const string ReleaseWord = "release";

    /// <summary>Reads the version a release pull request is for from its title, which
    /// release-please ends in "release" and the version: "chore(master): release 0.38.0".</summary>
    /// <param name="title">The pull request's title.</param>
    /// <param name="version">The version, when the title ends that way.</param>
    /// <returns>Whether the title is a release pull request's.</returns>
    public static bool TryReadVersion(string title, out ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(title);
        version = default;
        string[] words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length >= 2
            && words[^2] == ReleaseWord
            && ReleaseVersion.TryParse(words[^1], out version);
    }
}
