namespace CalCrony.ReleaseNotes.Tests;

/// <summary>Marks a test that asks git about the checkout the tests run from. It is skipped where
/// the files are not a git checkout — a copy made with <c>git archive</c>, say — and never in CI,
/// where it should fail rather than hide.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GitCheckoutFactAttribute : FactAttribute
{
    /// <summary>Whether git's entry stands beside the solution file: a folder in a clone, a file
    /// in a worktree.</summary>
    private static readonly Lazy<bool> IsCheckout = new(() => Path.Exists(Path.Combine(Repository.Root, ".git")));

    /// <summary>Decides whether the test runs here.</summary>
    public GitCheckoutFactAttribute()
    {
        bool isCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

        if (!isCi && !IsCheckout.Value)
        {
            Skip = "These files are not a git checkout (nothing named .git beside the solution file); CI runs this test.";
        }
    }
}
