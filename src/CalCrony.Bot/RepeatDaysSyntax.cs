using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>Parses the <c>repeat-days</c> slash-command option into a weekly day set. Pure and
/// static (like RsvpOptionSyntax) for direct testing; the API remains the validator of record
/// for the resulting flags.
///
/// Grammar: day names separated by commas, spaces, slashes, plus signs, or "and" — each name a
/// full weekday ("Tuesday") or any unambiguous prefix of at least two letters ("tue", "tues",
/// "thu", "thur", "weds"). Presets: <c>weekdays</c> (Mon–Fri) and <c>weekends</c> (Sat+Sun),
/// combinable with days ("weekdays, sat"). <c>none</c> clears the set (edit commands only).</summary>
public static class RepeatDaysSyntax
{
    private static readonly char[] Separators = [',', ' ', '/', '+', '&', ';'];

    private static readonly (string Name, RecurrenceDays Day)[] DayNames =
    [
        ("monday", RecurrenceDays.Monday),
        ("tuesday", RecurrenceDays.Tuesday),
        ("wednesday", RecurrenceDays.Wednesday),
        ("thursday", RecurrenceDays.Thursday),
        ("friday", RecurrenceDays.Friday),
        ("saturday", RecurrenceDays.Saturday),
        ("sunday", RecurrenceDays.Sunday),
    ];

    /// <summary>Parses the day-set text.</summary>
    /// <param name="input">The raw <c>repeat-days</c> value.</param>
    /// <param name="days">The parsed set on success (None only when <paramref name="allowNone"/>
    /// and the input was "none").</param>
    /// <param name="error">The user-facing problem on failure.</param>
    /// <param name="allowNone">Whether the literal "none" is accepted as "clear the day set".</param>
    /// <returns>True when the input parsed.</returns>
    public static bool TryParse(string input, out RecurrenceDays days, out string? error, bool allowNone = false)
    {
        days = RecurrenceDays.None;
        error = null;

        var tokens = input.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Equals("and", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tokens.Count == 0)
        {
            error = "Give at least one day, e.g. `tue,thu` or `weekdays`.";
            return false;
        }

        if (allowNone && tokens.Count == 1 && tokens[0].Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (token.StartsWith("weekday", StringComparison.OrdinalIgnoreCase))
            {
                days |= RecurrenceDays.Weekdays;
                continue;
            }

            if (token.StartsWith("weekend", StringComparison.OrdinalIgnoreCase))
            {
                days |= RecurrenceDays.Saturday | RecurrenceDays.Sunday;
                continue;
            }

            // Prefix matching accepts every common abbreviation without a table of them ("tues",
            // "thur", "thurs"); "weds" is the one popular form that isn't a prefix. A one-letter
            // token is rejected rather than guessed — "t" and "s" are ambiguous, and a lone
            // "m"/"w"/"f" reads as a typo.
            var trimmed = token.TrimEnd('.');
            if (trimmed.Equals("weds", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "wed";
            }

            var matches = DayNames.Where(d => d.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1)
            {
                error = $"\"{token}\" could be more than one day — spell out a bit more, e.g. `tue` or `thu`.";
                return false;
            }

            if (matches.Count == 0 || trimmed.Length < 2)
            {
                error = $"\"{token}\" isn't a weekday — use names like `mon,wed,fri` or the `weekdays` preset.";
                return false;
            }

            days |= matches[0].Day;
        }

        return true;
    }
}
