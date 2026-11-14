namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The comment on the open release pull request: what the release will announce, or that
/// nothing has been written for it, and summaries that look left behind.</summary>
public class PreviewCommentTests
{
    private const string Summary = "### What's new\n\n- One change.";
    private const string ChangelogAt0370 = "# Changelog\n\n## [0.37.0](https://example.test) (2026-11-13)\n";

    private static readonly ReleaseVersion Next = new(0, 38, 0);

    [Fact]
    public void With_a_summary_the_comment_shows_it_as_code()
    {
        using TemporaryCheckout checkout = new();
        checkout.Write(Changelog.FileName, ChangelogAt0370);
        checkout.WriteSummary("0.38.0", Summary + "\n");

        string comment = PreviewComment.Build(ReleasePreview.Gather(checkout.Root, Next));

        Assert.Equal(
            """
            <!-- release-summary-preview -->
            ### Release summary for 0.38.0

            `docs/release-notes/0.38.0.md` is on `master` (29 of 1,800 characters). When this pull request is merged, this text goes on top of the release notes and into the Discord announcement, under its usual first line. It is shown here as code, not as it will look:

            ```text
            ### What's new

            - One change.
            ```

            At release time GitHub is asked whether the text mentions anyone, and a summary that does is left out. To change the text, change the file through a pull request before merging this one.

            This comment is rewritten on every push to `master`.
            """.ReplaceLineEndings("\n"),
            comment);
    }

    [Fact]
    public void Nothing_in_a_summary_that_goes_out_can_close_the_code_it_is_shown_as()
    {
        // A comment notifies whoever it mentions the moment it is posted, so the summary is never
        // rendered in it: the fence is one backtick longer than the longest run inside it.
        using TemporaryCheckout checkout = new();
        string summary = "### What's new\n\n- Run ```who:@GM``` to see it.";
        checkout.WriteSummary("0.38.0", summary);

        ReleasePreview preview = ReleasePreview.Gather(checkout.Root, Next);
        string comment = PreviewComment.Build(preview);

        Assert.False(preview.IsHeldBack);
        Assert.Contains($"not as it will look:\n\n````text\n{summary}\n````\n\nAt release time", comment, StringComparison.Ordinal);
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
    public void A_summary_that_is_only_too_long_is_shown_with_what_becomes_of_it()
    {
        using TemporaryCheckout checkout = new();
        string summary = "### What's new\n\n- " + new string('a', 1800);
        checkout.WriteSummary("0.38.0", summary);

        ReleasePreview preview = ReleasePreview.Gather(checkout.Root, Next);
        string comment = PreviewComment.Build(preview);

        Assert.True(preview.HasSummary);
        Assert.False(preview.IsHeldBack);
        Assert.Contains(
            $"not as it will look:\n\n```text\n{summary}\n```\n\n**The file is too long.** As it stands the release notes get all of it, and Discord gets it cut short, with a pointer to the release notes:\n\n- the file: The summary is 1,818 characters; the limit is 1,800",
            comment,
            StringComparison.Ordinal);
        Assert.Contains("inside Discord's 2,000.\n\nAt release time GitHub is asked", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_the_check_refuses_is_shown_as_code_with_what_is_wrong()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "### What's new\n\n- One change.\n\nThanks @everyone.\n");

        ReleasePreview preview = ReleasePreview.Gather(checkout.Root, Next);
        string comment = PreviewComment.Build(preview);

        Assert.True(preview.IsHeldBack);
        Assert.Equal(
            """
            <!-- release-summary-preview -->
            ### Release summary for 0.38.0

            `docs/release-notes/0.38.0.md` is on `master`, but **the check refuses it, so as it stands this release goes out without a summary**: the changelog lines as release notes, and a Discord announcement with the link alone. What the check found:

            - line 5: `@` followed by a name outside backticks is a mention: GitHub notifies the user of that name. Put it in backticks, or write a web address that has it whole in angle brackets: `<https://…>`.

            The file as it is:

            ```text
            ### What's new

            - One change.

            Thanks @everyone.
            ```

            To have the summary go out, fix the file through a pull request before merging this one.

            This comment is rewritten on every push to `master`.
            """.ReplaceLineEndings("\n"),
            comment);
    }

    [Fact]
    public void Nothing_in_a_refused_summary_can_close_the_code_it_is_shown_as()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "### What's new\n\n- One change.\n\n````\n@everyone\n````");

        string comment = PreviewComment.Build(ReleasePreview.Gather(checkout.Root, Next));

        // The fence is one backtick longer than the longest run inside it.
        Assert.Contains("\n\n`````text\n### What's new\n", comment, StringComparison.Ordinal);
        Assert.Contains("\n````\n@everyone\n````\n`````\n\n", comment, StringComparison.Ordinal);
    }
}
