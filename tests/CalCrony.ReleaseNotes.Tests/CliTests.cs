namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The tool's commands, run against throwaway checkouts: what they write, what they hand
/// the release workflow, and their exit codes. No command sends anything anywhere.</summary>
public class CliTests
{
    private const string Summary = "### What's new\n\n- One change.";
    private const string Notes = "## [0.38.0](https://example.test/compare) (2026-12-01)\n\n\n### Features\n\n* one change (#1)";

    [Fact]
    public void No_command_is_a_usage_error()
    {
        CommandResult result = Run();

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("Usage:", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("check", "--version")]
    [InlineData("check", "--version", "0.38.0")]
    [InlineData("check", "stray", "value")]
    [InlineData("preview")]
    [InlineData("preview", "--version", "v0.38.0")]
    [InlineData("preview", "--version", "0.38.0", "--version", "0.38.1")]
    [InlineData("preview", "--version", "0.38.0", "--notes", "no-such-file.md")]
    [InlineData("render", "--version", "0.38.0")]
    [InlineData("comment", "--title", "chore(master): release 0.38.0")]
    public void A_wrong_command_line_is_a_usage_error(params string[] arguments)
    {
        CommandResult result = Run(arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.NotEqual(string.Empty, result.Error);
    }

    [Fact]
    public void Check_passes_a_folder_of_good_summaries()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.37.0", Summary);
        checkout.WriteSummary("0.38.0", Summary);
        checkout.Write("docs/release-notes/README.md", "# Release notes\n\n| a table is fine here |\n");

        CommandResult result = Run("check", "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Release summaries checked: 2. No problems.", result.Output.Trim());
    }

    [Fact]
    public void Check_passes_a_checkout_with_no_folder_at_all()
    {
        using TemporaryCheckout checkout = new();

        Assert.Equal(0, Run("check", "--root", checkout.Root).ExitCode);
    }

    [Fact]
    public void Check_fails_on_a_summary_that_breaks_a_rule_and_says_where()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "### What's new\n\n- One change.\n\nThanks @everyone.\n");

