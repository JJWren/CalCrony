namespace CalCrony.ReleaseNotes;

/// <summary>The options of one command line, each written as <c>--name value</c>.</summary>
public sealed class CliOptions
{
    private const string Prefix = "--";
    private const string RootName = "root";

    private readonly Dictionary<string, string> values;

    /// <summary>Wraps the options read from a command line.</summary>
    /// <param name="values">The options by name, without their dashes.</param>
    private CliOptions(Dictionary<string, string> values)
    {
        this.values = values;
    }

    /// <summary>The folder the repository is checked out in: the <c>--root</c> option, or the
    /// folder the tool was started in.</summary>
    public string Root => Value(RootName) ?? Directory.GetCurrentDirectory();

    /// <summary>Reads the options that follow a command. Every command also takes
    /// <c>--root</c>.</summary>
    /// <param name="arguments">The arguments after the command's name.</param>
    /// <param name="required">The options the command cannot do without.</param>
    /// <param name="optional">The options the command also takes.</param>
    /// <param name="options">The options, when the arguments are well formed.</param>
    /// <param name="problem">What is wrong with the arguments, when they are not.</param>
    /// <returns>Whether the arguments are well formed and complete.</returns>
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional,
        out CliOptions options,
        out string problem)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        options = new CliOptions(values);
        problem = string.Empty;

        if (arguments.Count % 2 != 0)
        {
            problem = "Every option takes a value: --name value.";
            return false;
        }

        for (int index = 0; index < arguments.Count; index += 2)
        {
            string name = arguments[index];

            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                problem = $"Expected an option, found \"{name}\".";
                return false;
            }

            string bareName = name[Prefix.Length..];

            if (bareName != RootName && !required.Contains(bareName) && !optional.Contains(bareName))
            {
                problem = $"Unknown option {name}.";
                return false;
            }

            if (!values.TryAdd(bareName, arguments[index + 1]))
            {
                problem = $"{name} is given twice.";
                return false;
            }
        }

        string? missing = required.FirstOrDefault(name => !values.ContainsKey(name));

        if (missing is not null)
        {
            problem = $"{Prefix}{missing} is required.";
            return false;
        }

        return true;
    }

    /// <summary>Looks an option up.</summary>
    /// <param name="name">The option's name, without its dashes.</param>
    /// <returns>The option's value, or null when it was not given.</returns>
    public string? Value(string name) => values.GetValueOrDefault(name);

    /// <summary>Looks up an option the command requires.</summary>
    /// <param name="name">The option's name, without its dashes.</param>
    /// <returns>The option's value.</returns>
    public string Required(string name) => values[name];
}
