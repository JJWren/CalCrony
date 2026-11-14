using System.Diagnostics;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>What a run of a step's script left behind.</summary>
/// <param name="ExitCode">The script's exit code.</param>
/// <param name="Output">What it wrote to its output.</param>
/// <param name="Error">What it wrote to its error stream.</param>
internal sealed record StepResult(int ExitCode, string Output, string Error);

/// <summary>The shell a workflow step runs in, which decides the flags bash gets.</summary>
internal enum StepShell
{
    /// <summary>A step that names no shell: the runner starts <c>bash -e</c>, so a pipeline's
    /// result is its last command's.</summary>
    Unnamed,

    /// <summary>A step that says <c>shell: bash</c>: the runner starts
    /// <c>bash --noprofile --norc -eo pipefail</c>, so a pipeline fails when any command in it
    /// does.</summary>
    Bash,
}

/// <summary>Runs a workflow step's script the way the runner does: bash with the flags the step's
/// shell setting gives it, and the step's <c>env</c> as environment variables.</summary>
internal static class StepRunner
{
    /// <summary>Why the tests that run the announce step are skipped on a machine that cannot
    /// run it.</summary>
    public const string WhyNot = "Needs bash, jq and curl on the PATH of a Linux or macOS machine; the CI runner has them.";

    private static readonly string[] Tools = ["bash", "jq", "curl"];
    private static readonly string[] StepVariables = ["WEBHOOK_URL", "RELEASE_TAG", "RELEASE_URL", "SUMMARY"];
    private static readonly string[] UnnamedShellFlags = ["-e"];
    private static readonly string[] BashShellFlags = ["--noprofile", "--norc", "-eo", "pipefail"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Whether this machine can run the announce step's script.</summary>
    public static bool CanRun { get; } = !OperatingSystem.IsWindows() && Tools.All(IsOnPath);

    /// <summary>Runs a script as the runner runs a <c>run</c> step.</summary>
    /// <param name="script">The script's text.</param>
    /// <param name="shell">The step's shell setting.</param>
    /// <param name="environment">The step's variables; any of them left out is not set at all.
    /// <c>PATH</c> may be among them, to put a stand-in program in front of the real one.</param>
    /// <returns>The script's exit code and what it wrote.</returns>
    public static async Task<StepResult> RunAsync(
        string script,
        StepShell shell,
        IReadOnlyDictionary<string, string> environment)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcrony-announce-step-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(path, script);

        try
        {
            ProcessStartInfo start = new("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string flag in shell == StepShell.Bash ? BashShellFlags : UnnamedShellFlags)
            {
                start.ArgumentList.Add(flag);
            }

            start.ArgumentList.Add(path);

            foreach (string name in StepVariables)
            {
                start.Environment.Remove(name);
            }

            foreach (KeyValuePair<string, string> variable in environment)
            {
                start.Environment[variable.Key] = variable.Value;
            }

            // The stand-in webhook is on this machine; no proxy should be asked for it.
            start.Environment["no_proxy"] = "127.0.0.1";

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("bash did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                throw new TimeoutException($"The step's script was still running after {Timeout.TotalSeconds} seconds.");
            }

            return new StepResult(process.ExitCode, await output, await error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Tells whether a program is in one of the folders of <c>PATH</c>.</summary>
    /// <param name="program">The program's file name.</param>
    /// <returns>Whether a file of that name is there.</returns>
    private static bool IsOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, program)));
}
