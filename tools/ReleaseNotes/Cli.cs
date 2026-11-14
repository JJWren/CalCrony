using System.Globalization;

namespace CalCrony.ReleaseNotes;

/// <summary>The tool's commands: <c>check</c> for every pull request, <c>preview</c> for a person
/// who wants to see what a release would say, and <c>render</c> and <c>comment</c> for the release
/// workflow. None of them sends anything anywhere; they read a checkout and write text.</summary>
public static class Cli
{
    /// <summary>The repository a preview links to when none is named.</summary>
    public const string DefaultRepository = "JJWren/CalCrony";

    /// <summary>The name of the output that says whether the release has a summary.</summary>
    public const string HasSummaryOutput = "has_summary";

    /// <summary>The name of the output that carries the summary for Discord.</summary>
    public const string DiscordSummaryOutput = "discord_summary";

    private const int Success = 0;
    private const int Failure = 1;
    private const int UsageError = 2;

    private const string NotesPlaceholder = "(the notes release-please writes for this version)";

    private const string Usage = """
        Usage: dotnet run --project tools/ReleaseNotes -- <command> [options]

          check
              Check every summary in docs/release-notes against the rules.
          preview --version <version> [--notes <file>] [--repository <owner/name>]
              Show what a release would post to Discord and put on GitHub. Sends nothing.
          render --version <version> --notes <file> --out <file> [--summary-out <file>] [--github-output <file>]
              Release time: write the release notes with the summary on top.
          comment --title <title> --out <file>
              Write the comment for the open release pull request.

        Every command takes --root <folder> for a checkout other than the current folder.
        """;

