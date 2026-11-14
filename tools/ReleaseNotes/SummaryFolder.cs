namespace CalCrony.ReleaseNotes;

/// <summary>The folder of release summaries in a checkout of the repository
/// (<see cref="ReleaseSummary.FolderPath"/>): one file per release, named for its version, and a
/// README.</summary>
public static class SummaryFolder
{
    /// <summary>The one file in the folder that is not a summary.</summary>
    public const string ReadMeFileName = "README.md";

    /// <summary>Gives the folder's path in a checkout.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The full path of the folder of summaries, existing or not.</returns>
    public static string PathIn(string repositoryRoot) => Path.Combine(repositoryRoot, ReleaseSummary.FolderPath);

    /// <summary>Lists what is in the folder, files and folders alike, by name.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The names in ordinal order; empty when the folder does not exist.</returns>
    public static IReadOnlyList<string> EntryNames(string repositoryRoot)
    {
        string folder = PathIn(repositoryRoot);

        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFileSystemEntries(folder)
            .Select(entry => Path.GetFileName(entry))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Lists the versions that have a summary: a plain file named for the version.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The versions, oldest first.</returns>
    public static IReadOnlyList<ReleaseVersion> Versions(string repositoryRoot)
    {
        List<ReleaseVersion> versions = [];

        foreach (string name in EntryNames(repositoryRoot))
        {
            if (ReleaseSummary.TryParseFileName(name, out ReleaseVersion version) && IsPlainFile(repositoryRoot, name))
            {
                versions.Add(version);
            }
        }

        versions.Sort();
        return versions;
    }

    /// <summary>Lists what does not belong in the folder: anything that is neither the README
    /// nor a plain file named for a version — <c>v0.38.0.md</c>, <c>0.38.md</c>, a folder, a
    /// link. A release would never read it.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The names that do not belong, in ordinal order.</returns>
    public static IReadOnlyList<string> UnexpectedNames(string repositoryRoot) =>
        EntryNames(repositoryRoot)
            .Where(name => name != ReadMeFileName
                && !(ReleaseSummary.TryParseFileName(name, out _) && IsPlainFile(repositoryRoot, name)))
            .ToList();

    /// <summary>Tells whether a path is a file of its own: there, not a folder, and not a link
    /// to something else. A link is never followed, because what it points at is not the
    /// repository's to publish.</summary>
    /// <param name="path">The full path.</param>
    /// <returns>Whether the path is a plain file.</returns>
    internal static bool IsPlainFile(string path)
    {
        FileInfo file = new(path);
        return file.Exists && file.LinkTarget is null;
    }

    /// <summary>Tells whether an entry of the folder is a plain file.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <param name="name">The entry's name.</param>
    /// <returns>Whether the entry is a plain file.</returns>
    private static bool IsPlainFile(string repositoryRoot, string name) =>
        IsPlainFile(Path.Combine(PathIn(repositoryRoot), name));
}
