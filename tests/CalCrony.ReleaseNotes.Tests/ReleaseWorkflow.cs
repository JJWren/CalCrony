namespace CalCrony.ReleaseNotes.Tests;

/// <summary>One script of the workflow file: a <c>run</c> key and what it runs.</summary>
/// <param name="KeyIndent">The number of spaces before the <c>run</c> key.</param>
/// <param name="Lines">The script's lines as they stand in the file, indentation included; for a
/// script written on its key's own line, that one text.</param>
/// <param name="IsBlock">Whether the script is a block under <c>run: |</c>.</param>
internal sealed record RunScript(int KeyIndent, IReadOnlyList<string> Lines, bool IsBlock);

/// <summary>The release workflow's file, read as text: a job's lines, a step's settings, and the
/// script of a step exactly as the runner would execute it.</summary>
internal sealed class ReleaseWorkflow
{
    /// <summary>Where the workflow file is, from the repository root.</summary>
    public const string RepositoryPath = ".github/workflows/release-please.yml";

    private const string JobIndent = "  ";
    private const string RunBlock = "run: |";
    private const string RunKey = "run: ";

    private readonly string[] lines;

    /// <summary>Wraps the workflow file's text.</summary>
    /// <param name="text">The file's normalized text.</param>
    private ReleaseWorkflow(string text)
    {
        lines = text.Split('\n');
    }

    /// <summary>Reads the release workflow of this checkout.</summary>
    /// <returns>The workflow.</returns>
    public static ReleaseWorkflow Load() => new(Repository.Read(RepositoryPath));

    /// <summary>Gives the lines of one job: from its name to the last line before the next job,
    /// without the comment that introduces that next job.</summary>
    /// <param name="name">The job's key under <c>jobs:</c>.</param>
    /// <returns>The job's lines.</returns>
    public IReadOnlyList<string> Job(string name)
    {
        int start = Array.IndexOf(lines, $"{JobIndent}{name}:");
        Assert.True(start >= 0, $"The workflow has no job named {name}.");
        int end = start + 1;

        while (end < lines.Length && !IsJobKey(lines[end]))
        {
            end++;
        }

        while (lines[end - 1].Trim().Length == 0 || lines[end - 1].StartsWith($"{JobIndent}#", StringComparison.Ordinal))
        {
            end--;
        }

        return lines[start..end];
    }

    /// <summary>Gives the lines nested under a key of a job, such as its <c>permissions</c>.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <param name="key">A key directly under the job, without its colon.</param>
    /// <returns>The lines under the key, trimmed, without comments or blank lines.</returns>
    public IReadOnlyList<string> Block(string jobName, string key)
    {
        List<string> job = Job(jobName).ToList();
        string opening = $"{JobIndent}{JobIndent}{key}:";
        int start = job.IndexOf(opening);
        Assert.True(start >= 0, $"The job {jobName} has no block named {key}.");

        return job
            .Skip(start + 1)
            .TakeWhile(line => line.Trim().Length == 0 || LeadingSpaces(line) > LeadingSpaces(opening))
            .Select(line => line.Trim())
            .Where(IsSetting)
            .ToList();
    }