    /// <summary>Runs one command.</summary>
    /// <param name="arguments">The command's name and its options.</param>
    /// <param name="output">Where the command's report goes.</param>
    /// <param name="error">Where a complaint about the command line goes.</param>
    /// <returns>0 when the command did its work, 1 when it found problems, 2 when the command
    /// line was wrong.</returns>
    public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Count == 0)
        {
            error.WriteLine(Usage);
            return UsageError;
        }

        IReadOnlyList<string> options = arguments.Skip(1).ToList();

        switch (arguments[0])
        {
            case "check":
                return Check(options, output, error);
            case "preview":
                return Preview(options, output, error);
            case "render":
                return Render(options, output, error);
            case "comment":
                return Comment(options, output, error);
            default:
                error.WriteLine($"Unknown command \"{arguments[0]}\".");
                error.WriteLine(Usage);
                return UsageError;
        }
    }

    /// <summary>Checks the folder of summaries: every file named for a version against the rules,
    /// and that nothing else is in the folder but its README.</summary>
    /// <param name="arguments">The command's options.</param>
    /// <param name="output">Where the problems are listed, one per line.</param>
    /// <param name="error">Where a complaint about the command line goes.</param>
    /// <returns>The command's exit code.</returns>
    private static int Check(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (!TryReadOptions(arguments, [], [], error, out CliOptions options))
        {
            return UsageError;
        }

        if (SummaryFolder.IsReachedThroughLink(options.Root))
        {
            output.WriteLine(
                $"{ReleaseSummary.FolderPath} is reached through a link. Summaries are plain files in a plain folder; nothing is read through a link.");
            return Failure;
        }

        if (!Directory.Exists(SummaryFolder.PathIn(options.Root)))
        {
            error.WriteLine(
                $"{ReleaseSummary.FolderPath} was not found under {options.Root}. Run the check from the repository root, or name it with --root.");
            return UsageError;
        }

        int problemCount = 0;

        foreach (string name in SummaryFolder.UnexpectedNames(options.Root))
        {
            output.WriteLine(
                $"{ReleaseSummary.FolderPath}/{name}: not a release summary. A summary is a plain file named for its version, like 0.38.0.md — not a folder and not a link — and {SummaryFolder.ReadMeFileName} is the only other file here.");
            problemCount++;
        }

        IReadOnlyList<ReleaseVersion> versions = SummaryFolder.Versions(options.Root);

        foreach (ReleaseVersion version in versions)
        {
            ReleaseSummary? summary = ReleaseSummary.Find(options.Root, version);

            if (summary is null)
            {
                continue;
            }

            foreach (SummaryProblem problem in SummaryRules.Check(summary.Text))
            {
                output.WriteLine(Locate(ReleaseSummary.PathFor(version), problem));
                problemCount++;
            }
        }

        if (problemCount > 0)
        {
            return Failure;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Release summaries checked: {versions.Count}. No problems."));
        return Success;
    }

    /// <summary>Shows what a release of a version would say, in Discord and on GitHub, from the
    /// checkout as it is.</summary>
    /// <param name="arguments">The command's options.</param>
    /// <param name="output">Where the two texts go.</param>
    /// <param name="error">Where a complaint about the command line goes.</param>
    /// <returns>The command's exit code.</returns>
    private static int Preview(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (!TryReadOptions(arguments, ["version"], ["notes", "repository"], error, out CliOptions options)
            || !TryReadVersion(options, error, out ReleaseVersion version)
            || !TryReadNotes(options, error, out string? notes))
        {
            return UsageError;
        }

        string path = ReleaseSummary.PathFor(version);
        string written = ReleaseSummary.Find(options.Root, version)?.Text ?? string.Empty;
        IReadOnlyList<SummaryProblem> problems = written.Length == 0 ? [] : SummaryRules.Check(written);
        bool isHeldBack = SummaryRules.HoldSummaryBack(problems);
        string summaryText = isHeldBack ? string.Empty : written;
        string tag = Announcement.TagFor(version);
        string releaseUrl = Announcement.ReleaseUrl(options.Value("repository") ?? DefaultRepository, tag);
        string discordSummary = ReleaseTexts.DiscordSummary(summaryText);
        string content = Announcement.Content(tag, releaseUrl, discordSummary);
        string existingNotes = notes ?? NotesPlaceholder;

        if (written.Length == 0)
        {
            output.WriteLine(
                $"No release summary for {version}: {path} is missing or empty. The release would go out as it always has.");
        }
        else
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Release summary for {version}: {path}, {written.Length:N0} of {SummaryRules.MaxLength:N0} characters."));

            foreach (SummaryProblem problem in problems)
            {
                output.WriteLine($"  {Locate(path, problem)}");
            }

            if (isHeldBack)
            {
                output.WriteLine("The check refuses it, so the release would go out without it, as it always has.");
            }
        }

        output.WriteLine();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"== Discord announcement ({Announcement.CountCodePoints(content):N0} of {Announcement.MaxContentLength:N0} characters) =="));
        output.WriteLine(content);
        output.WriteLine();
        output.WriteLine("== Body posted to the webhook ==");
        output.WriteLine(Announcement.Payload(tag, releaseUrl, discordSummary));
        output.WriteLine();
        output.WriteLine("== GitHub release notes ==");
        output.WriteLine(summaryText.Length == 0 ? existingNotes : ReleaseTexts.ReleaseNotes(summaryText, existingNotes));
        return Success;
    }

    /// <summary>Release time. Writes the release notes with the version's summary on top, and
    /// hands the summary for Discord on as a job output. Without a summary it writes no file and
    /// says so in the outputs. What the rules find wrong with a summary is reported as warnings:
    /// one that is only too long still goes out, cut short for Discord, and one the rules refuse
    /// for any other reason is left out, so the release goes out as it would without it. The
    /// command succeeds either way. With <c>--summary-out</c> it also writes the summary by
    /// itself, for the workflow to show to GitHub's own renderer before anything is published.</summary>
    /// <param name="arguments">The command's options.</param>
    /// <param name="output">Where the command's report and its warnings go.</param>
    /// <param name="error">Where a complaint about the command line goes.</param>
    /// <returns>The command's exit code.</returns>
    private static int Render(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (!TryReadOptions(arguments, ["version", "notes", "out"], ["github-output", "summary-out"], error, out CliOptions options)
            || !TryReadVersion(options, error, out ReleaseVersion version)
            || !TryReadNotes(options, error, out string? notes))
        {
            return UsageError;
        }

        string path = ReleaseSummary.PathFor(version);
        string? outputsPath = options.Value("github-output");
        string? summaryPath = options.Value("summary-out");
        string summaryText = ReleaseSummary.Find(options.Root, version)?.Text ?? string.Empty;

        if (summaryText.Length == 0)
        {
            output.WriteLine(
                $"No release summary for {version}: {path} is missing or empty. The release notes and the announcement stay as they are.");
            WriteOutputs(outputsPath, false, string.Empty);
            return Success;
        }

        IReadOnlyList<SummaryProblem> problems = SummaryRules.Check(summaryText);

        foreach (SummaryProblem problem in problems)
        {
            output.WriteLine(WorkflowCommand.Warning(path, problem.Line, problem.Message));
        }

        if (SummaryRules.HoldSummaryBack(problems))
        {
            output.WriteLine(
                $"The check refuses {path}, so it is left out: the release notes and the announcement stay as they are.");
            WriteOutputs(outputsPath, false, string.Empty);
            return Success;
        }

        string discordSummary = ReleaseTexts.DiscordSummary(summaryText);
        string outPath = options.Required("out");
        File.WriteAllText(outPath, ReleaseTexts.ReleaseNotes(summaryText, notes ?? string.Empty) + "\n");

        if (summaryPath is not null)
        {
            File.WriteAllText(summaryPath, summaryText + "\n");
        }

        WriteOutputs(outputsPath, true, discordSummary);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Release summary for {version}: {path}, {summaryText.Length:N0} characters; {discordSummary.Length:N0} go to Discord."));
        output.WriteLine($"Release notes with the summary on top: {outPath}");
        return Success;
    }

    /// <summary>Writes the comment for the open release pull request, whose title names the
    /// version. A title that names no plain version — a prerelease, say — gets no comment: the
    /// command writes no file and still succeeds, so the job that runs it on every push does not
    /// fail for as long as such a pull request is open.</summary>
    /// <param name="arguments">The command's options.</param>
    /// <param name="output">Where the command's report goes.</param>
    /// <param name="error">Where a complaint about the command line goes.</param>
    /// <returns>The command's exit code.</returns>
    private static int Comment(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (!TryReadOptions(arguments, ["title", "out"], [], error, out CliOptions options))
        {
            return UsageError;
        }

        string title = options.Required("title");

        if (!ReleasePullRequest.TryReadVersion(title, out ReleaseVersion version))
        {
            output.WriteLine($"\"{title}\" does not end in \"release\" and a version of three numbers, so there is no summary to look for. No comment written.");
            return Success;
        }

        ReleasePreview preview = ReleasePreview.Gather(options.Root, version);
        string outPath = options.Required("out");
        File.WriteAllText(outPath, PreviewComment.Build(preview) + "\n");

        output.WriteLine(preview.HasSummary
            ? $"{version}: summary found at {ReleaseSummary.PathFor(version)}. Comment written to {outPath}"
            : $"{version}: no summary written. Comment written to {outPath}");
        return Success;
    }

    /// <summary>Reads a command's options, and says what is wrong when they are not right.</summary>
    /// <param name="arguments">The arguments after the command's name.</param>
    /// <param name="required">The options the command cannot do without.</param>
    /// <param name="optional">The options the command also takes.</param>
    /// <param name="error">Where the complaint goes.</param>
    /// <param name="options">The options read.</param>
    /// <returns>Whether the command can go on.</returns>
    private static bool TryReadOptions(
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional,
        TextWriter error,
        out CliOptions options)
    {
        if (!CliOptions.TryParse(arguments, required, optional, out options, out string problem))
        {
            error.WriteLine(problem);
            error.WriteLine(Usage);
            return false;
        }

        if (!Directory.Exists(options.Root))
        {
            error.WriteLine($"The folder {options.Root} does not exist.");
            return false;
        }

        return true;
    }

    /// <summary>Reads the <c>--version</c> option.</summary>
    /// <param name="options">The command's options, <c>version</c> among them.</param>
    /// <param name="error">Where the complaint goes when it is not a version.</param>
    /// <param name="version">The version read.</param>
    /// <returns>Whether the option is a version.</returns>
    private static bool TryReadVersion(CliOptions options, TextWriter error, out ReleaseVersion version)
    {
        string text = options.Required("version");

        if (ReleaseVersion.TryParse(text, out version))
        {
            return true;
        }

        error.WriteLine($"\"{text}\" is not a version. Write it as release-please does: 0.38.0.");
        return false;
    }

    /// <summary>Reads the file the <c>--notes</c> option names, when the option is given.</summary>
    /// <param name="options">The command's options.</param>
    /// <param name="error">Where the complaint goes when the file does not exist.</param>
    /// <param name="notes">The file's text, or null when the option was not given.</param>
    /// <returns>Whether the option is absent or names a file that exists.</returns>
    private static bool TryReadNotes(CliOptions options, TextWriter error, out string? notes)
    {
        string? path = options.Value("notes");
        notes = null;

        if (path is null)
        {
            return true;
        }

        if (!File.Exists(path))
        {
            error.WriteLine($"The notes file {path} does not exist.");
            return false;
        }

        notes = File.ReadAllText(path);
        return true;
    }

    /// <summary>Writes the two outputs the release workflow reads, when it asked for them.</summary>
    /// <param name="outputsPath">The file named by <c>GITHUB_OUTPUT</c>, or null outside a workflow.</param>
    /// <param name="hasSummary">Whether the release has a summary.</param>
    /// <param name="discordSummary">The summary for Discord, empty without one.</param>
    private static void WriteOutputs(string? outputsPath, bool hasSummary, string discordSummary)
    {
        if (outputsPath is null)
        {
            return;
        }

        GitHubOutput.Append(outputsPath, HasSummaryOutput, hasSummary ? "true" : "false");
        GitHubOutput.Append(outputsPath, DiscordSummaryOutput, discordSummary);
    }

    /// <summary>Puts a problem where an editor can jump to it: path, line, message.</summary>
    /// <param name="path">The summary's repository path.</param>
    /// <param name="problem">The problem.</param>
    /// <returns>One line of text.</returns>
    private static string Locate(string path, SummaryProblem problem) =>
        problem.Line > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{path}:{problem.Line}: {problem.Message}")
            : $"{path}: {problem.Message}";
}
