using System.Globalization;
using System.Text;

namespace CalCrony.ReleaseNotes;

/// <summary>The comment the release workflow keeps on the open release pull request: what the
/// release will announce, or that no summary has been written for it yet. It blocks nothing.</summary>
public static class PreviewComment
{
    /// <summary>The first line of the comment, by which the workflow finds it again to rewrite
    /// it in place.</summary>
    public const string Marker = "<!-- release-summary-preview -->";

    private const string Rule = "---";

    /// <summary>Writes the comment for a release about to be made.</summary>
    /// <param name="preview">What the checkout holds for the release.</param>
    /// <returns>The comment as Markdown.</returns>
    public static string Build(ReleasePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        StringBuilder comment = new();
        comment.Append(CultureInfo.InvariantCulture, $"{Marker}\n### Release summary for {preview.Version}\n\n");

        if (preview.HasSummary)
        {
            AppendSummary(comment, preview);
        }
        else
        {
            AppendNoSummary(comment, preview);
        }

        AppendLeftovers(comment, preview);
        comment.Append("This comment is rewritten on every push to `master`.");

        return comment.ToString();
    }

    /// <summary>Appends the summary as it will go out, and what the rules found wrong with it.</summary>
    /// <param name="comment">The comment being written.</param>
    /// <param name="preview">A preview that has a summary.</param>
    private static void AppendSummary(StringBuilder comment, ReleasePreview preview)
    {
        string path = ReleaseSummary.PathFor(preview.Version);
        string text = preview.Summary!.Text;

        comment.Append(CultureInfo.InvariantCulture, $"`{path}` is on `master` ({text.Length:N0} of {SummaryRules.MaxLength:N0} characters). ");
        comment.Append("When this pull request is merged, the text between the two rules goes on top of the release notes and into the Discord announcement, under its usual first line.\n\n");
        comment.Append(CultureInfo.InvariantCulture, $"{Rule}\n\n{text}\n\n{Rule}\n\n");

        if (preview.Problems.Count > 0)
        {
            comment.Append("**The check found problems with the file.** Fix them through a pull request before merging this one:\n\n");

            foreach (SummaryProblem problem in preview.Problems)
            {
                string place = problem.Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"line {problem.Line}") : "the file";
                comment.Append(CultureInfo.InvariantCulture, $"- {place}: {problem.Message}\n");
            }

            comment.Append('\n');
        }

        if (text.Length > SummaryRules.MaxLength)
        {
            comment.Append("As it stands, Discord gets it cut at a line end, with a pointer to the release notes.\n\n");
        }

        comment.Append("To change the text, change the file through a pull request before merging this one.\n\n");
    }

    /// <summary>Appends what happens without a summary and how to add one.</summary>
    /// <param name="comment">The comment being written.</param>
    /// <param name="preview">A preview without a summary, or with an empty one.</param>
    private static void AppendNoSummary(StringBuilder comment, ReleasePreview preview)
    {
        string path = ReleaseSummary.PathFor(preview.Version);
        string state = preview.Summary is null ? "is not on `master`" : "is empty";

        comment.Append(CultureInfo.InvariantCulture, $"No summary has been written for this release: `{path}` {state}. ");
        comment.Append("Merged as it is, the release goes out as it always has: the changelog lines as release notes, and a Discord announcement with the link alone.\n\n");
        comment.Append("To add one, merge a pull request that adds that file before merging this one. `CONTRIBUTING.md` (\"Release summary\") says what goes in it.\n\n");
    }

    /// <summary>Appends the summaries that look left behind, when there are any.</summary>
    /// <param name="comment">The comment being written.</param>
    /// <param name="preview">The preview.</param>
    private static void AppendLeftovers(StringBuilder comment, ReleasePreview preview)
    {
        if (preview.Leftovers.Count == 0)
        {
            return;
        }

        comment.Append(CultureInfo.InvariantCulture, $"**Left over.** These summaries are for neither the last release nor this one. If one was meant for this release, rename it to `{preview.Version}.md`:\n\n");

        foreach (ReleaseVersion leftover in preview.Leftovers)
        {
            comment.Append(CultureInfo.InvariantCulture, $"- `{ReleaseSummary.PathFor(leftover)}`\n");
        }

        comment.Append('\n');
    }
}
