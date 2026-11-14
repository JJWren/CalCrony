using System.Text;

namespace CalCrony.ReleaseNotes;

/// <summary>The two texts a release summary becomes: the GitHub release notes with the summary on
/// top, and the part of the Discord announcement that follows its first line.</summary>
public static class ReleaseTexts
{
    /// <summary>The line that tells a reader in Discord that a summary was cut short.</summary>
    public const string Pointer = "*…continued in the release notes.*";

    private const string Rule = "---";
    private const string CodeFence = "```";
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
    /// fits, a code block the cut leaves open is closed, and <see cref="Pointer"/> follows.</summary>
    /// <param name="summary">The summary's normalized text.</param>
    /// <returns>Text of at most <see cref="SummaryRules.MaxLength"/> characters.</returns>
    public static string DiscordSummary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.Length <= SummaryRules.MaxLength)
        {
            return summary;
        }

        // Room for the pointer, the blank line before it, and a fence on a line of its own.
        int budget = SummaryRules.MaxLength - Pointer.Length - BlankLine.Length - (CodeFence.Length + 1);
        string kept = KeepWholeLines(summary, budget);
        string closing = IsCodeBlockOpen(kept) ? "\n" + CodeFence : string.Empty;

        return $"{kept}{closing}{BlankLine}{Pointer}";
    }

    /// <summary>Keeps the leading lines of a text that fit a number of characters, without the
    /// blank lines the cut leaves at the end. A first line longer than that by itself is cut
    /// inside the line.</summary>
    /// <param name="text">Normalized text, so its first line is not blank.</param>
    /// <param name="budget">The most characters to keep.</param>
    /// <returns>The part kept.</returns>
    private static string KeepWholeLines(string text, int budget)
    {
        StringBuilder kept = new();

        foreach (string line in text.Split('\n'))
        {
            bool isFirst = kept.Length == 0;
            int lengthWithLine = isFirst ? line.Length : kept.Length + 1 + line.Length;

            if (lengthWithLine > budget)
            {
                break;
            }

            if (!isFirst)
            {
                kept.Append('\n');
            }

            kept.Append(line);
        }

        return kept.Length > 0
            ? kept.ToString().TrimEnd()
            : CutInsideLine(text, budget);
    }

    /// <summary>Cuts a text at a number of characters, one earlier when that would split a
    /// character that takes two.</summary>
    /// <param name="text">The text, longer than the budget.</param>
    /// <param name="budget">The most characters to keep.</param>
    /// <returns>The leading part of the text.</returns>
    private static string CutInsideLine(string text, int budget)
    {
        int length = char.IsHighSurrogate(text[budget - 1]) ? budget - 1 : budget;
        return text[..length].TrimEnd();
    }

    /// <summary>Tells whether a text ends inside a fenced code block.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether an odd number of its lines are code fences.</returns>
    private static bool IsCodeBlockOpen(string text) =>
        text.Split('\n').Count(SummaryRules.IsCodeFence) % 2 == 1;
}
