namespace CalCrony.ReleaseNotes.Tests;

/// <summary>Marks a test that runs a workflow step's own script: the announce step against the
/// stand-in webhook, or a step of the summary job against a stand-in <c>gh</c>. It is skipped
/// where such a script cannot run — a Windows machine, or one without jq — and never in CI,
/// where a missing tool should fail the test rather than hide it.</summary>
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
