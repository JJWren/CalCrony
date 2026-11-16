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
    /// this is for a file that reached a release anyway — is cut short, after its last whole line
    /// that fits where it can, and <see cref="Pointer"/> follows.</summary>
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

    /// <summary>Keeps the leading lines of a text that fit a number of characters. A line that
    /// does not fit ends the text — unless nothing but headings came before it, in which case
    /// that line is cut inside instead. The blank lines the cut leaves at the end are dropped,
    /// and so is a heading left there with nothing under it: a heading alone says nothing.</summary>
    /// <param name="text">Normalized text, longer than the budget.</param>
    /// <param name="budget">The most characters to keep.</param>
    /// <returns>The part kept.</returns>
    private static string Cut(string text, int budget)
    {
        List<string> kept = [];
        int length = 0;
        bool hasContent = false;

        foreach (string line in text.Split('\n'))
        {
            int separator = kept.Count == 0 ? 0 : 1;
            int room = budget - length - separator;

            if (line.Length > room)
            {
                if (!hasContent && room > 0)
                {
                    kept.Add(CutInsideLine(line, room));
                }

                break;
            }

            kept.Add(line);
            length += separator + line.Length;
            hasContent |= IsContent(line);
        }

        while (kept.Count > 1 && !IsContent(kept[^1]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join('\n', kept);
    }

    /// <summary>Tells whether a line says something by itself: it is neither blank nor a heading.</summary>
    /// <param name="line">One line of a summary.</param>
    /// <returns>Whether the line is content.</returns>
    private static bool IsContent(string line) => line.Trim().Length > 0 && !line.StartsWith('#');

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
