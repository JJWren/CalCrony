using System.Text;

namespace CalCrony.ReleaseNotes;

/// <summary>The two texts a release summary becomes: the GitHub release notes with the summary on
/// top, and the part of the Discord announcement that follows its first line.</summary>
public static class ReleaseTexts
{
    /// <summary>The line that tells a reader in Discord that a summary was cut short.</summary>
    public const string Pointer = "*…continued in the release notes.*";

    private const string Rule = "---";
    private const string BlankLine = "\n\n";

    /// <summary>Puts a summary on top of the notes release-please wrote: the summary, a rule, then
    /// the notes untouched. Notes that already begin with the summary come back as they are, so
    /// running a release's job twice adds nothing twice.</summary>
    /// <param name="summary">The summary's normalized text.</param>
    /// <param name="notes">The release notes as they stand on the release.</param>
    /// <returns>The release notes to put on the release.</returns>
    public static string ReleaseNotes(string summary, string notes)
    {
        ArgumentException.ThrowIfNullOrEmpty(summary);
        string existing = ReleaseSummary.Normalize(notes);

        if (existing.Length == 0)
        {
            return summary;
        }

        return existing.StartsWith(summary, StringComparison.Ordinal)
            ? existing
            : $"{summary}{BlankLine}{Rule}{BlankLine}{existing}";
    }

    /// <summary>The summary as the Discord announcement carries it: as written. A summary over
    /// <see cref="SummaryRules.MaxLength"/> — which the check on every pull request refuses, so
    /// this is for a file that reached a release anyway — is cut after its last whole line that
    /// fits, and <see cref="Pointer"/> follows.</summary>
    /// <param name="summary">The summary's normalized text, with nothing wrong with it but, at
    /// most, its length.</param>
    /// <returns>Text of at most <see cref="SummaryRules.MaxLength"/> characters.</returns>
    public static string DiscordSummary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.Length <= SummaryRules.MaxLength)
        {
            return summary;
        }

        int budget = SummaryRules.MaxLength - Pointer.Length - BlankLine.Length;
        return $"{Cut(summary, budget)}{BlankLine}{Pointer}";
    }

    /// <summary>Keeps the leading lines of a text that fit a number of characters, without the
    /// blank lines the cut leaves at the end. A line that does not fit ends the text — unless
    /// nothing but headings came before it: a heading with nothing under it says nothing, so that
    /// line is cut inside instead.</summary>
    /// <param name="text">Normalized text, longer than the budget.</param>
    /// <param name="budget">The most characters to keep.</param>
    /// <returns>The part kept.</returns>
    private static string Cut(string text, int budget)
    {
        StringBuilder kept = new();
        bool hasContent = false;

        foreach (string line in text.Split('\n'))
        {
            string separator = kept.Length == 0 ? string.Empty : "\n";
            int room = budget - kept.Length - separator.Length;

            if (line.Length > room)
            {
                if (!hasContent && room > 0)
                {
                    kept.Append(separator).Append(CutInsideLine(line, room));
                }

                break;
            }

            kept.Append(separator).Append(line);
            hasContent |= line.Trim().Length > 0 && !line.StartsWith('#');
        }

        return kept.ToString().TrimEnd();
    }

    /// <summary>Cuts a line at a number of characters, one earlier when that would split a
    /// character that takes two.</summary>
    /// <param name="line">The line, longer than the room left.</param>
    /// <param name="room">The most characters to keep, at least one.</param>
    /// <returns>The leading part of the line.</returns>
    private static string CutInsideLine(string line, int room)
    {
        int length = char.IsHighSurrogate(line[room - 1]) ? room - 1 : room;
        return line[..length];
    }
}
