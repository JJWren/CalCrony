namespace CalCrony.ReleaseNotes.Tests;

/// <summary>Marks a test that creates a symbolic link. It is skipped where this user may not
/// create one — on Windows that takes Developer Mode or an elevated prompt — and never in CI,
/// where it should fail rather than hide.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> CanCreateLinks = new(TryCreateLink);

    /// <summary>Decides whether the test runs on this machine.</summary>
    public SymbolicLinkFactAttribute()
    {
        bool isCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

        if (!CanCreateLinks.Value && !isCi)
        {
            Skip = "This user may not create symbolic links here (on Windows: Developer Mode or an elevated prompt); CI runs this test.";
        }
    }

    /// <summary>Tries to create a symbolic link in a throwaway folder.</summary>
    /// <returns>Whether the link could be created.</returns>
    private static bool TryCreateLink()
    {
        string folder = Directory.CreateTempSubdirectory("calcrony-link-probe-").FullName;

        try
        {
            string target = Path.Combine(folder, "target.txt");
            File.WriteAllText(target, "target");
            File.CreateSymbolicLink(Path.Combine(folder, "link.txt"), target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
