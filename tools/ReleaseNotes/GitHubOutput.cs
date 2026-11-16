namespace CalCrony.ReleaseNotes;

/// <summary>Writes a step's outputs in the form GitHub Actions reads from the file named by
/// <c>GITHUB_OUTPUT</c>.</summary>
public static class GitHubOutput
{
    /// <summary>Formats one output. A value of one line is written as <c>name=value</c>; a value
    /// of several lines between a delimiter line that is new every time and is not in the value.</summary>
    /// <param name="name">The output's name: letters, digits, underscores and hyphens.</param>
    /// <param name="value">The output's value, with line feeds between its lines.</param>
    /// <returns>The lines to append to the outputs file, each ended by a line feed.</returns>
    public static string Format(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new ArgumentException("An output name is letters, digits, underscores and hyphens.", nameof(name));
        }

        if (!value.Contains('\n', StringComparison.Ordinal))
        {
            return $"{name}={value}\n";
        }

        string delimiter = NewDelimiter(value);
        return $"{name}<<{delimiter}\n{value}\n{delimiter}\n";
    }

    /// <summary>Appends one output to the outputs file.</summary>
    /// <param name="path">The file named by <c>GITHUB_OUTPUT</c>.</param>
    /// <param name="name">The output's name.</param>
    /// <param name="value">The output's value.</param>
    public static void Append(string path, string name, string value) =>
        File.AppendAllText(path, Format(name, value));

    /// <summary>Makes a delimiter line that does not occur in a value.</summary>
    /// <param name="value">The value the delimiter will enclose.</param>
    /// <returns>The delimiter.</returns>
    private static string NewDelimiter(string value)
    {
        string delimiter;

        do
        {
            delimiter = $"ghadelimiter_{Guid.NewGuid():N}";
        }
        while (value.Contains(delimiter, StringComparison.Ordinal));

        return delimiter;
    }
}
