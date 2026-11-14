using System.Globalization;
using System.Text;

namespace CalCrony.ReleaseNotes;

/// <summary>What a release summary has to keep to. The file is posted as written to Discord and
/// to GitHub, so the rules hold it to what both show the same way, to a length the announcement
/// has room for, and to text that mentions nobody.</summary>
public static class SummaryRules
{
    /// <summary>The most characters a summary may have. Discord takes 2,000 in a message, and the
    /// announcement's first line needs about 130 of them.</summary>
    public const int MaxLength = 1800;

    /// <summary>The heading every summary opens with.</summary>
    public const string WhatsNewHeading = "### What's new";

    /// <summary>The one other heading a summary may have, after the first.</summary>
    public const string HowToHeading = "### How to use it";

    private const int WholeFile = 0;
    private const int MaxHeadingIndent = 3;
    private const int MaxHeadingLevel = 6;
    private const int MinRuleLength = 3;
    private const string CodeFence = "```";
    private const string RuleCharacters = "-*_=";

    /// <summary>Checks a summary against every rule.</summary>
    /// <param name="text">The summary's normalized text (<see cref="ReleaseSummary.Normalize"/>).</param>
    /// <returns>The problems found, in the order of their lines; empty when the summary is fine.</returns>
    public static IReadOnlyList<SummaryProblem> Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [new SummaryProblem(WholeFile, "The file is empty.")];
        }

        List<SummaryProblem> problems = [];

        if (text.Length > MaxLength)
        {
            problems.Add(new SummaryProblem(WholeFile, string.Create(
                CultureInfo.InvariantCulture,
                $"The summary is {text.Length:N0} characters; the limit is {MaxLength:N0}, which keeps the Discord announcement inside Discord's 2,000.")));
        }

        string[] lines = text.Split('\n');
        bool isInCodeBlock = false;
        int codeBlockLine = 0;
        bool hasHowTo = false;
        bool hasBullet = false;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int number = index + 1;

            AddControlCharacterProblem(line, number, problems);

            if (number == 1 && line != WhatsNewHeading)
            {
                problems.Add(new SummaryProblem(number, $"The first line must be `{WhatsNewHeading}`."));
            }

            if (IsCodeFence(line))
            {
                isInCodeBlock = !isInCodeBlock;
                codeBlockLine = number;
                continue;
            }

            if (isInCodeBlock)
            {
                continue;
            }

            if (IsHeading(line))
            {
                bool isHowTo = line == HowToHeading && !hasHowTo;
                hasHowTo |= isHowTo;

                if (number > 1 && !isHowTo)
                {
                    problems.Add(new SummaryProblem(
                        number,
                        $"Only two headings are allowed: `{WhatsNewHeading}` on the first line and `{HowToHeading}` once after it."));
                }

                continue;
            }

            hasBullet |= !hasHowTo && IsBullet(line);
            AddTextProblems(MaskCodeSpans(line), number, problems);
        }

        if (isInCodeBlock)
        {
            problems.Add(new SummaryProblem(codeBlockLine, "The code block opened here is never closed."));
        }

        if (!hasBullet)
        {
            problems.Add(new SummaryProblem(
                1,
                $"`{WhatsNewHeading}` needs at least one bullet under it: a line that starts with `- `."));
        }

        return problems.OrderBy(problem => problem.Line).ToList();
    }

    /// <summary>Tells whether a line opens or closes a fenced code block.</summary>
    /// <param name="line">One line of the summary.</param>
    /// <returns>Whether the line is a code fence.</returns>
    internal static bool IsCodeFence(string line)
    {
        string trimmed = line.TrimStart(' ');

        return line.Length - trimmed.Length <= MaxHeadingIndent
            && trimmed.StartsWith(CodeFence, StringComparison.Ordinal);
    }

    /// <summary>Tells whether a line is a heading of any level: one to six number signs, then a
    /// space or the end of the line.</summary>
    /// <param name="line">One line of the summary, outside a code block.</param>
    /// <returns>Whether Markdown reads the line as a heading.</returns>
    private static bool IsHeading(string line)
    {
        string trimmed = line.TrimStart(' ');

        if (line.Length - trimmed.Length > MaxHeadingIndent)
        {
            return false;
        }

        int level = trimmed.Length - trimmed.TrimStart('#').Length;

        return level is >= 1 and <= MaxHeadingLevel
            && (trimmed.Length == level || trimmed[level] is ' ' or '\t');
    }

    /// <summary>Tells whether a line starts a list item: a hyphen or an asterisk, then a space,
    /// indented or not.</summary>
    /// <param name="line">One line of the summary.</param>
    /// <returns>Whether the line is a bullet.</returns>
    private static bool IsBullet(string line)
    {
        string trimmed = line.TrimStart(' ');
        return trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal);
    }

    /// <summary>Adds the problems of a line's text: what Discord shows raw where GitHub renders
    /// it, and what would mention someone.</summary>
    /// <param name="text">The line with the contents of its code spans blanked out.</param>
    /// <param name="number">The line's number.</param>
    /// <param name="problems">The list to add to.</param>
    private static void AddTextProblems(string text, int number, List<SummaryProblem> problems)
    {
        string trimmed = text.Trim();

        if (trimmed.StartsWith('|'))
        {
            problems.Add(new SummaryProblem(number, "Tables do not render in Discord; use a list."));
        }

        if (text.Contains("![", StringComparison.Ordinal))
        {
            problems.Add(new SummaryProblem(number, "Images do not show in Discord; remove it or link to it."));
        }

        if (IsRule(trimmed))
        {
            problems.Add(new SummaryProblem(
                number,
                "A line of dashes is a rule or a heading on GitHub and plain dashes in Discord; remove it."));
        }

        if (HasBareAngleBracket(text))
        {
            problems.Add(new SummaryProblem(
                number,
                "`<` outside backticks: HTML and Discord's own tags show as raw text in one place or the other. Put it in backticks or reword; a link written as `<https://…>` is fine."));
        }

        if (HasMention(text))
        {
            problems.Add(new SummaryProblem(
                number,
                "`@` followed by a name outside backticks is a mention: GitHub notifies the user of that name. Put it in backticks."));
        }
    }

    /// <summary>Adds a problem for the first character of a line that cannot be seen in review: a
    /// control character other than a tab, or one that reorders the text around it.</summary>
    /// <param name="line">One line of the summary, inside a code block or not.</param>
    /// <param name="number">The line's number.</param>
    /// <param name="problems">The list to add to.</param>
    private static void AddControlCharacterProblem(string line, int number, List<SummaryProblem> problems)
    {
        foreach (char character in line)
        {
            if (IsInvisibleControl(character))
            {
                problems.Add(new SummaryProblem(number, string.Create(
                    CultureInfo.InvariantCulture,
                    $"An invisible control character (U+{(int)character:X4}); remove it.")));
                return;
            }
        }
    }

    /// <summary>Tells whether a character is a control character other than a tab, or a
    /// bidirectional control (U+202A–U+202E, U+2066–U+2069).</summary>
    /// <param name="character">The character.</param>
    /// <returns>Whether the character is one the rules refuse.</returns>
    private static bool IsInvisibleControl(char character) =>
        (char.IsControl(character) && character != '\t')
        || character is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    /// <summary>Tells whether a line is three or more of one rule character and nothing else but
    /// spaces: a horizontal rule, or the underline that turns the line above into a heading.</summary>
    /// <param name="trimmed">The line without its outer spaces.</param>
    /// <returns>Whether the line is a rule.</returns>
    private static bool IsRule(string trimmed)
    {
        string marks = trimmed.Replace(" ", string.Empty, StringComparison.Ordinal);

        return marks.Length >= MinRuleLength
            && RuleCharacters.Contains(marks[0], StringComparison.Ordinal)
            && marks.All(mark => mark == marks[0]);
    }

    /// <summary>Tells whether a line has a <c>&lt;</c> that does not open a link written as
    /// <c>&lt;https://…&gt;</c>.</summary>
    /// <param name="text">The line with its code spans blanked out.</param>
    /// <returns>Whether such an angle bracket is there.</returns>
    private static bool HasBareAngleBracket(string text)
    {
        for (int index = text.IndexOf('<', StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf('<', index + 1))
        {
            ReadOnlySpan<char> rest = text.AsSpan(index);

            if (!rest.StartsWith("<https://", StringComparison.Ordinal)
                && !rest.StartsWith("<http://", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tells whether a line has an at sign followed by a letter, a digit or an
    /// underscore.</summary>
    /// <param name="text">The line with its code spans blanked out.</param>
    /// <returns>Whether a mention is there.</returns>
    private static bool HasMention(string text)
    {
        for (int index = 0; index < text.Length - 1; index++)
        {
            char next = text[index + 1];

            if (text[index] == '@' && (char.IsLetterOrDigit(next) || next == '_'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Blanks out the code spans of a line, backticks included, so that what is written
    /// as code is not read as text. A run of backticks with no matching run after it on the line
    /// stays as it is.</summary>
    /// <param name="line">One line of the summary, outside a code block.</param>
    /// <returns>The line, the same length, with spaces where its code spans were.</returns>
    private static string MaskCodeSpans(string line)
    {
        StringBuilder masked = new(line.Length);
        int index = 0;

        while (index < line.Length)
        {
            if (line[index] != '`')
            {
                masked.Append(line[index]);
                index++;
                continue;
            }

            int runLength = CountBackticks(line, index);
            int closing = FindClosingRun(line, index + runLength, runLength);

            if (closing < 0)
            {
                masked.Append('`', runLength);
                index += runLength;
                continue;
            }

            int spanLength = closing + runLength - index;
            masked.Append(' ', spanLength);
            index += spanLength;
        }

        return masked.ToString();
    }

    /// <summary>Counts the backticks in a row from a position.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">The position of the first backtick.</param>
    /// <returns>How many backticks follow one another there.</returns>
    private static int CountBackticks(string line, int start)
    {
        int end = start;

        while (end < line.Length && line[end] == '`')
        {
            end++;
        }

        return end - start;
    }

    /// <summary>Finds the run of backticks that closes a code span: the next run of exactly the
    /// length that opened it.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">Where to look from.</param>
    /// <param name="runLength">The length of the run that opened the span.</param>
    /// <returns>The position of the closing run, or -1 when the line has none.</returns>
    private static int FindClosingRun(string line, int start, int runLength)
    {
        int index = start;

        while (index < line.Length)
        {
            if (line[index] != '`')
            {
                index++;
                continue;
            }

            int length = CountBackticks(line, index);

            if (length == runLength)
            {
                return index;
            }

            index += length;
        }

        return -1;
    }
}
