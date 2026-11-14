namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The rules a release summary keeps to. Each rule has a text that breaks it and nothing
/// else, so a rule taken out of the check fails its own test; and the things a summary is meant to
/// contain — commands in backticks, links, a code block — pass.</summary>
public class SummaryRulesTests
{
    private const string Good = """
        ### What's new

        - **Free slots.** `/availability slots` lists the times a member is free.
        - A second change, with a [link](https://calcrony.app/docs) and <https://calcrony.app>.

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
    }

    [Theory]
    [InlineData("## What's new")]
    [InlineData("### What’s new")]
    [InlineData("### Whats new")]
    [InlineData("### What's New")]
    [InlineData("What's new")]
    public void The_first_line_is_the_whats_new_heading_exactly(string firstLine)
    {
        SummaryProblem problem = Assert.Single(Check($"{firstLine}\n\n- One change."));

        Assert.Equal(1, problem.Line);
        Assert.Equal("The first line must be `### What's new`.", problem.Message);
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
    public void No_heading_but_the_two_is_allowed(string heading)
    {
        SummaryProblem problem = Assert.Single(Check($"### What's new\n\n- One change.\n\n{heading}\n\nText."));

        Assert.Equal(5, problem.Line);
        Assert.StartsWith("Only two headings are allowed", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_how_to_heading_comes_once()
    {
        SummaryProblem problem = Assert.Single(
            Check("### What's new\n\n- One change.\n\n### How to use it\n\n1. A step.\n\n### How to use it\n\n1. Again."));

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
    [InlineData("See ![the grid](https://calcrony.app/grid.png).", "Images do not show in Discord")]
    [InlineData("---", "A line of dashes")]
    [InlineData("***", "A line of dashes")]
    [InlineData("___", "A line of dashes")]
    [InlineData("===", "A line of dashes")]
    [InlineData("- - -", "A line of dashes")]
    [InlineData("Line one.<br>Line two.", "`<` outside backticks")]
    [InlineData("<details>More</details>", "`<` outside backticks")]
    [InlineData("<!-- a note to self -->", "`<` outside backticks")]
    [InlineData("Starts <t:1700000000:R>.", "`<` outside backticks")]
    [InlineData("Posted in <#123456789012345678>.", "`<` outside backticks")]
    [InlineData("Use </availability slots:123> to try it.", "`<` outside backticks")]
    [InlineData("Slots < 60 minutes are left out.", "`<` outside backticks")]
    [InlineData("Thanks to @everyone who tested.", "`@` followed by a name")]
    [InlineData("Run /availability slots who:@GM to see it.", "`@` followed by a name")]
    [InlineData("Write to help@calcrony.app.", "`@` followed by a name")]
    [InlineData("An unclosed `code span with @GM in it.", "`@` followed by a name")]
    public void Text_that_one_side_would_show_differently_is_refused(string line, string message)
    {
        SummaryProblem problem = Assert.Single(Check($"### What's new\n\n- One change.\n\n{line}"));

        Assert.Equal(5, problem.Line);
        Assert.StartsWith(message, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_discord_mention_written_out_breaks_both_rules()
    {
        IReadOnlyList<SummaryProblem> problems = Check("### What's new\n\n- One change.\n\nAsk <@123456789012345678> or <@&1543943300132769802>.");

        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.Equal(5, problem.Line));
        Assert.Contains(problems, problem => problem.Message.StartsWith("`<` outside backticks", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Message.StartsWith("`@` followed by a name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Run `/availability slots who:@GM`.")]
    [InlineData("Run ``who: ` @GM`` — a lone backtick inside a double pair does not end the code.")]
    [InlineData("#hashtag at the start of a line is text, not a heading.")]
    [InlineData("  - An indented bullet, with `who:@GM` in code.")]
    [InlineData("The reply reads `<t:1700000000:R>` as a time.")]
    [InlineData("More at <https://calcrony.app/docs> and <http://localhost:8080>.")]
    [InlineData("The docs: [calcrony.app/docs](https://calcrony.app/docs).")]
    [InlineData("**Bold**, *italic* and ~~struck~~ text.")]
    [InlineData("> A quoted line.")]
    [InlineData("An @ sign by itself, and one at the end @")]
    [InlineData("A table-like line inside code: `| a | b |`.")]
    [InlineData("\tA line that starts with a tab.")]
    [InlineData("Emoji are fine: 🎉 and 👨\u200D👩\u200D👧.")]
    public void What_a_summary_is_meant_to_hold_passes(string line)
    {
        Assert.Empty(Check($"### What's new\n\n- One change.\n\n{line}"));
    }

    [Fact]
    public void A_code_block_may_hold_anything()
    {
        string text = "### What's new\n\n- One change.\n\n```\n# not a heading\n| not | a table |\n<not html> @nobody ![not an image]\n---\n```";

        Assert.Empty(Check(text));
    }

    [Fact]
    public void A_code_block_has_to_be_closed()
    {
        SummaryProblem problem = Assert.Single(Check("### What's new\n\n- One change.\n\n```\n/availability slots"));

        Assert.Equal(5, problem.Line);
        Assert.Equal("The code block opened here is never closed.", problem.Message);
    }

    [Theory]
    [InlineData("A right-to-left override: \u202E.", "U+202E")]
    [InlineData("An isolate: \u2066.", "U+2066")]
    [InlineData("A bell: \u0007.", "U+0007")]
    [InlineData("An escape: \u001B[31m.", "U+001B")]
    public void Characters_nobody_can_see_in_review_are_refused(string line, string codePoint)
    {
        SummaryProblem problem = Assert.Single(Check($"### What's new\n\n- One change.\n\n{line}"));

        Assert.Equal(5, problem.Line);
        Assert.Equal($"An invisible control character ({codePoint}); remove it.", problem.Message);
    }

    [Fact]
    public void An_invisible_character_is_refused_inside_a_code_block_too()
    {
        SummaryProblem problem = Assert.Single(Check("### What's new\n\n- One change.\n\n```\n\u202E\n```"));

        Assert.Equal(6, problem.Line);
    }

    [Fact]
    public void A_summary_may_be_exactly_as_long_as_the_limit()
    {
        Assert.Empty(Check(TextOfLength(SummaryRules.MaxLength)));
    }

    [Fact]
    public void One_character_over_the_limit_is_refused()
    {
        SummaryProblem problem = Assert.Single(Check(TextOfLength(SummaryRules.MaxLength + 1)));

        Assert.Equal(0, problem.Line);
        Assert.Equal(
            "The summary is 1,801 characters; the limit is 1,800, which keeps the Discord announcement inside Discord's 2,000.",
            problem.Message);
    }

    [Fact]
    public void A_character_that_takes_two_counts_twice()
    {
        // Two UTF-16 units each: counting them that way can only overstate what Discord counts.
        string text = "### What's new\n\n- " + string.Concat(Enumerable.Repeat("🎉", 900));

        Assert.Equal(1818, text.Length);
        Assert.Contains(Check(text), problem => problem.Line == 0);
    }

    [Fact]
    public void Problems_come_in_the_order_of_their_lines()
    {
        IReadOnlyList<SummaryProblem> problems = Check("Wrong first line\n\n| a table |\n\n@someone");

        Assert.Equal([1, 1, 3, 5], problems.Select(problem => problem.Line));
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
