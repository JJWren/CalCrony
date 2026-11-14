namespace CalCrony.ReleaseNotes.Tests;

/// <summary>Marks a test that runs the announce step's own script against the stand-in webhook.
/// It is skipped where the script cannot run — a Windows machine, or one without jq — and never
/// in CI, where a missing tool should fail the test rather than hide it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AnnounceStepFactAttribute : FactAttribute
{
    /// <summary>Decides whether the test runs on this machine.</summary>
    public AnnounceStepFactAttribute()
    {
        bool isCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

        if (!StepRunner.CanRun && !isCi)
        {
            Skip = StepRunner.WhyNot;
        }
    }
}
