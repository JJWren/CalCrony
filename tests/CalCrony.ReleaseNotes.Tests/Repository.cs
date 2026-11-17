namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The checkout these tests run from, for the tests that read the repository's own
/// files: its release summaries, its release workflow, its code owners file, and the files kept
/// beside the tests.</summary>
internal static class Repository
{
    private const string SolutionFileName = "CalCrony.slnx";
    private const string FixturesPath = "tests/CalCrony.ReleaseNotes.Tests/Fixtures";

    /// <summary>The folder the repository is checked out in.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Reads a file of the repository, with line ends and outer blank lines normalized.</summary>
    /// <param name="repositoryPath">The file's path from the repository root, with forward slashes.</param>
    /// <returns>The file's normalized text.</returns>
    public static string Read(string repositoryPath) =>
        ReleaseSummary.Normalize(File.ReadAllText(Path.Combine(Root, repositoryPath)));

    /// <summary>Reads a file kept beside the tests.</summary>
    /// <param name="name">The file's path inside the fixtures folder, with forward slashes.</param>
    /// <returns>The file's normalized text.</returns>
    public static string ReadFixture(string name) => Read($"{FixturesPath}/{name}");

    /// <summary>Walks up from the test assembly to the folder that holds the solution file. In a
    /// GitHub Actions run that folder has to be the workspace the repository was checked out to:
    /// any other folder with a solution file in it, lower in the tree, would have the tests read
    /// its files in place of the repository's.</summary>
    /// <returns>The repository root.</returns>
    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName
            ?? throw new InvalidOperationException($"{SolutionFileName} was not found above {AppContext.BaseDirectory}.");
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");

        if (!string.IsNullOrEmpty(workspace) && !IsSameFolder(root, workspace))
        {
            throw new InvalidOperationException($"{SolutionFileName} was found in {root}, not in the workspace {workspace}.");
        }

        return root;
    }

    /// <summary>Tells whether two paths name the same folder, written with or without a final
    /// separator; letter case counts only where the file system's does.</summary>
    /// <param name="first">One folder's path.</param>
    /// <param name="second">The other folder's path.</param>
    /// <returns>Whether the two paths name the same folder.</returns>
    private static bool IsSameFolder(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
