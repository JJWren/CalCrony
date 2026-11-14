namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The release workflow's file, read as text: a job's lines, a step's settings, and the
/// script of a step exactly as the runner would execute it.</summary>
internal sealed class ReleaseWorkflow
{
    private const string RepositoryPath = ".github/workflows/release-please.yml";
    private const string JobIndent = "  ";
    private const string RunBlock = "run: |";

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

    /// <summary>Gives a step's settings: its lines from the first to its <c>run</c> line, or to
    /// its end when it runs an action. The script of a <c>run: |</c> block is not among them
    /// (<see cref="StepScript"/> gives that).</summary>
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

        foreach (string line in job.Skip(start + 1))
        {
            if (line.Trim().Length > 0 && LeadingSpaces(line) <= indent)
            {
                break;
            }

            if (IsSetting(line.Trim()))
            {
                settings.Add(line.Trim());
            }

            if (line.Trim() == RunBlock)
            {
                break;
            }
        }

        return settings;
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

    /// <summary>Gives the commands of a step's <c>run: |</c> block: its script without the lines
    /// that are comments, so that a test which pins the commands does not also pin the wording
    /// of a comment.</summary>
    /// <param name="jobName">The job's key.</param>
    /// <param name="stepName">The step's name.</param>
    /// <returns>The script's lines other than comments, each ended by a line feed.</returns>
    public string StepCommands(string jobName, string stepName) =>
        string.Concat(
            Commands(StepScript(jobName, stepName).TrimEnd('\n').Split('\n'))
                .Select(line => $"{line}\n"));

    /// <summary>Gives the lines of a script that are not comments. A line that starts with a
    /// number sign is a comment only where a command could start. After a line that ends in a
    /// backslash it is not one: bash joins it to the command above, where it cuts off the rest.
    /// Inside quoted text that runs over several lines, or inside a here-document, it is part of
    /// that text. All of those stay, so that such a line is seen by whatever pins the commands.
    /// Quotes are counted plainly, line by line, which can only err one way: a lone quote inside
    /// the other kind keeps the comments after it in.</summary>
    /// <param name="script">The script's lines.</param>
    /// <returns>The lines that are commands or parts of commands, in order.</returns>
    public static IReadOnlyList<string> Commands(IEnumerable<string> script)
    {
        List<string> commands = [];
        bool isContinued = false;
        bool isInSingleQuotes = false;
        bool isInDoubleQuotes = false;
        bool hasHereDocument = false;

        foreach (string line in script)
        {
            bool couldStartCommand = !isContinued && !isInSingleQuotes && !isInDoubleQuotes && !hasHereDocument;

            if (couldStartCommand && line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            commands.Add(line);
            isContinued = line.EndsWith('\\');
            isInSingleQuotes ^= line.Count(character => character == '\'') % 2 == 1;
            isInDoubleQuotes ^= CountUnescaped(line, '"') % 2 == 1;
            hasHereDocument |= line.Contains("<<", StringComparison.Ordinal);
        }

        return commands;
    }

    /// <summary>Counts a character in a line, leaving out each one that follows a backslash.</summary>
    /// <param name="line">A line of a script.</param>
    /// <param name="wanted">The character to count.</param>
    /// <returns>How many times the character stands there unescaped.</returns>
    private static int CountUnescaped(string line, char wanted)
    {
        int count = 0;

        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == wanted && (index == 0 || line[index - 1] != '\\'))
            {
                count++;
            }
        }

        return count;
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

    /// <summary>Counts the spaces a line starts with.</summary>
    /// <param name="line">A line of the workflow file.</param>
    /// <returns>The number of leading spaces.</returns>
    private static int LeadingSpaces(string line) => line.Length - line.TrimStart(' ').Length;
}
