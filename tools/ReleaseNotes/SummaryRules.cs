using System.Globalization;
using System.Text;

namespace CalCrony.ReleaseNotes;

/// <summary>What a release summary has to keep to. The file is posted as written to Discord and
/// to GitHub, so the rules hold it to a shape both show alike, to a length the announcement has
/// room for, and to text that mentions nobody. Every line is read by itself, the way GitHub
/// reads a line — what is code, what a backslash does — because GitHub is where a mention
/// notifies. Code that runs over several lines is refused rather than followed, so there is no
/// state from one line to the next for the check and GitHub to disagree about.</summary>
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
    private const int MaxHeadingLevel = 6;
    private const int MinRuleLength = 3;
    private const int MaxListNumberLength = 9;
    private const int ZeroWidthJoiner = 0x200D;
    private const string RuleCharacters = "-*_=";
    private const string BacktickFence = "```";
    private const string TildeFence = "~~~";
    private static readonly string[] LinkSchemes = ["https://", "http://"];

    /// <summary>Checks a summary against every rule.</summary>
    /// <param name="text">The summary's normalized text (<see cref="ReleaseSummary.Normalize"/>).</param>
    /// <returns>The problems found, in the order of their lines; empty when the summary is fine.</returns>
    public static IReadOnlyList<SummaryProblem> Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [new SummaryProblem(WholeFile, "The file is empty.", false)];
        }

        List<SummaryProblem> problems = [];

        if (text.Length > MaxLength)
        {
            string message = string.Create(
                CultureInfo.InvariantCulture,
                $"The summary is {text.Length:N0} characters; the limit is {MaxLength:N0}, which keeps the Discord announcement inside Discord's 2,000.");
            problems.Add(new SummaryProblem(WholeFile, message, true));
        }

        string[] lines = text.Split('\n');
        bool hasHowTo = false;
        bool hasBullet = false;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int number = index + 1;
            string content = StripContainers(line);

            AddInvisibleCharacterProblem(line, number, problems);

            if (number == 1 && line != WhatsNewHeading)
            {
                Add(problems, number, $"The first line must be exactly `{WhatsNewHeading}`: a straight apostrophe, nothing before it and nothing after.");
            }

            if (IsHeading(content))
            {
                bool isHowTo = line == HowToHeading && !hasHowTo;
                hasHowTo |= isHowTo;

                if (number > 1 && !isHowTo)
                {
                    Add(problems, number, $"Only two headings are allowed: `{WhatsNewHeading}` on the first line and `{HowToHeading}` once after it, each on a line of its own.");
                }

                continue;
            }

            hasBullet |= !hasHowTo && IsBullet(line);
            AddTextProblems(line, content, number, problems);
        }

        if (!hasBullet)
        {
            Add(problems, 1, $"`{WhatsNewHeading}` needs at least one bullet under it: a line that starts with `- `.");
        }

        return problems.OrderBy(problem => problem.Line).ToList();
    }

    /// <summary>Tells whether problems keep a summary out of a release. Being too long does not —
    /// Discord gets the summary cut short — and anything else does: text the check refuses is not
    /// published.</summary>
    /// <param name="problems">What <see cref="Check"/> found.</param>
    /// <returns>Whether the summary must be left out.</returns>
    public static bool HoldSummaryBack(IReadOnlyList<SummaryProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Any(problem => !problem.IsLength);
    }

    /// <summary>Adds a problem that keeps the summary out of a release.</summary>
    /// <param name="problems">The list to add to.</param>
    /// <param name="line">The line the problem is on.</param>
    /// <param name="message">What is wrong and what to change.</param>
    private static void Add(List<SummaryProblem> problems, int line, string message) =>
        problems.Add(new SummaryProblem(line, message, false));

    /// <summary>Takes the list and quote markers off the front of a line — <c>- </c>, <c>1. </c>,
    /// <c>&gt;</c>, nested or not — because GitHub reads a heading or a code fence behind them as
    /// what it is.</summary>
    /// <param name="line">One line of the summary.</param>
    /// <returns>What the line holds inside its lists and quotes, without leading spaces.</returns>
    private static string StripContainers(string line)
    {
        string rest = line.TrimStart(' ');
        int marker = ContainerMarkerLength(rest);

        while (marker > 0)
        {
            rest = rest[marker..].TrimStart(' ');
            marker = ContainerMarkerLength(rest);
        }

        return rest;
    }

    /// <summary>Measures a list or quote marker at the start of a text.</summary>
    /// <param name="text">Text without leading spaces.</param>
    /// <returns>The marker's length, or 0 when the text starts with none.</returns>
    private static int ContainerMarkerLength(string text)
    {
        if (text.StartsWith('>'))
        {
            return 1;
        }

        if (text.Length >= 2 && text[0] is '-' or '*' or '+' && text[1] == ' ')
        {
            return 2;
        }

        int digits = 0;

        while (digits < text.Length && digits < MaxListNumberLength && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        bool isNumbered = digits > 0
            && digits + 1 < text.Length
            && text[digits] is '.' or ')'
            && text[digits + 1] == ' ';

        return isNumbered ? digits + 2 : 0;
    }

    /// <summary>Tells whether a text is a heading of any level: one to six number signs, then a
    /// space or the end of the line.</summary>
    /// <param name="content">A line without its list and quote markers.</param>
    /// <returns>Whether Markdown reads it as a heading.</returns>
    private static bool IsHeading(string content)
    {
        int level = content.Length - content.TrimStart('#').Length;

        return level is >= 1 and <= MaxHeadingLevel
            && (content.Length == level || content[level] is ' ' or '\t');
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

    /// <summary>Adds the problems of a line of text: what Discord shows raw where GitHub renders
    /// it, what GitHub would read differently from this check, and what would mention someone.</summary>
    /// <param name="line">The line as written.</param>
    /// <param name="content">The line without its list and quote markers.</param>
    /// <param name="number">The line's number.</param>
    /// <param name="problems">The list to add to.</param>
    private static void AddTextProblems(string line, string content, int number, List<SummaryProblem> problems)
    {
        string text = MaskInline(line, out bool hasLooseBacktick);
        bool isFence = content.StartsWith(BacktickFence, StringComparison.Ordinal)
            || content.StartsWith(TildeFence, StringComparison.Ordinal);

        if (isFence)
        {
            Add(problems, number, "Code blocks are not allowed in a summary; put a command in backticks on one line.");
        }
        else if (hasLooseBacktick)
        {
            Add(problems, number, "A backtick with no partner on its line: code opens and closes on the same line.");
        }

        if (content.StartsWith('|') || IsTableDelimiterRow(content.Trim()))
        {
            Add(problems, number, "Tables do not render in Discord; use a list.");
        }

        if (text.Contains("![", StringComparison.Ordinal))
        {
            Add(problems, number, "Images do not show in Discord; remove it or link to it.");
        }

        if (IsRule(line.Trim()))
        {
            Add(problems, number, "A line of only dashes or equals signs is a rule or a heading on GitHub and plain characters in Discord; remove it.");
        }

        if (text.Contains('<', StringComparison.Ordinal))
        {
            Add(problems, number, "`<` outside backticks: HTML and Discord's own tags show as raw text in one place or the other. Put it in backticks or reword; a link written as `<https://…>` is fine.");
        }

        if (HasEntity(text))
        {
            Add(problems, number, "An HTML entity such as `&amp;` outside backticks: GitHub turns it into the character and Discord shows it as typed. Write the character itself.");
        }

        if (HasMention(text))
        {
            Add(problems, number, "`@` followed by a name outside backticks is a mention: GitHub notifies the user of that name. Put it in backticks.");
        }
    }

    /// <summary>Adds a problem for the first character of a line that cannot be seen in review:
    /// a control character other than a tab, a format character other than the joiner emoji are
    /// built with — zero-width spaces, marks that reorder the text around them, tag characters —
    /// or a line or paragraph separator.</summary>
    /// <param name="line">One line of the summary.</param>
    /// <param name="number">The line's number.</param>
    /// <param name="problems">The list to add to.</param>
    private static void AddInvisibleCharacterProblem(string line, int number, List<SummaryProblem> problems)
    {
        foreach (Rune rune in line.EnumerateRunes())
        {
            if (IsInvisible(rune))
            {
                Add(problems, number, string.Create(
                    CultureInfo.InvariantCulture,
                    $"An invisible character (U+{rune.Value:X4}); remove it."));
                return;
            }
        }
    }

    /// <summary>Tells whether a character is one the rules refuse because nobody can see it.</summary>
    /// <param name="rune">The character.</param>
    /// <returns>Whether the character is invisible in that sense.</returns>
    private static bool IsInvisible(Rune rune)
    {
        if (rune.Value is '\t' or ZeroWidthJoiner)
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>Tells whether a line is a rule or the underline of a heading: three or more of
    /// one rule character with or without spaces between them, or any number of dashes or of
    /// equals signs alone, which under a line of text turn that line into a heading.</summary>
    /// <param name="trimmed">The line without its outer spaces.</param>
    /// <returns>Whether the line is a rule or an underline.</returns>
    private static bool IsRule(string trimmed)
    {
        string marks = trimmed.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (marks.Length == 0
            || !RuleCharacters.Contains(marks[0], StringComparison.Ordinal)
            || !marks.All(mark => mark == marks[0]))
        {
            return false;
        }

        bool isUnderline = marks[0] is '-' or '=' && marks.Length == trimmed.Length;
        return marks.Length >= MinRuleLength || isUnderline;
    }

    /// <summary>Tells whether a line is the row that makes the lines around it a table on GitHub:
    /// cells of dashes, with or without colons, between pipes.</summary>
    /// <param name="trimmed">The line without its outer spaces.</param>
    /// <returns>Whether the line is a table's delimiter row.</returns>
    private static bool IsTableDelimiterRow(string trimmed)
    {
        if (!trimmed.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.Trim('|').Split('|').All(cell => IsDelimiterCell(cell.Trim()));
    }

    /// <summary>Tells whether a table cell is a delimiter: dashes, with a colon allowed at either
    /// end.</summary>
    /// <param name="cell">The cell without its outer spaces.</param>
    /// <returns>Whether the cell is a delimiter.</returns>
    private static bool IsDelimiterCell(string cell)
    {
        string dashes = cell.Trim(':');
        return dashes.Length > 0 && dashes.All(character => character == '-');
    }

    /// <summary>Tells whether a line has an HTML entity: an ampersand, then a name or a number
    /// sign and digits, then a semicolon. GitHub turns <c>&amp;#64;</c> into an at sign and then
    /// reads the mention.</summary>
    /// <param name="text">The line with its code and its links blanked out.</param>
    /// <returns>Whether an entity is there.</returns>
    private static bool HasEntity(string text)
    {
        for (int index = text.IndexOf('&', StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf('&', index + 1))
        {
            int start = index + 1;
            bool isNumeric = start < text.Length && text[start] == '#';
            int nameStart = isNumeric ? start + 1 : start;
            int end = nameStart;

            while (end < text.Length && char.IsAsciiLetterOrDigit(text[end]))
            {
                end++;
            }

            bool hasName = end > nameStart && (isNumeric || char.IsAsciiLetter(text[nameStart]));

            if (hasName && end < text.Length && text[end] == ';')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tells whether a line has an at sign followed by a letter, a digit or an
    /// underscore.</summary>
    /// <param name="text">The line with its code and its links blanked out.</param>
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

    /// <summary>Blanks out what a line holds that is not text: its code spans, backticks
    /// included; its links written whole in angle brackets; and the addresses of its other links,
    /// where an at sign is part of the address. It goes left to right as GitHub does: a
    /// character after a backslash is that character and opens nothing, so <c>\`</c> starts no
    /// code; a link ends where its address ends, whatever is inside; and a run of backticks with
    /// no run of the same length after it on the line stays, and is reported.</summary>
    /// <param name="line">One line of the summary.</param>
    /// <param name="hasLooseBacktick">Whether a run of backticks on the line has no partner.</param>
    /// <returns>The line, the same length, with spaces where its code and its links were.</returns>
    private static string MaskInline(string line, out bool hasLooseBacktick)
    {
        StringBuilder masked = new(line.Length);
        int index = 0;
        hasLooseBacktick = false;

        while (index < line.Length)
        {
            char character = line[index];
            int length = 1;
            bool isBlanked = false;

            if (character == '\\' && index + 1 < line.Length && IsAsciiPunctuation(line[index + 1]))
            {
                length = 2;
            }
            else if (character == '`')
            {
                int runLength = CountBackticks(line, index);
                int closing = FindClosingRun(line, index + runLength, runLength);
                isBlanked = closing >= 0;
                hasLooseBacktick |= !isBlanked;
                length = isBlanked ? closing + runLength - index : runLength;
            }
            else
            {
                int linkLength = character == '<' ? AutolinkLength(line, index) : AddressLength(line, index);
                isBlanked = linkLength > 0;
                length = Math.Max(linkLength, 1);
            }

            if (isBlanked)
            {
                masked.Append(' ', length);
            }
            else
            {
                masked.Append(line, index, length);
            }

            index += length;
        }

        return masked.ToString();
    }

    /// <summary>Reads a link written whole in angle brackets: <c>&lt;https://…&gt;</c> or
    /// <c>&lt;http://…&gt;</c>, with no space, backtick or angle bracket before its end.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">The position of the opening angle bracket.</param>
    /// <returns>The link's length with both brackets, or 0 when no such link starts there.</returns>
    private static int AutolinkLength(string line, int start)
    {
        ReadOnlySpan<char> rest = line.AsSpan(start + 1);

        if (SchemeLength(rest) == 0)
        {
            return 0;
        }

        for (int index = 0; index < rest.Length; index++)
        {
            char character = rest[index];

            if (character == '>')
            {
                return index + 2;
            }

            if (char.IsWhiteSpace(character) || character is '<' or '`')
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>Reads a web address written bare or as a link's target: a scheme, a host name
    /// with a dot in it, then a path that runs to the next space, bracket or backtick. Only what
    /// follows the host is the path: in <c>https://name@host</c> the at sign is not in a path,
    /// and it stays visible to the check.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">The position to read from.</param>
    /// <returns>The address's length, or 0 when none starts there.</returns>
    private static int AddressLength(string line, int start)
    {
        ReadOnlySpan<char> rest = line.AsSpan(start);
        int scheme = SchemeLength(rest);
        bool isInsideWord = start > 0 && char.IsLetterOrDigit(line[start - 1]);

        if (scheme == 0 || isInsideWord)
        {
            return 0;
        }

        int end = scheme;
        int dots = 0;
        bool isHostWellFormed = true;

        while (end < rest.Length && (char.IsAsciiLetterOrDigit(rest[end]) || rest[end] is '-' or '.'))
        {
            bool isDot = rest[end] == '.';
            bool followsNoName = end == scheme || rest[end - 1] == '.';
            isHostWellFormed &= !(isDot && followsNoName);
            dots += isDot ? 1 : 0;
            end++;
        }

        if (end == scheme || dots == 0 || !isHostWellFormed || rest[end - 1] == '.')
        {
            return 0;
        }

        if (end < rest.Length && rest[end] is '/' or '?' or '#')
        {
            while (end < rest.Length && !char.IsWhiteSpace(rest[end]) && rest[end] is not ('<' or '>' or ')' or '`'))
            {
                end++;
            }
        }

        return end;
    }

    /// <summary>Measures a web scheme at the start of a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The length of <c>https://</c> or <c>http://</c>, or 0 when the text starts with
    /// neither.</returns>
    private static int SchemeLength(ReadOnlySpan<char> text)
    {
        foreach (string scheme in LinkSchemes)
        {
            if (text.StartsWith(scheme, StringComparison.Ordinal))
            {
                return scheme.Length;
            }
        }

        return 0;
    }

    /// <summary>Tells whether a character is ASCII punctuation, which a backslash in front of it
    /// turns into the plain character.</summary>
    /// <param name="character">The character after a backslash.</param>
    /// <returns>Whether a backslash escapes it.</returns>
    private static bool IsAsciiPunctuation(char character) =>
        character is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    /// <summary>Counts the backticks in a row from a position.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">The position to count from.</param>
    /// <returns>How many backticks follow one another there; 0 when there is none.</returns>
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
    /// length that opened it. Inside code a backslash escapes nothing.</summary>
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
