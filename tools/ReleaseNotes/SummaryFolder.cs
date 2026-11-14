namespace CalCrony.ReleaseNotes;

/// <summary>The folder of release summaries in a checkout of the repository
/// (<see cref="ReleaseSummary.FolderPath"/>): one file per release, named for its version, and a
/// README.</summary>
public static class SummaryFolder
{
    /// <summary>The one file in the folder that is not a summary.</summary>
    public const string ReadMeFileName = "README.md";

    /// <summary>Lists what is in the folder, files and folders alike, by name.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The names in ordinal order; empty when the folder does not exist.</returns>
    public static IReadOnlyList<string> EntryNames(string repositoryRoot)
    {
        string folder = Path.Combine(repositoryRoot, ReleaseSummary.FolderPath);

        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFileSystemEntries(folder)
            .Select(entry => Path.GetFileName(entry))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Lists the versions that have a summary.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The versions, oldest first.</returns>
    public static IReadOnlyList<ReleaseVersion> Versions(string repositoryRoot)
    {
        List<ReleaseVersion> versions = [];

        foreach (string name in EntryNames(repositoryRoot))
        {
            if (ReleaseSummary.TryParseFileName(name, out ReleaseVersion version))
            {
                versions.Add(version);
            }
        }

        versions.Sort();
        return versions;
    }

    /// <summary>Lists what does not belong in the folder: anything that is neither the README
    /// nor named for a version, such as <c>v0.38.0.md</c> or <c>0.38.md</c>. A release would never
    /// read it.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The names that do not belong, in ordinal order.</returns>
    public static IReadOnlyList<string> UnexpectedNames(string repositoryRoot) =>
        EntryNames(repositoryRoot)
            .Where(name => name != ReadMeFileName && !ReleaseSummary.TryParseFileName(name, out _))
            .ToList();
}
