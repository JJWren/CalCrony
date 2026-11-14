namespace CalCrony.ReleaseNotes;

/// <summary>Reads what the tool needs from <c>CHANGELOG.md</c>, which release-please writes: the
/// last version released.</summary>
public static class Changelog
{
    /// <summary>The changelog's file name, at the repository root.</summary>
    public const string FileName = "CHANGELOG.md";

    private const string HeadingStart = "## ";
    private static readonly char[] VersionEnds = [']', ' ', '('];

    /// <summary>Reads the newest version from a changelog: the one in its first version heading,
    /// written <c>## [0.37.0](…) (2026-11-13)</c>, or <c>## 0.1.0 (…)</c> for a first release.</summary>
    /// <param name="changelog">The changelog's text.</param>
    /// <param name="version">The newest version, when a heading carries one.</param>
    /// <returns>Whether the changelog names a version.</returns>
    public static bool TryReadLatestVersion(string changelog, out ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(changelog);
        version = default;

        foreach (string line in ReleaseSummary.Normalize(changelog).Split('\n'))
        {
            if (line.StartsWith(HeadingStart, StringComparison.Ordinal) && TryReadHeadingVersion(line, out version))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads the version a second-level heading opens with, inside square brackets or
    /// bare.</summary>
    /// <param name="heading">A line that starts with "## ".</param>
    /// <param name="version">The version, when the heading opens with one.</param>
    /// <returns>Whether the heading is a version's.</returns>
    private static bool TryReadHeadingVersion(string heading, out ReleaseVersion version)
    {
        string rest = heading[HeadingStart.Length..].TrimStart('[');
        int end = rest.IndexOfAny(VersionEnds);

        return ReleaseVersion.TryParse(end < 0 ? rest : rest[..end], out version);
    }
}
