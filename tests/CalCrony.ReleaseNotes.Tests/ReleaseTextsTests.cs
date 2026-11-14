namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The two texts a summary becomes. On GitHub it sits on top of release-please's notes,
/// once, however often the job runs. In Discord it is the file as written; a file over the limit
/// — which the check refuses, so this is the last line of defence — is cut cleanly rather than
/// costing the release its announcement.</summary>
public class ReleaseTextsTests
{
    private const string Summary = "### What's new\n\n- One change.";
    private const string Notes = "## [0.38.0](https://example.test/compare) (2026-12-01)\n\n\n### Features\n\n* one change (#1)";

    [Fact]
    public void The_summary_goes_on_top_then_a_rule_then_the_notes_untouched()
    {
        Assert.Equal($"{Summary}\n\n---\n\n{Notes}", ReleaseTexts.ReleaseNotes(Summary, Notes));
    }

    [Fact]
    public void Notes_that_already_carry_the_summary_are_left_alone()
    {
        string once = ReleaseTexts.ReleaseNotes(Summary, Notes);

        Assert.Equal(once, ReleaseTexts.ReleaseNotes(Summary, once));
    }

    [Fact]
    public void Notes_arrive_with_any_line_ends_and_leave_with_line_feeds()
    {
        string fromWindows = "\r\n" + Notes.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

        Assert.Equal($"{Summary}\n\n---\n\n{Notes}", ReleaseTexts.ReleaseNotes(Summary, fromWindows));
    }

    [Fact]
    public void A_release_with_no_notes_gets_the_summary_alone()
    {
        Assert.Equal(Summary, ReleaseTexts.ReleaseNotes(Summary, "\n"));
    }

    [Fact]
    public void A_summary_inside_the_limit_goes_to_discord_as_written()
    {
        string atTheLimit = Summary + new string('a', SummaryRules.MaxLength - Summary.Length);

        Assert.Equal(Summary, ReleaseTexts.DiscordSummary(Summary));
        Assert.Equal(atTheLimit, ReleaseTexts.DiscordSummary(atTheLimit));
        Assert.Equal(string.Empty, ReleaseTexts.DiscordSummary(string.Empty));
    }

    [Fact]
    public void An_over_long_summary_is_cut_after_a_whole_line_and_points_to_the_notes()
    {
        string summary = "### What's new\n\n" + string.Join('\n', Enumerable.Range(1, 60).Select(number => $"- Change number {number}, described at some length."));

        string forDiscord = ReleaseTexts.DiscordSummary(summary);

        Assert.True(summary.Length > SummaryRules.MaxLength);
        Assert.True(forDiscord.Length <= SummaryRules.MaxLength);
        Assert.EndsWith($"\n\n{ReleaseTexts.Pointer}", forDiscord, StringComparison.Ordinal);

        string kept = forDiscord[..^(ReleaseTexts.Pointer.Length + 2)];
        Assert.StartsWith(kept, summary, StringComparison.Ordinal);
        Assert.Equal('\n', summary[kept.Length]);
        Assert.EndsWith("described at some length.", kept, StringComparison.Ordinal);

        // It keeps as much as fits: the next line would not have.
        string nextLine = summary[(kept.Length + 1)..].Split('\n')[0];
        Assert.True(kept.Length + 1 + nextLine.Length + 2 + ReleaseTexts.Pointer.Length > SummaryRules.MaxLength);
    }

    [Fact]
    public void A_cut_text_is_exactly_as_long_as_the_limit_allows_when_a_line_is_cut_inside()
    {
        string forDiscord = ReleaseTexts.DiscordSummary(new string('a', SummaryRules.MaxLength + 1));

        Assert.Equal(SummaryRules.MaxLength, forDiscord.Length);
    }

    [Fact]
    public void A_cut_never_leaves_a_heading_with_nothing_under_it()
    {
        // One long bullet does not fit as a whole line; dropping it would leave the heading alone.
        string summary = "### What's new\n\n- " + new string('a', SummaryRules.MaxLength);

        string forDiscord = ReleaseTexts.DiscordSummary(summary);

        Assert.True(forDiscord.Length <= SummaryRules.MaxLength);
        Assert.StartsWith("### What's new\n\n- aaaa", forDiscord, StringComparison.Ordinal);
        Assert.EndsWith($"a\n\n{ReleaseTexts.Pointer}", forDiscord, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_that_does_not_fit_after_other_content_is_left_out_whole()
    {
        string summary = "### What's new\n\n- A short change.\n- " + new string('a', SummaryRules.MaxLength);

        Assert.Equal($"### What's new\n\n- A short change.\n\n{ReleaseTexts.Pointer}", ReleaseTexts.DiscordSummary(summary));
    }

    [Fact]
    public void A_blank_line_left_at_the_cut_is_dropped()
    {
        // The second paragraph does not fit, so the cut falls on the blank line before it.
        string firstParagraph = "### What's new\n\n- " + new string('a', 1700);
        string summary = firstParagraph + "\n\n" + new string('b', 200);

        Assert.Equal($"{firstParagraph}\n\n{ReleaseTexts.Pointer}", ReleaseTexts.DiscordSummary(summary));
    }

    [Fact]
    public void A_single_line_over_the_limit_is_cut_inside_the_line()
    {
        string forDiscord = ReleaseTexts.DiscordSummary(new string('a', 3000));

        Assert.True(forDiscord.Length <= SummaryRules.MaxLength);
        Assert.StartsWith("aaaa", forDiscord, StringComparison.Ordinal);
        Assert.EndsWith($"a\n\n{ReleaseTexts.Pointer}", forDiscord, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public void A_cut_never_splits_a_character_that_takes_two(string lead)
    {
        // With and without one character in front, so the cut falls on either half of a pair.
        string forDiscord = ReleaseTexts.DiscordSummary(lead + string.Concat(Enumerable.Repeat("🎉", 1500)));
        string kept = forDiscord[..^(ReleaseTexts.Pointer.Length + 2)];

        Assert.True(forDiscord.Length <= SummaryRules.MaxLength);
        Assert.False(char.IsHighSurrogate(kept[^1]));
        Assert.Equal(lead.Length, kept.Length % 2);
    }
}
