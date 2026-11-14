namespace CalCrony.ReleaseNotes;

/// <summary>The folder of release summaries in a checkout of the repository
/// (<see cref="ReleaseSummary.FolderPath"/>): one file per release, named for its version, and a
/// README. Nothing is read through a link — not a file that is one, and not a folder that is
/// one — because what a link points at is not the repository's to publish.</summary>
public static class SummaryFolder
{
    /// <summary>The one file in the folder that is not a summary.</summary>
    public const string ReadMeFileName = "README.md";

    /// <summary>Gives the folder's path in a checkout.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The full path of the folder of summaries, existing or not.</returns>
    public static string PathIn(string repositoryRoot) => Path.Combine(repositoryRoot, ReleaseSummary.FolderPath);

    /// <summary>Tells whether the folder of summaries, or a folder above it inside the checkout,
    /// is a link to somewhere else.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>Whether the folder is reached through a link.</returns>
    public static bool IsReachedThroughLink(string repositoryRoot)
    {
        string path = repositoryRoot;

        foreach (string part in ReleaseSummary.FolderPath.Split('/'))
        {
            path = Path.Combine(path, part);

            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lists what is in the folder, files and folders alike, by name.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <returns>The names in ordinal order; empty when the folder does not exist or is reached
    /// through a link.</returns>
    public static IReadOnlyList<string> EntryNames(string repositoryRoot)
    {
        string folder = PathIn(repositoryRoot);

        if (!Directory.Exists(folder) || IsReachedThroughLink(repositoryRoot))
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

    /// <summary>Tells whether a version's summary can be read: a file of its own — there, not a
    /// folder, not a link — in a folder that is not reached through a link either.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <param name="name">The file's name in the folder of summaries.</param>
    /// <returns>Whether the entry is a plain file in a plain folder.</returns>
    internal static bool IsPlainFile(string repositoryRoot, string name)
    {
        FileInfo file = new(Path.Combine(PathIn(repositoryRoot), name));
        return file.Exists && file.LinkTarget is null && !IsReachedThroughLink(repositoryRoot);
    }
}
