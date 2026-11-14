namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The release workflow's file, read as text: a job's lines, and the script of one of
/// its steps exactly as the runner would execute it.</summary>
internal sealed class ReleaseWorkflow
{
    private const string RepositoryPath = ".github/workflows/release-please.yml";
    private const string JobIndent = "  ";

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
        IReadOnlyList<string> job = Job(jobName);
        string opening = $"{JobIndent}{JobIndent}{key}:";
        int start = job.ToList().IndexOf(opening);
        Assert.True(start >= 0, $"The job {jobName} has no block named {key}.");

        return job
            .Skip(start + 1)
            .TakeWhile(line => line.Trim().Length == 0 || LeadingSpaces(line) > opening.Length - key.Length - 1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
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
        int run = job.FindIndex(step, line => line.Trim() == "run: |");
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

    /// <summary>Tells whether a line opens a job: a key at the first level under <c>jobs:</c>.</summary>
    /// <param name="line">A line of the workflow file.</param>
    /// <returns>Whether the line is a job's key.</returns>
    private static bool IsJobKey(string line) =>
        line.Length > JobIndent.Length
        && line.StartsWith(JobIndent, StringComparison.Ordinal)
        && line[JobIndent.Length] is not (' ' or '#')
        && line.TrimEnd().EndsWith(':');

    /// <summary>Counts the spaces a line starts with.</summary>
    /// <param name="line">A line of the workflow file.</param>
    /// <returns>The number of leading spaces.</returns>
    private static int LeadingSpaces(string line) => line.Length - line.TrimStart(' ').Length;
}
