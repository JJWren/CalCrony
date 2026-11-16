using System.Globalization;

namespace CalCrony.ReleaseNotes;

/// <summary>Writes the lines GitHub Actions reads from a step's output as commands. What goes
/// into one is escaped the way the runner expects, so that no text can end a command early or
/// start another.</summary>
public static class WorkflowCommand
{
    /// <summary>Writes a warning the run shows against a file.</summary>
    /// <param name="file">The file's path from the repository root.</param>
    /// <param name="line">The line the warning is about, or 0 for the file as a whole.</param>
    /// <param name="message">The warning's text.</param>
    /// <returns>The command, on one line.</returns>
    public static string Warning(string file, int line, string message)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(message);

        string place = line > 0
            ? string.Create(CultureInfo.InvariantCulture, $"file={EscapeProperty(file)},line={line}")
            : $"file={EscapeProperty(file)}";

        return $"::warning {place}::{EscapeData(message)}";
    }

    /// <summary>Escapes a command's text: a percent sign and the two line-end characters.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The text, safe after the last <c>::</c> of a command.</returns>
    private static string EscapeData(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    /// <summary>Escapes a command's property: what <see cref="EscapeData"/> escapes, and the
    /// colon and comma that separate properties.</summary>
    /// <param name="value">The property's value.</param>
    /// <returns>The value, safe inside a command's property list.</returns>
    private static string EscapeProperty(string value) =>
        EscapeData(value)
            .Replace(":", "%3A", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal);
}
