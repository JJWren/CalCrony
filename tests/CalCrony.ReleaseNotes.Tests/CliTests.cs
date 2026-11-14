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
    [InlineData("check", "--root", "no-such-folder")]
    [InlineData("preview", "--version", "0.38.0", "--root", "no-such-folder")]
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
    public void Check_run_where_there_is_no_folder_of_summaries_says_so_instead_of_passing()
    {
        using TemporaryCheckout checkout = new();

        CommandResult result = Run("check", "--root", checkout.Root);

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("docs/release-notes was not found under ", result.Error, StringComparison.Ordinal);
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
    public void Render_sends_out_a_summary_that_is_only_too_long_with_a_warning_and_cuts_it_for_discord()
    {
        using TemporaryCheckout checkout = new();
        string overLong = "### What's new\n\n- One change.\n- " + new string('a', 1800);
        checkout.WriteSummary("0.38.0", overLong);
        string notes = checkout.Write("notes.md", Notes);
        string outputs = checkout.PathOf("outputs.txt");
        string rendered = checkout.PathOf("release-notes.md");

        CommandResult result = Run(
            "render", "--version", "0.38.0", "--notes", notes, "--out", rendered, "--github-output", outputs, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("::warning file=docs/release-notes/0.38.0.md::The summary is 1,832 characters", result.Output, StringComparison.Ordinal);

        // GitHub's notes take the whole summary; Discord's text is cut to fit.
        Assert.StartsWith(overLong, File.ReadAllText(rendered), StringComparison.Ordinal);
        string[] outputLines = File.ReadAllText(outputs).Split('\n');
        Assert.Equal("has_summary=true", outputLines[0]);
        Assert.Equal($"### What's new\n\n- One change.\n\n{ReleaseTexts.Pointer}", string.Join('\n', outputLines[2..^2]));
    }

    [Theory]
    [InlineData("Thanks @everyone.", "line=5::`@` followed by a name")]
    [InlineData("| a | table |", "line=5::Tables do not render in Discord")]
    [InlineData("```", "line=5::Code blocks are not allowed")]
    public void Render_leaves_out_a_summary_the_check_refuses_and_the_release_goes_out_as_before(string line, string warning)
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", $"### What's new\n\n- One change.\n\n{line}");
        string notes = checkout.Write("notes.md", Notes);
        string outputs = checkout.PathOf("outputs.txt");
        string rendered = checkout.PathOf("release-notes.md");

        CommandResult result = Run(
            "render", "--version", "0.38.0", "--notes", notes, "--out", rendered, "--github-output", outputs, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"::warning file=docs/release-notes/0.38.0.md,{warning}", result.Output, StringComparison.Ordinal);
        Assert.Contains("The check refuses docs/release-notes/0.38.0.md, so it is left out", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(rendered));
        Assert.Equal("has_summary=false\ndiscord_summary=\n", File.ReadAllText(outputs));
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

    [Theory]
    [InlineData("feat: something else")]
    [InlineData("chore(master): release 1.0.0-rc.1")]
    public void Comment_writes_nothing_for_a_title_that_names_no_plain_version_and_still_succeeds(string title)
    {
        using TemporaryCheckout checkout = new();
        string comment = checkout.PathOf("comment.md");

        CommandResult result = Run("comment", "--title", title, "--out", comment, "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(comment));
        Assert.Contains("No comment written.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_says_when_the_check_refuses_the_summary_and_shows_the_release_without_it()
    {
        using TemporaryCheckout checkout = new();
        checkout.WriteSummary("0.38.0", "### What's new\n\n- One change.\n\nThanks @everyone.");

        CommandResult result = Run("preview", "--version", "0.38.0", "--root", checkout.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("  docs/release-notes/0.38.0.md:5: `@` followed by a name", result.Output, StringComparison.Ordinal);
        Assert.Contains("The check refuses it, so the release would go out without it", result.Output, StringComparison.Ordinal);
        Assert.Contains("== Discord announcement (130 of 2,000 characters) ==", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("@everyone", result.Output, StringComparison.Ordinal);
    }

    [SymbolicLinkFact]
    public void A_link_named_for_a_version_is_not_read_as_a_summary()
    {
        using TemporaryCheckout checkout = new();
        string elsewhere = checkout.Write("elsewhere/not-ours.txt", "### What's new\n\n- Not the repository's to publish.");
        string notes = checkout.Write("notes.md", Notes);
        string outputs = checkout.PathOf("outputs.txt");
        Directory.CreateDirectory(checkout.PathOf("docs/release-notes"));
        File.CreateSymbolicLink(checkout.PathOf("docs/release-notes/0.38.0.md"), elsewhere);

        CommandResult rendered = Run(
            "render", "--version", "0.38.0", "--notes", notes, "--out", checkout.PathOf("release-notes.md"), "--github-output", outputs, "--root", checkout.Root);
        CommandResult check = Run("check", "--root", checkout.Root);

        Assert.Equal(0, rendered.ExitCode);
        Assert.Equal("has_summary=false\ndiscord_summary=\n", File.ReadAllText(outputs));
        Assert.False(File.Exists(checkout.PathOf("release-notes.md")));
        Assert.Equal(1, check.ExitCode);
        Assert.StartsWith("docs/release-notes/0.38.0.md: not a release summary. A summary is a plain file", check.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_named_for_a_version_is_not_read_as_a_summary()
    {
        using TemporaryCheckout checkout = new();
        checkout.Write("docs/release-notes/0.38.0.md/inside.md", Summary);

        CommandResult check = Run("check", "--root", checkout.Root);

        Assert.Equal(1, check.ExitCode);
        Assert.StartsWith("docs/release-notes/0.38.0.md: not a release summary.", check.Output, StringComparison.Ordinal);
        Assert.Null(ReleaseSummary.Find(checkout.Root, new ReleaseVersion(0, 38, 0)));
        Assert.Empty(SummaryFolder.Versions(checkout.Root));
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
