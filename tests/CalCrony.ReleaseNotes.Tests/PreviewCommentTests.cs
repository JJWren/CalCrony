namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The comment on the open release pull request: what the release will announce, or that
/// nothing has been written for it, and summaries that look left behind.</summary>
public class PreviewCommentTests
{
    private const string Summary = "### What's new\n\n- One change.";
    private const string ChangelogAt0370 = "# Changelog\n\n## [0.37.0](https://example.test) (2026-11-13)\n";

    private static readonly ReleaseVersion Next = new(0, 38, 0);

    [Fact]
    public void With_a_summary_the_comment_shows_it_between_two_rules()
    {
        using TemporaryCheckout checkout = new();
        checkout.Write(Changelog.FileName, ChangelogAt0370);
        checkout.WriteSummary("0.38.0", Summary + "\n");

        string comment = PreviewComment.Build(ReleasePreview.Gather(checkout.Root, Next));

        Assert.Equal(
            """
            <!-- release-summary-preview -->
            ### Release summary for 0.38.0

            `docs/release-notes/0.38.0.md` is on `master` (29 of 1,800 characters). When this pull request is merged, the text between the two rules goes on top of the release notes and into the Discord announcement, under its usual first line.

            ---

            ### What's new

            - One change.

            ---

            To change the text, change the file through a pull request before merging this one.

            This comment is rewritten on every push to `master`.
            """.ReplaceLineEndings("\n"),
            comment);
    }

    [Fact]
    public void Without_a_summary_the_comment_says_what_happens_and_how_to_add_one()
    {
        using TemporaryCheckout checkout = new();
        checkout.Write(Changelog.FileName, ChangelogAt0370);

        string comment = PreviewComment.Build(ReleasePreview.Gather(checkout.Root, Next));

        Assert.Equal(
            """
            <!-- release-summary-preview -->
            ### Release summary for 0.38.0

            No summary has been written for this release: `docs/release-notes/0.38.0.md` is not on `master`. Merged as it is, the release goes out as it always has: the changelog lines as release notes, and a Discord announcement with the link alone.

            To add one, merge a pull request that adds that file before merging this one. `CONTRIBUTING.md` ("Release summary") says what goes in it.

            This comment is rewritten on every push to `master`.
            """.ReplaceLineEndings("\n"),
            comment);
    }

    [Fact]
    public void An_empty_file_counts_as_no_summary_and_the_comment_says_it_is_empty()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "\n\n");

        ReleasePreview preview = ReleasePreview.Gather(checkout.Root, Next);

        Assert.False(preview.HasSummary);
        Assert.Contains("`docs/release-notes/0.38.0.md` is empty.", PreviewComment.Build(preview), StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_for_a_number_the_release_did_not_get_is_named_as_left_over()
    {
        using TemporaryCheckout checkout = new();
        checkout.Write(Changelog.FileName, ChangelogAt0370);
        checkout.WriteSummary("0.36.4", Summary);
        checkout.WriteSummary("0.37.0", Summary);
        checkout.WriteSummary("0.37.1", Summary);
        checkout.WriteSummary("0.38.0", Summary);
        checkout.WriteSummary("0.39.0", Summary);

        ReleasePreview preview = ReleasePreview.Gather(checkout.Root, Next);
        string comment = PreviewComment.Build(preview);

        Assert.Equal([new ReleaseVersion(0, 37, 1), new ReleaseVersion(0, 39, 0)], preview.Leftovers);
        Assert.Contains(
            "**Left over.** These summaries are for neither the last release nor this one. If one was meant for this release, rename it to `0.38.0.md`:\n\n- `docs/release-notes/0.37.1.md`\n- `docs/release-notes/0.39.0.md`\n\nThis comment is rewritten",
            comment,
            StringComparison.Ordinal);
        Assert.DoesNotContain("0.36.4.md", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("0.37.0.md", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void A_left_over_summary_is_named_when_the_release_has_none_of_its_own()
    {
        using TemporaryCheckout checkout = new();
        checkout.Write(Changelog.FileName, ChangelogAt0370);
        checkout.WriteSummary("0.37.1", Summary);

        string comment = PreviewComment.Build(ReleasePreview.Gather(checkout.Root, Next));

        Assert.Contains("No summary has been written for this release", comment, StringComparison.Ordinal);
        Assert.Contains("- `docs/release-notes/0.37.1.md`", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_changelog_nothing_is_called_left_over()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.37.1", Summary);

        Assert.Empty(ReleasePreview.Gather(checkout.Root, Next).Leftovers);
    }

    [Fact]
    public void What_the_rules_find_is_listed_under_the_summary()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "### What's new\n\n- One change.\n\nThanks @everyone.\n\n" + new string('a', 1800));

        string comment = PreviewComment.Build(ReleasePreview.Gather(checkout.Root, Next));

        Assert.Contains("**The check found problems with the file.**", comment, StringComparison.Ordinal);
        Assert.Contains("- the file: The summary is 1,850 characters; the limit is 1,800", comment, StringComparison.Ordinal);
        Assert.Contains("- line 5: `@` followed by a name", comment, StringComparison.Ordinal);
        Assert.Contains("As it stands, Discord gets it cut at a line end", comment, StringComparison.Ordinal);
    }
}
