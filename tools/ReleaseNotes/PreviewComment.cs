using System.Globalization;
using System.Text;

namespace CalCrony.ReleaseNotes;

/// <summary>The comment the release workflow keeps on the open release pull request: what the
/// release will announce, or that no summary has been written for it yet. It blocks nothing. A
/// summary is always shown as code, never rendered: a comment notifies whoever it mentions the
/// moment it is written, and shown as code a summary can mention nobody, whatever it holds.</summary>
public static class PreviewComment
{
    /// <summary>The first line of the comment, by which the workflow finds it again to rewrite
    /// it in place.</summary>
    public const string Marker = "<!-- release-summary-preview -->";

    private const int MinFenceLength = 3;

    /// <summary>Writes the comment for a release about to be made.</summary>
    /// <param name="preview">What the checkout holds for the release.</param>
    /// <returns>The comment as Markdown.</returns>
    public static string Build(ReleasePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        StringBuilder comment = new();
        comment.Append(CultureInfo.InvariantCulture, $"{Marker}\n### Release summary for {preview.Version}\n\n");

        if (preview.IsHeldBack)
        {
            AppendHeldBack(comment, preview);
        }
        else if (preview.HasSummary)
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

    /// <summary>Appends the summary that will go out. The only thing that can be wrong with it
    /// here is its length.</summary>
    /// <param name="comment">The comment being written.</param>
    /// <param name="preview">A preview whose summary the rules let through.</param>
    private static void AppendSummary(StringBuilder comment, ReleasePreview preview)
    {
        string text = preview.Summary!.Text;

        comment.Append(CultureInfo.InvariantCulture, $"`{ReleaseSummary.PathFor(preview.Version)}` is on `master` ({text.Length:N0} of {SummaryRules.MaxLength:N0} characters). ");
        comment.Append("When this pull request is merged, this text goes on top of the release notes and into the Discord announcement, under its usual first line. It is shown here as code, not as it will look:\n\n");
        AppendAsCode(comment, text);

        if (preview.Problems.Count > 0)
        {
            comment.Append("**The file is too long.** As it stands the release notes get all of it, and Discord gets it cut short, with a pointer to the release notes:\n\n");
            AppendProblems(comment, preview);
        }

        comment.Append("At release time GitHub is asked whether the text mentions anyone, and a summary that does is left out. To change the text, change the file through a pull request before merging this one.\n\n");
    }

    /// <summary>Appends what the rules found wrong with a summary that will be left out, and the
    /// file as it is.</summary>
    /// <param name="comment">The comment being written.</param>
    /// <param name="preview">A preview whose summary the rules refuse.</param>
    private static void AppendHeldBack(StringBuilder comment, ReleasePreview preview)
    {
        comment.Append(CultureInfo.InvariantCulture, $"`{ReleaseSummary.PathFor(preview.Version)}` is on `master`, but **the check refuses it, so as it stands this release goes out without a summary**: the changelog lines as release notes, and a Discord announcement with the link alone. What the check found:\n\n");
        AppendProblems(comment, preview);
        comment.Append("The file as it is:\n\n");
        AppendAsCode(comment, preview.Summary!.Text);
        comment.Append("To have the summary go out, fix the file through a pull request before merging this one.\n\n");
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

    /// <summary>Appends a text inside a code fence longer than any run of backticks in the text,
    /// so that nothing in it can close the fence, be rendered, or notify anyone.</summary>
    /// <param name="comment">The comment being written.</param>
    /// <param name="text">The text to show.</param>
    private static void AppendAsCode(StringBuilder comment, string text)
    {
        string fence = new('`', Math.Max(MinFenceLength, LongestBacktickRun(text) + 1));
        comment.Append(CultureInfo.InvariantCulture, $"{fence}text\n{text}\n{fence}\n\n");
    }

    /// <summary>Appends the problems as a list, each with its line.</summary>
    /// <param name="comment">The comment being written.</param>
    /// <param name="preview">A preview with problems.</param>
    private static void AppendProblems(StringBuilder comment, ReleasePreview preview)
    {
        foreach (SummaryProblem problem in preview.Problems)
        {
            string place = problem.Line > 0
                ? string.Create(CultureInfo.InvariantCulture, $"line {problem.Line}")
                : "the file";
            comment.Append(CultureInfo.InvariantCulture, $"- {place}: {problem.Message}\n");
        }

        comment.Append('\n');
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

    /// <summary>Gives the length of the longest run of backticks in a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The number of backticks in the longest run, 0 when there is none.</returns>
    private static int LongestBacktickRun(string text)
    {
        int longest = 0;
        int current = 0;

        foreach (char character in text)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }
}
