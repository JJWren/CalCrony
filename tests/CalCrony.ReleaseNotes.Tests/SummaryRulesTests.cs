namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The rules a release summary keeps to. Each rule has a text that breaks it and nothing
/// else, so a rule taken out of the check fails its own test; the things a summary is meant to
/// hold — commands in backticks, links, lists — pass; and the ways the reviews found to get a
/// mention past the check on GitHub are each refused by name.</summary>
public class SummaryRulesTests
{
    private const string Opening = "### What's new\n\n- One change.\n\n";

    private const string Good = """
        ### What's new

        - **Free slots.** `/availability slots` lists the times a member is free.
        - A second change, with a [link](https://calcrony.app/docs) and <https://calcrony.app>.
        - `/availability slots`

        ### How to use it

        1. Run `/availability slots who:@GM`.
        2. Add ``days:14`` to look further ahead.

        > Only you see the reply.
        """;

    [Fact]
    public void The_intended_shape_has_no_problems()
    {
        Assert.Empty(Check(Good));
    }

    [Fact]
    public void A_fix_only_release_needs_one_bullet_and_no_how_to()
    {
        Assert.Empty(Check("### What's new\n\n- Dates typed in words are limited to 100 characters."));
    }

    [Fact]
    public void An_asterisk_bullet_counts_as_a_bullet()
    {
        Assert.Empty(Check("### What's new\n\n* One change."));
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        SummaryProblem problem = Assert.Single(Check(string.Empty));

        Assert.Equal(0, problem.Line);
        Assert.Equal("The file is empty.", problem.Message);
        Assert.False(problem.IsLength);
    }

    [Theory]
    [InlineData("## What's new")]
    [InlineData("### What’s new")]
    [InlineData("### Whats new")]
    [InlineData("### What's New")]
    [InlineData("### What's new ")]
    [InlineData("What's new")]
    [InlineData("- ### What's new")]
    public void The_first_line_is_the_whats_new_heading_exactly(string firstLine)
    {
        SummaryProblem problem = Assert.Single(Check($"{firstLine}\n\n- One change."));

        Assert.Equal(1, problem.Line);
        Assert.Equal(
            "The first line must be exactly `### What's new`: a straight apostrophe, nothing before it and nothing after.",
            problem.Message);
    }