    /// <summary>Gives the names of a job's steps in order: what follows <c>- name:</c> or
    /// <c>- uses:</c>.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <returns>Each step's first line, trimmed.</returns>
    public IReadOnlyList<string> StepLabels(string jobName) =>
        Job(jobName)
            .Where(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal) && LeadingSpaces(line) == StepIndent(jobName))
            .Select(line => line.Trim())
            .ToList();

    /// <summary>Gives a step's settings: every line of the step from its first to its last, but
    /// for the script of a <c>run: |</c> block (<see cref="StepScript"/> gives that). A setting
    /// written after such a block is a setting like any other, and is among them.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <param name="label">The step's first line, trimmed: <c>- name: …</c> or <c>- uses: …</c>.</param>
    /// <returns>The step's lines, trimmed, without comments or blank lines.</returns>
    public IReadOnlyList<string> Step(string jobName, string label)
    {
        List<string> job = Job(jobName).ToList();
        int start = job.FindIndex(line => line.Trim() == label);
        Assert.True(start >= 0, $"The job {jobName} has no step that starts with \"{label}\".");
        int indent = LeadingSpaces(job[start]);
        List<string> settings = [label];
        int blockIndent = -1;

        foreach (string line in job.Skip(start + 1))
        {
            string trimmed = line.Trim();

            if (blockIndent >= 0 && (trimmed.Length == 0 || LeadingSpaces(line) > blockIndent))
            {
                continue;
            }

            blockIndent = -1;

            if (trimmed.Length > 0 && LeadingSpaces(line) <= indent)
            {
                break;
            }

            if (IsSetting(trimmed))
            {
                settings.Add(trimmed);
            }

            if (trimmed == RunBlock)
            {
                blockIndent = LeadingSpaces(line);
            }
        }

        return settings;
    }

    /// <summary>Gives the keys of a job: what stands directly under its name, such as
    /// <c>needs</c>, <c>if</c> and <c>steps</c>.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <returns>The keys, in the order of the file, without their colons.</returns>
    public IReadOnlyList<string> JobKeys(string jobName) =>
        KeysAt(Job(jobName).Skip(1), JobIndent.Length * 2);

    /// <summary>Gives the keys the workflow file has at its outermost level.</summary>
    /// <returns>The keys, in the order of the file, without their colons.</returns>
    public IReadOnlyList<string> TopLevelKeys() => KeysAt(lines, 0);

    /// <summary>Gives the lines of the file that are structure: every line but the text of a
    /// block (under a key whose value is <c>|</c> or <c>&gt;</c>), blank lines and comments.</summary>
    /// <returns>The lines, in the order of the file, as they stand.</returns>
    public IReadOnlyList<string> StructureLines() => SplitLines().Structure;

    /// <summary>Gives the lines of the file that are not structure: the text of every block, and
    /// the comments outside the blocks.</summary>
    /// <returns>The lines, in the order of the file, as they stand, without the blank ones.</returns>
    public IReadOnlyList<string> TextLines() => SplitLines().Text;

    /// <summary>Sorts the file's lines into structure and text.</summary>
    /// <returns>The structure lines, and the lines of blocks and comments.</returns>
    private (IReadOnlyList<string> Structure, IReadOnlyList<string> Text) SplitLines()
    {
        List<string> structure = [];
        List<string> text = [];
        int blockIndent = -1;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (blockIndent >= 0 && LeadingSpaces(line) > blockIndent)
            {
                text.Add(line);
                continue;
            }

            blockIndent = -1;

            if (!IsSetting(trimmed))
            {
                text.Add(line);
                continue;
            }

            structure.Add(line);

            if (OpensBlock(trimmed))
            {
                string key = line.TrimStart(' ');
                key = key.StartsWith("- ", StringComparison.Ordinal) ? key[2..].TrimStart(' ') : key;
                blockIndent = line.Length - key.Length;
            }
        }

        return (structure, text);
    }

    /// <summary>Gives the script of a step's <c>run: |</c> block, without the block's indentation,
    /// as the runner writes it to a file before executing it.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <param name="stepName">The step's name.</param>
    /// <returns>The script, each line ended by a line feed.</returns>
    public string StepScript(string jobName, string stepName)
    {
        List<string> job = Job(jobName).ToList();
        int step = job.FindIndex(line => line.Trim() == $"- name: {stepName}");
        Assert.True(step >= 0, $"The job {jobName} has no step named {stepName}.");
        int run = job.FindIndex(step, line => line.Trim() == RunBlock);
        Assert.True(run >= 0, $"The step {stepName} has no run block.");
        int indent = LeadingSpaces(job[run]);
        List<string> script = [];

        foreach (string line in job.Skip(run + 1))
        {
            if (line.Trim().Length > 0 && LeadingSpaces(line) <= indent)
            {
                break;
            }

            script.Add(line.Trim().Length == 0 ? string.Empty : line[(indent + JobIndent.Length)..]);
        }

        return string.Join('\n', script).TrimEnd('\n') + "\n";
    }

    /// <summary>Gives a step's script with the wording of its comments taken out
    /// (<see cref="WithoutCommentWording(IEnumerable{string})"/>), for a test to pin.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <param name="stepName">The step's name.</param>
    /// <returns>The script, each line ended by a line feed, each number-sign line cut down to
    /// its indentation and the number sign.</returns>
    public string StepScriptWithoutCommentWording(string jobName, string stepName) =>
        string.Concat(
            WithoutCommentWording(StepScript(jobName, stepName).TrimEnd('\n').Split('\n'))
                .Select(line => $"{line}\n"));

    /// <summary>Cuts every number-sign line of a script down to its indentation and the number
    /// sign. A test that pins the result pins every command and the place of every comment, and
    /// leaves free only what a comment says. Nothing is decided here about what bash takes for a
    /// comment: a number-sign line added, taken out or moved changes the result wherever it
    /// stands — also inside a command that runs over several lines, where it would cut the
    /// command short. Only spaces and tabs count as indentation, as in bash. Two things about a
    /// comment's text still matter, and other tests hold them: that it has no expression in it,
    /// which GitHub fills in before bash sees the script, and no character that ends a line for
    /// one reader and not for another.</summary>
    /// <param name="script">The script's lines.</param>
    /// <returns>The lines, in order, each number-sign line without its text.</returns>
    public static IReadOnlyList<string> WithoutCommentWording(IEnumerable<string> script) =>
        script.Select(WithoutCommentWording).ToList();

    /// <summary>Gives every script in the workflow file, of every job: each block under
    /// <c>run: |</c> and each script written on its key's own line.</summary>
    /// <returns>The scripts, in the order of the file.</returns>
    public IReadOnlyList<RunScript> Scripts()
    {
        List<RunScript> scripts = [];
        int index = 0;

        while (index < lines.Length)
        {
            string line = lines[index];
            string key = line.TrimStart(' ');
            key = key.StartsWith("- ", StringComparison.Ordinal) ? key[2..].TrimStart(' ') : key;
            int keyIndent = line.Length - key.Length;
            index++;

            if (key == RunBlock)
            {
                List<string> block = [];

                while (index < lines.Length && (lines[index].Trim().Length == 0 || LeadingSpaces(lines[index]) > keyIndent))
                {
                    block.Add(lines[index]);
                    index++;
                }

                scripts.Add(new RunScript(keyIndent, block, true));
            }
            else if (key.StartsWith(RunKey, StringComparison.Ordinal) || key == RunKey.TrimEnd())
            {
                string text = key.Length > RunKey.Length ? key[RunKey.Length..] : string.Empty;
                scripts.Add(new RunScript(keyIndent, [text], false));
            }
        }

        return scripts;
    }

    /// <summary>Counts the spaces a line starts with.</summary>
    /// <param name="line">A line of the workflow file.</param>
    /// <returns>The number of leading spaces.</returns>
    public static int LeadingSpaces(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>Gives the keys written at one indentation among some lines.</summary>
    /// <param name="among">The lines to look at.</param>
    /// <param name="indent">The number of spaces before each key wanted.</param>
    /// <returns>The keys, in order, without their colons.</returns>
    private static IReadOnlyList<string> KeysAt(IEnumerable<string> among, int indent) =>
        among
            .Where(line => LeadingSpaces(line) == indent && IsSetting(line.Trim()) && line.Contains(':', StringComparison.Ordinal))
            .Select(line => line.Trim()[..line.Trim().IndexOf(':', StringComparison.Ordinal)])
            .ToList();

    /// <summary>Tells whether a line's value opens a block of text: a <c>|</c> or a <c>&gt;</c>
    /// after the key's colon, with or without the marks that may follow it.</summary>
    /// <param name="trimmed">A line without its outer spaces.</param>
    /// <returns>Whether the lines under it, indented deeper, are text and not structure.</returns>
    private static bool OpensBlock(string trimmed)
    {
        int colon = trimmed.IndexOf(": ", StringComparison.Ordinal);
        string value = colon >= 0 ? trimmed[(colon + 2)..].Trim() : string.Empty;

        return value.Length > 0
            && value[0] is '|' or '>'
            && value[1..].All(mark => mark is '-' or '+' || char.IsAsciiDigit(mark));
    }

    /// <summary>Cuts a number-sign line down to its indentation and the number sign, and leaves
    /// any other line as it is.</summary>
    /// <param name="line">A line of a script.</param>
    /// <returns>The line, without its text when it starts with a number sign.</returns>
    private static string WithoutCommentWording(string line)
    {
        int indentation = line.Length - line.TrimStart(' ', '\t').Length;
        return indentation < line.Length && line[indentation] == '#' ? line[..(indentation + 1)] : line;
    }

    /// <summary>Gives the indentation of a job's steps.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <returns>The number of spaces before each step's dash.</returns>
    private int StepIndent(string jobName)
    {
        List<string> job = Job(jobName).ToList();
        int steps = job.IndexOf($"{JobIndent}{JobIndent}steps:");
        Assert.True(steps >= 0, $"The job {jobName} has no steps.");

        return LeadingSpaces(job.Skip(steps + 1).First(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal)));
    }

    /// <summary>Tells whether a line opens a job: a key at the first level under <c>jobs:</c>.</summary>
    /// <param name="line">A line of the workflow file.</param>
    /// <returns>Whether the line is a job's key.</returns>
    private static bool IsJobKey(string line) =>
        line.Length > JobIndent.Length
        && line.StartsWith(JobIndent, StringComparison.Ordinal)
        && line[JobIndent.Length] is not (' ' or '#')
        && line.TrimEnd().EndsWith(':');

    /// <summary>Tells whether a trimmed line says something: not blank and not a comment.</summary>
    /// <param name="trimmed">A line without its outer spaces.</param>
    /// <returns>Whether the line is a setting.</returns>
    private static bool IsSetting(string trimmed) => trimmed.Length > 0 && !trimmed.StartsWith('#');
}