        CommandResult result = Run("check", "--root", checkout.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("docs/release-notes/0.38.0.md:5: `@` followed by a name", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.38.0.md")]
    [InlineData("0.38.md")]
    [InlineData("next.md")]
    [InlineData("0.38.0.txt")]
    [InlineData("readme.md")]
    public void Check_fails_on_a_file_no_release_would_ever_read(string fileName)
    {
        using TemporaryCheckout checkout = new();
        checkout.Write($"docs/release-notes/{fileName}", Summary);

        CommandResult result = Run("check", "--root", checkout.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith($"docs/release-notes/{fileName}: not a release summary.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_without_a_summary_writes_no_notes_and_says_so_in_the_outputs()
    {
        using TemporaryCheckout checkout = new();
        string notes = checkout.Write("notes.md", Notes);
        string outputs = checkout.PathOf("outputs.txt");
        string rendered = checkout.PathOf("release-notes.md");

        CommandResult result = Run(
            "render", "--version", "0.38.0", "--notes", notes, "--out", rendered, "--github-output", outputs, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(rendered));
        Assert.Equal("has_summary=false\ndiscord_summary=\n", File.ReadAllText(outputs));
        Assert.Contains("No release summary for 0.38.0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_treats_an_empty_summary_file_as_no_summary()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "\n");
        string notes = checkout.Write("notes.md", Notes);
        string outputs = checkout.PathOf("outputs.txt");

        CommandResult result = Run(
            "render", "--version", "0.38.0", "--notes", notes, "--out", checkout.PathOf("release-notes.md"), "--github-output", outputs, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("has_summary=false\ndiscord_summary=\n", File.ReadAllText(outputs));
    }

    [Fact]
    public void Render_with_a_summary_writes_the_notes_and_hands_the_text_on()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "\uFEFF" + Summary.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n");
        string notes = checkout.Write("notes.md", Notes + "\n");
        string outputs = checkout.PathOf("outputs.txt");
        string rendered = checkout.PathOf("release-notes.md");

        CommandResult result = Run(
            "render", "--version", "0.38.0", "--notes", notes, "--out", rendered, "--github-output", outputs, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{Summary}\n\n---\n\n{Notes}\n", File.ReadAllText(rendered));

        string[] outputLines = File.ReadAllText(outputs).Split('\n');
        Assert.Equal("has_summary=true", outputLines[0]);
        Assert.StartsWith("discord_summary<<ghadelimiter_", outputLines[1], StringComparison.Ordinal);
        Assert.Equal(Summary, string.Join('\n', outputLines[2..^2]));
        Assert.DoesNotContain("::warning", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_reads_the_summary_named_for_the_version_and_no_other()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.37.1", "### What's new\n\n- A change for another release.");
        string notes = checkout.Write("notes.md", Notes);
        string outputs = checkout.PathOf("outputs.txt");

        Run("render", "--version", "0.38.0", "--notes", notes, "--out", checkout.PathOf("release-notes.md"), "--github-output", outputs, "--root", checkout.Root);

        Assert.Equal("has_summary=false\ndiscord_summary=\n", File.ReadAllText(outputs));
    }

    [Fact]
    public void Render_warns_about_a_summary_that_breaks_a_rule_and_carries_on()
    {
        using TemporaryCheckout checkout = new();
        string overLong = "### What's new\n\n- One change.\n\nThanks @everyone.\n\n" + new string('a', 1800);
        checkout.WriteSummary("0.38.0", overLong);
        string notes = checkout.Write("notes.md", Notes);
        string outputs = checkout.PathOf("outputs.txt");
        string rendered = checkout.PathOf("release-notes.md");

        CommandResult result = Run(
            "render", "--version", "0.38.0", "--notes", notes, "--out", rendered, "--github-output", outputs, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("::warning file=docs/release-notes/0.38.0.md::The summary is 1,850 characters", result.Output, StringComparison.Ordinal);
        Assert.Contains("::warning file=docs/release-notes/0.38.0.md,line=5::`@` followed by a name", result.Output, StringComparison.Ordinal);

        // GitHub's notes take the whole summary; Discord's text is cut to fit.
        Assert.StartsWith(overLong, File.ReadAllText(rendered), StringComparison.Ordinal);
        string forDiscord = string.Join('\n', File.ReadAllText(outputs).Split('\n')[2..^2]);
        Assert.True(forDiscord.Length <= SummaryRules.MaxLength);
        Assert.EndsWith(ReleaseTexts.Pointer, forDiscord, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_run_again_over_its_own_result_adds_nothing_twice()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", Summary);
        string notes = checkout.Write("notes.md", Notes);
        string first = checkout.PathOf("first.md");
        string second = checkout.PathOf("second.md");

        Run("render", "--version", "0.38.0", "--notes", notes, "--out", first, "--root", checkout.Root);
        Run("render", "--version", "0.38.0", "--notes", first, "--out", second, "--root", checkout.Root);

        Assert.Equal($"{Summary}\n\n---\n\n{Notes}\n", File.ReadAllText(second));
        Assert.Equal(File.ReadAllText(first), File.ReadAllText(second));
    }

    [Fact]
    public void Render_without_an_outputs_file_still_writes_the_notes()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", Summary);
        string notes = checkout.Write("notes.md", Notes);
        string rendered = checkout.PathOf("release-notes.md");

        CommandResult result = Run("render", "--version", "0.38.0", "--notes", notes, "--out", rendered, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(rendered));
    }

    [Fact]
    public void Preview_shows_the_discord_message_and_the_release_notes()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", Summary);
        string notes = checkout.Write("notes.md", Notes);

        CommandResult result = Run("preview", "--version", "0.38.0", "--notes", notes, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Release summary for 0.38.0: docs/release-notes/0.38.0.md, 29 of 1,800 characters.", result.Output, StringComparison.Ordinal);
        Assert.Contains("== Discord announcement (161 of 2,000 characters) ==", result.Output, StringComparison.Ordinal);
        Assert.Contains(
            $"<@&1543943300132769802> **CalCrony v0.38.0** just shipped — release notes: https://github.com/JJWren/CalCrony/releases/tag/v0.38.0\n\n{Summary}\n",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains($"== GitHub release notes ==\n{Summary}\n\n---\n\n{Notes}\n", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_without_a_summary_shows_the_first_line_alone()
    {
        using TemporaryCheckout checkout = new();

        CommandResult result = Run("preview", "--version", "0.38.0", "--repository", "someone/fork", "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("No release summary for 0.38.0", result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "== Discord announcement (127 of 2,000 characters) ==\n<@&1543943300132769802> **CalCrony v0.38.0** just shipped — release notes: https://github.com/someone/fork/releases/tag/v0.38.0\n\n==",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Comment_writes_the_release_pull_requests_comment()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", Summary);
        string comment = checkout.PathOf("comment.md");

        CommandResult result = Run("comment", "--title", "chore(master): release 0.38.0", "--out", comment, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith($"{PreviewComment.Marker}\n### Release summary for 0.38.0\n", File.ReadAllText(comment), StringComparison.Ordinal);
        Assert.Contains("summary found", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Comment_refuses_a_title_that_names_no_version()
    {
        using TemporaryCheckout checkout = new();
        string comment = checkout.PathOf("comment.md");

        CommandResult result = Run("comment", "--title", "feat: something else", "--out", comment, "--root", checkout.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(comment));
        Assert.Contains("is not a release pull request's title", result.Error, StringComparison.Ordinal);
    }

    /// <summary>Runs the tool in this process.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>The exit code and what was written, with line feeds as line ends.</returns>
    private static CommandResult Run(params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = Cli.Run(arguments, output, error);

        return new CommandResult(
            exitCode,
            output.ToString().ReplaceLineEndings("\n"),
            error.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>What a command left behind.</summary>
    /// <param name="ExitCode">The exit code.</param>
    /// <param name="Output">What it wrote to its output.</param>
    /// <param name="Error">What it wrote to its error stream.</param>
    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