    [Theory]
    [InlineData("### Notes")]
    [InlineData("# Big")]
    [InlineData("## Medium")]
    [InlineData("#### Small")]
    [InlineData("###")]
    [InlineData("### How to use it ")]
    [InlineData("### How To Use It")]
    [InlineData("   ## Indented")]
    [InlineData("- # Big, inside a bullet")]
    [InlineData("1. ## Inside a numbered step")]
    [InlineData("> ### Inside a quote")]
    [InlineData("- > - # Nested three deep")]
    [InlineData("- ### How to use it")]
    [InlineData("-\t# After a hyphen and a tab")]
    [InlineData("- \t# After a hyphen, a space and a tab")]
    [InlineData("+ # Under a plus bullet")]
    [InlineData("1) ## Under a numbered step written with a bracket")]
    [InlineData("1.\t# After a number and a tab")]
    [InlineData("\t## After a tab")]
    public void No_heading_but_the_two_is_allowed(string heading)
    {
        SummaryProblem problem = Assert.Single(Check($"{Opening}{heading}\n\nText."));

        Assert.Equal(5, problem.Line);
        Assert.StartsWith("Only two headings are allowed", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_how_to_heading_comes_once()
    {
        SummaryProblem problem = Assert.Single(
            Check($"{Opening}### How to use it\n\n1. A step.\n\n### How to use it\n\n1. Again."));

        Assert.Equal(9, problem.Line);
        Assert.StartsWith("Only two headings are allowed", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("### What's new\n\nA paragraph and no list.")]
    [InlineData("### What's new\n\n1. A numbered step is not a bullet.")]
    [InlineData("### What's new\n\n### How to use it\n\n- A bullet, but under the wrong heading.")]
    public void Whats_new_needs_a_bullet_of_its_own(string text)
    {
        SummaryProblem problem = Assert.Single(Check(text));

        Assert.Equal(1, problem.Line);
        Assert.Contains("needs at least one bullet", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("| Command | What it does |", "Tables do not render in Discord")]
    [InlineData("  | a | b |", "Tables do not render in Discord")]
    [InlineData("--- | ---", "Tables do not render in Discord")]
    [InlineData(":-- | --:", "Tables do not render in Discord")]
    [InlineData("> | quoted | table |", "Tables do not render in Discord")]
    [InlineData("See ![the grid](https://calcrony.app/grid.png).", "Images do not show in Discord")]
    [InlineData("---", "A line of only dashes or equals signs")]
    [InlineData("***", "A line of only dashes or equals signs")]
    [InlineData("___", "A line of only dashes or equals signs")]
    [InlineData("===", "A line of only dashes or equals signs")]
    [InlineData("- - -", "A line of only dashes or equals signs")]
    [InlineData("=", "A line of only dashes or equals signs")]
    [InlineData("--", "A line of only dashes or equals signs")]
    [InlineData("-", "A line of only dashes or equals signs")]
    [InlineData("> ---", "A line of only dashes or equals signs")]
    [InlineData("> ==", "A line of only dashes or equals signs")]
    [InlineData("- > --", "A line of only dashes or equals signs")]
    [InlineData("Line one.<br>Line two.", "`<` outside backticks")]
    [InlineData("<details>More</details>", "`<` outside backticks")]
    [InlineData("<!-- a note to self -->", "`<` outside backticks")]
    [InlineData("Starts <t:1700000000:R>.", "`<` outside backticks")]
    [InlineData("Posted in <#123456789012345678>.", "`<` outside backticks")]
    [InlineData("Use </availability slots:123> to try it.", "`<` outside backticks")]
    [InlineData("Slots < 60 minutes are left out.", "`<` outside backticks")]
    [InlineData("A link cut short: <https://calcrony.app/docs and more.", "`<` outside backticks")]
    [InlineData("Not a web link: <mailto:someone>.", "`<` outside backticks")]
    [InlineData("Not a link, with a space in it: <https://calcrony.app/docs and more>.", "`<` outside backticks")]
    [InlineData("A tag inside what looks like a link: <https://calcrony.app/<b> and on.", "`<` outside backticks")]
    [InlineData("Fish &amp; chips.", "An HTML entity")]
    [InlineData("Thanks to @everyone who tested.", "`@` followed by a name")]
    [InlineData("Run /availability slots who:@GM to see it.", "`@` followed by a name")]
    [InlineData("Write to help@calcrony.app.", "`@` followed by a name")]
    [InlineData("A name that starts with an underscore: @_someone.", "`@` followed by a name")]
    [InlineData("An address that names someone before the host: https://someone@calcrony.app/docs.", "`@` followed by a name")]
    [InlineData("A bare address is text like any other: https://example.com/@someone.", "`@` followed by a name")]
    [InlineData("So is the address of a link: [the channel](https://example.com/@someone).", "`@` followed by a name")]
    [InlineData("And an address in square brackets: [https://example.com/@someone].", "`@` followed by a name")]
    [InlineData("A link's text is still text: [@someone](https://calcrony.app).", "`@` followed by a name")]
    [InlineData("A name after a link's closing bracket: [docs](https://calcrony.app/docs)@someone.", "`@` followed by a name")]
    [InlineData("An entity in an address: https://example.com/&#64;someone.", "An HTML entity")]
    [InlineData("See https://example.com/a`b` for more.", "A backtick glued to a web address or to a `$`")]
    [InlineData("See www.example.com/a`b` for more.", "A backtick glued to a web address or to a `$`")]
    [InlineData("See WWW.EXAMPLE.COM/a`b` for more.", "A backtick glued to a web address or to a `$`")]
    [InlineData("See ftp://example.com/a`b` for more.", "A backtick glued to a web address or to a `$`")]
    [InlineData("Code first, `like this`, then https://example.com/a`b` after it.", "A backtick glued to a web address or to a `$`")]
    [InlineData("A formula: $`a + b`$.", "A backtick glued to a web address or to a `$`")]
    [InlineData("[^1]: A footnote.", "Footnotes and alert boxes")]
    [InlineData("[^1]: # A heading inside a footnote", "Footnotes and alert boxes")]
    [InlineData("> [!WARNING]", "Footnotes and alert boxes")]
    [InlineData("```", "Code blocks are not allowed")]
    [InlineData("```text", "Code blocks are not allowed")]
    [InlineData("~~~", "Code blocks are not allowed")]
    [InlineData("- ```", "Code blocks are not allowed")]
    [InlineData("An unclosed `code span.", "A backtick with no partner")]
    [InlineData("Two ticks open ``and one closes`.", "A backtick with no partner")]
    public void Text_that_one_side_would_show_differently_is_refused(string line, string message)
    {
        SummaryProblem problem = Assert.Single(Check($"{Opening}{line}"));

        Assert.Equal(5, problem.Line);
        Assert.StartsWith(message, problem.Message, StringComparison.Ordinal);
        Assert.False(problem.IsLength);
    }

    [Theory]
    [InlineData("```/availability slots``` thanks @someone")]
    [InlineData("\\` @someone \\`")]
    [InlineData("<https://example.com/`> @someone `")]
    [InlineData("See <https://example.com/`> and `code`.")]
    [InlineData("&#64;someone")]
    [InlineData("&#x40;someone")]
    [InlineData("&commat;someone")]
    [InlineData("Ask <@123456789012345678> or <@&1543943300132769802>.")]
    [InlineData("An unclosed `code span with @someone in it.")]
    [InlineData("See https://example.com/a`b @someone `c` for more.")]
    [InlineData("See www.example.com/a`b @someone `c` for more.")]
    [InlineData("[https://example.com/@someone]")]
    [InlineData("[a bracket that never closes, then https://example.com/@someone")]
    [InlineData("$`a @someone b`$")]
    public void The_ways_found_to_get_a_mention_past_the_check_are_refused(string line)
    {
        // Each of these is, or leads to, a live mention on GitHub. What matters is that the line
        // is refused; several break more than one rule.
        IReadOnlyList<SummaryProblem> problems = Check($"{Opening}{line}");

        Assert.NotEmpty(problems);
        Assert.All(problems, problem => Assert.Equal(5, problem.Line));
        Assert.True(SummaryRules.HoldSummaryBack(problems));
    }

    [Fact]
    public void A_dash_and_a_space_under_a_line_of_text_is_an_underline_too()
    {
        // GitHub reads the line above it as a heading; read as a list marker, the line is empty.
        SummaryProblem problem = Assert.Single(Check($"{Opening}A title\n- \n\nMore text."));

        Assert.Equal(6, problem.Line);
        Assert.StartsWith("A line of only dashes or equals signs", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_that_runs_over_a_line_end_is_refused_where_it_opens()
    {
        // On GitHub the code opened on the first line closes on the second, which leaves the name
        // after it as text. Read by itself the second line looks harmless, so the first is refused.
        IReadOnlyList<SummaryProblem> problems = Check($"{Opening}A span that opens here `and\ncloses here` @someone `more");

        SummaryProblem problem = Assert.Single(problems);
        Assert.Equal(5, problem.Line);
        Assert.StartsWith("A backtick with no partner", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_block_cannot_hide_text_from_the_check()
    {
        // Lines the first version of the check skipped as the inside of a code block.
        IReadOnlyList<SummaryProblem> problems = Check($"{Opening}```text\n```js\n```\n@someone\n```text\n```js\n```");

        Assert.Equal([5, 6, 7, 8, 9, 10, 11], problems.Select(problem => problem.Line));
        Assert.StartsWith("`@` followed by a name", problems[3].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Run `/availability slots who:@GM`.")]
    [InlineData("Run ``who: ` @GM`` — a lone backtick inside a double pair does not end the code.")]
    [InlineData("Run ```who:@GM``` — three backticks in the middle of a line are still code.")]
    [InlineData("  - An indented bullet, with `who:@GM` in code.")]
    [InlineData("- `/availability slots`")]
    [InlineData("#hashtag at the start of a line is text, not a heading.")]
    [InlineData("- #1 on the list is not a heading either.")]
    [InlineData("The reply reads `<t:1700000000:R>` as a time.")]
    [InlineData("More at <https://calcrony.app/docs> and <http://localhost:8080>.")]
    [InlineData("The docs: [calcrony.app/docs](https://calcrony.app/docs).")]
    [InlineData("- Watch <https://example.com/@someone>.")]
    [InlineData("- Watch [the channel](<https://example.com/@someone>).")]
    [InlineData("**Bold**, *italic* and ~~struck~~ text.")]
    [InlineData("> A quoted line.")]
    [InlineData("An @ sign by itself, and one at the end @")]
    [InlineData("A table-like line inside code: `| a | b |`.")]
    [InlineData("Either this | or that, in a sentence.")]
    [InlineData("Questions & answers, R&D, A&B; and a semicolon; none of it an entity.")]
    [InlineData("An address with a port, written whole in angle brackets: <https://example.com:8443/@someone>.")]
    [InlineData("A bare address, then code after a space: https://calcrony.app/docs `/availability slots`.")]
    [InlineData("A price of $5, and `$5` in code.")]
    [InlineData("Open `http://localhost:8080` in a browser.")]
    [InlineData("Run `curl https://example.com/health` to check.")]
    [InlineData("Set `API_BASE_URL=https://example.com` in the file.")]
    [InlineData("See `www.example.com` for more.")]
    [InlineData("It costs `5$` a month, and the pattern `^abc$` matches.")]
    [InlineData("The link looks like `https://calcrony.app/c/<your-link>`.")]
    [InlineData("An address with a name in it, as code: `https://example.com/@someone`.")]
    [InlineData("Code right after a link in angle brackets: <https://example.com/x>`who:@GM`.")]
    [InlineData("A bare address, a tab, then code: https://calcrony.app/docs\t`/availability slots`.")]
    [InlineData("An escaped backtick \\` stays one character, and `code` after it is still code.")]
    [InlineData("\tA line that starts with a tab.")]
    public void What_a_summary_is_meant_to_hold_passes(string line)
    {
        Assert.Empty(Check($"{Opening}{line}"));
    }

    [Theory]
    [InlineData(0x202E)]
    [InlineData(0x202A)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0x200B)]
    [InlineData(0x200E)]
    [InlineData(0x2060)]
    [InlineData(0xFEFF)]
    [InlineData(0x00AD)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x0007)]
    [InlineData(0x001B)]
    [InlineData(0x007F)]
    [InlineData(0x009F)]
    [InlineData(0xE0001)]
    [InlineData(0xE0067)]
    public void Characters_nobody_can_see_in_review_are_refused(int codePoint)
    {
        string line = $"A character nobody can see: {char.ConvertFromUtf32(codePoint)}.";

        SummaryProblem problem = Assert.Single(Check($"{Opening}{line}"));

        Assert.Equal(5, problem.Line);
        Assert.Equal($"An invisible character (U+{codePoint:X4}); remove it.", problem.Message);
    }

    [Fact]
    public void An_invisible_character_is_refused_inside_code_too()
    {
        SummaryProblem problem = Assert.Single(Check($"{Opening}Run `/slots{char.ConvertFromUtf32(0x202E)}`."));

        Assert.Equal(5, problem.Line);
        Assert.StartsWith("An invisible character (U+202E)", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_joiner_that_emoji_are_built_with_is_allowed()
    {
        string joiner = char.ConvertFromUtf32(0x200D);

        Assert.Empty(Check($"{Opening}Emoji are fine: 🎉 and 👨{joiner}👩{joiner}👧."));
    }

    [Fact]
    public void A_summary_may_be_exactly_as_long_as_the_limit()
    {
        Assert.Empty(Check(TextOfLength(SummaryRules.MaxLength)));
    }

    [Fact]
    public void One_character_over_the_limit_is_a_problem_of_length_alone()
    {
        IReadOnlyList<SummaryProblem> problems = Check(TextOfLength(SummaryRules.MaxLength + 1));
        SummaryProblem problem = Assert.Single(problems);

        Assert.Equal(0, problem.Line);
        Assert.True(problem.IsLength);
        Assert.Equal(
            "The summary is 1,801 characters; the limit is 1,800, which keeps the Discord announcement inside Discord's 2,000.",
            problem.Message);
        Assert.False(SummaryRules.HoldSummaryBack(problems));
    }

    [Fact]
    public void A_character_that_takes_two_counts_twice()
    {
        // Two UTF-16 units each: counting them that way can only overstate what Discord counts.
        string text = "### What's new\n\n- " + string.Concat(Enumerable.Repeat("🎉", 900));

        Assert.Equal(1818, text.Length);
        Assert.Contains(Check(text), problem => problem.IsLength);
    }

    [Fact]
    public void Only_a_problem_other_than_length_holds_a_summary_back()
    {
        Assert.False(SummaryRules.HoldSummaryBack([]));
        Assert.False(SummaryRules.HoldSummaryBack(Check(TextOfLength(SummaryRules.MaxLength + 50))));
        Assert.True(SummaryRules.HoldSummaryBack(Check(TextOfLength(SummaryRules.MaxLength + 50) + "\n\n| a table |")));
        Assert.True(SummaryRules.HoldSummaryBack(Check(string.Empty)));
    }

    [Fact]
    public void Problems_come_in_the_order_of_their_lines()
    {
        IReadOnlyList<SummaryProblem> problems = Check("Wrong first line\n\n| a table |\n\n@someone");

        Assert.Equal([1, 1, 3, 5], problems.Select(problem => problem.Line));
    }

    [Fact]
    public void Line_ends_of_any_kind_are_read_as_line_ends()
    {
        Assert.Empty(Check(Good.ReplaceLineEndings("\r\n")));
        Assert.Empty(Check(Good.ReplaceLineEndings("\r")));

        SummaryProblem problem = Assert.Single(Check("### What's new\r\r- One change.\r\r@someone"));
        Assert.Equal(5, problem.Line);
    }

    /// <summary>Checks a text the way the tool does: normalized first.</summary>
    /// <param name="text">The summary's text.</param>
    /// <returns>The problems the rules find.</returns>
    private static IReadOnlyList<SummaryProblem> Check(string text) =>
        SummaryRules.Check(ReleaseSummary.Normalize(text));

    /// <summary>Builds a summary that keeps every rule and has a given length.</summary>
    /// <param name="length">The number of characters wanted.</param>
    /// <returns>The opening heading and one long bullet.</returns>
    private static string TextOfLength(int length)
    {
        const string Start = "### What's new\n\n- ";
        return Start + new string('a', length - Start.Length);
    }
}
