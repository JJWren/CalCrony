namespace CalCrony.ReleaseNotes;

/// <summary>The hand-written summary of one release: what changed and how to use it, in words for
/// the people who use CalCrony. It lives in a file named for the version, and a release puts its
/// text on top of the GitHub release notes and into the Discord announcement, as written.</summary>
/// <param name="Version">The release the summary is for.</param>
/// <param name="Text">The file's text with line ends and outer blank lines normalized.</param>
public sealed record ReleaseSummary(ReleaseVersion Version, string Text)
{
    /// <summary>Where the summaries live, from the repository root. Not <c>releases</c>: the
    /// repository's ignore file takes a folder of that name for build output.</summary>
    public const string FolderPath = "docs/release-notes";

    private const string Extension = ".md";
    private const char ByteOrderMark = '\uFEFF';

    /// <summary>The path of a version's summary from the repository root, with forward slashes:
    /// "docs/release-notes/0.38.0.md".</summary>
    /// <param name="version">The release.</param>
    /// <returns>The repository path of its summary file.</returns>
    public static string PathFor(ReleaseVersion version) => $"{FolderPath}/{version}{Extension}";

    /// <summary>Reads the version a summary file is named for: "0.38.0.md" is 0.38.0.</summary>
    /// <param name="fileName">A file name without its folder.</param>
    /// <param name="version">The version, when the name is a version followed by ".md".</param>
    /// <returns>Whether the file name is a summary's.</returns>
    public static bool TryParseFileName(string fileName, out ReleaseVersion version)
    {
        version = default;

        return fileName.EndsWith(Extension, StringComparison.Ordinal)
            && ReleaseVersion.TryParse(fileName[..^Extension.Length], out version);
    }

    /// <summary>Brings text to one form whatever wrote it: no byte-order mark, line ends as a
    /// single line feed, and no blank lines or spaces around the whole. Nothing inside a line
    /// changes.</summary>
    /// <param name="raw">The text as read.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return raw
            .TrimStart(ByteOrderMark)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    /// <summary>Reads a version's summary from a checkout of the repository. Only a plain file
    /// counts: a link named for the version is not followed.</summary>
    /// <param name="repositoryRoot">The folder the repository is checked out in.</param>
    /// <param name="version">The release.</param>
    /// <returns>The summary, or null when no plain file is named for that version.</returns>
    public static ReleaseSummary? Find(string repositoryRoot, ReleaseVersion version)
    {
        string path = Path.Combine(repositoryRoot, PathFor(version));

        return SummaryFolder.IsPlainFile(path)
            ? new ReleaseSummary(version, Normalize(File.ReadAllText(path)))
            : null;
    }
}
