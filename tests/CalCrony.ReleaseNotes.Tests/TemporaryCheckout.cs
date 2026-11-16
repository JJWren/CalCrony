namespace CalCrony.ReleaseNotes.Tests;

/// <summary>A throwaway checkout: a folder with only what a test puts in it, deleted afterwards.</summary>
internal sealed class TemporaryCheckout : IDisposable
{
    /// <summary>Creates an empty folder under the system's temporary folder.</summary>
    public TemporaryCheckout()
    {
        Root = Directory.CreateTempSubdirectory("calcrony-release-notes-").FullName;
    }

    /// <summary>The folder standing in for the repository root.</summary>
    public string Root { get; }

    /// <summary>Writes a file, creating its folders.</summary>
    /// <param name="repositoryPath">The file's path from the root, with forward slashes.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The file's full path.</returns>
    public string Write(string repositoryPath, string text)
    {
        string path = Path.Combine(Root, repositoryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Writes a release summary for a version.</summary>
    /// <param name="version">The version, as text: "0.38.0".</param>
    /// <param name="text">The summary's text.</param>
    public void WriteSummary(string version, string text) =>
        Write($"{ReleaseSummary.FolderPath}/{version}.md", text);

    /// <summary>Gives the path of a file under the root, existing or not.</summary>
    /// <param name="repositoryPath">The file's path from the root, with forward slashes.</param>
    /// <returns>The file's full path.</returns>
    public string PathOf(string repositoryPath) => Path.Combine(Root, repositoryPath);

    /// <summary>Deletes the folder and everything in it.</summary>
    public void Dispose() => Directory.Delete(Root, true);
}
