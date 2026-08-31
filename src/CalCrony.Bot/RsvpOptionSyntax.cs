using System.Text.RegularExpressions;
using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>Parses the /create and /edit <c>rsvp-options</c> string into option specs. Pure and
/// static (like AttendeeRoleSpec) for direct testing. The syntax is presentation-layer sugar —
/// the API validates the resulting specs as the rules of record.
///
/// Grammar: comma-separated entries, each <c>[emoji] label [xN]</c>, with a trailing <c>*</c>
/// marking the attending option (default: the first). Example:
/// <c>"⚔️ Raider x10, 🛡️ Standby, ❌ Can't make it"</c>.</summary>
public static partial class RsvpOptionSyntax
{
    private const int MaxOptions = 10;
    private const string DefaultEmote = "🔹";

    /// <summary>Trailing <c>xN</c> capacity. Any digit run is a capacity token — a number too
    /// large for the API is an error, never silently label text.</summary>
    [GeneratedRegex(@"^x(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CapacityToken();

    /// <summary>Parses the delimited option string.</summary>
    /// <param name="input">The raw <c>rsvp-options</c> value.</param>
    /// <param name="specs">The parsed specs on success.</param>
    /// <param name="error">The user-facing problem on failure.</param>
    /// <returns>True when the input parsed.</returns>
    public static bool TryParse(string input, out List<RsvpOptionSpec> specs, out string? error)
    {
        specs = [];
        error = null;

        var entries = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length is < 1 or > MaxOptions)
        {
            error = $"Give between 1 and {MaxOptions} RSVP options, separated by commas.";
            return false;
        }

        var sawAttending = false;
        foreach (var rawEntry in entries)
        {
            var entry = rawEntry;
            var isAttending = entry.EndsWith('*');
            if (isAttending)
            {
                if (sawAttending)
                {
                    error = "Only one option can carry the attending `*` marker.";
                    return false;
                }

                sawAttending = true;
                entry = entry[..^1].TrimEnd();
            }

            var tokens = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (tokens.Count > 0 && EmoteText.IsCustomEmote(tokens[0]))
            {
                error = "Custom server emojis aren't supported on RSVP buttons — use a standard emoji.";
                return false;
            }

            // Leading emoji token = the button emoji; a plain-text first token (including
            // accented words like "Café") means "no emoji given" and gets the default.
            var emote = DefaultEmote;
            if (tokens.Count > 0 && LooksLikeEmoji(tokens[0]))
            {
                emote = tokens[0];
                tokens.RemoveAt(0);
            }

            // Trailing xN = capacity.
            int? capacity = null;
            if (tokens.Count > 0 && CapacityToken().Match(tokens[^1]) is { Success: true } match)
            {
                if (!int.TryParse(
                        match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsedCapacity)
                    || parsedCapacity < 1)
                {
                    error = $"Capacity \"{tokens[^1]}\" must be a whole number between 1 and {int.MaxValue}.";
                    return false;
                }

                capacity = parsedCapacity;
                tokens.RemoveAt(tokens.Count - 1);
            }

            var label = string.Join(' ', tokens);
            if (label.Length == 0)
            {
                error = $"Every RSVP option needs a label — couldn't read \"{rawEntry}\".";
                return false;
            }

            specs.Add(new RsvpOptionSpec(emote, label, capacity, isAttending));
        }

        // No * marker means the first option attends — made explicit here so the specs are
        // self-describing (the API would default the same way, but only implicitly).
        if (!sawAttending)
        {
            specs[0] = specs[0] with { IsAttending = true };
        }

        return true;
    }

    /// <summary>Whether a token reads as an emoji rather than label text — the API's own
    /// classifier, so keycaps (1️⃣) and BMP stragglers (‼️) become the button emoji here exactly
    /// when the API would accept them. Accented words stay label text.</summary>
    /// <param name="token">The whitespace-delimited token.</param>
    /// <returns>True when the token should become the button emoji.</returns>
    private static bool LooksLikeEmoji(string token) => EmoteText.IsLikelyEmoji(token);
}
