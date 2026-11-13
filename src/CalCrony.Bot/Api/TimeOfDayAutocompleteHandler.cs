using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Api;

/// <summary>Autocomplete for the <c>from</c> and <c>to</c> options of
/// <c>/settings available-hours set</c>: the times a member can pick, on the step between
/// selectable times. An empty box shows the whole hours; typing narrows them ("6" → 6 AM, 6:30 AM,
/// 6 PM, 6:30 PM). Nothing is fetched — the list is the clock. Typed text that is not picked from
/// the list is still read by <see cref="TimeOfDayText.TryParse"/> when the command runs.</summary>
public class TimeOfDayAutocompleteHandler : AutocompleteHandler
{
    /// <summary>Discord shows at most this many suggestions.</summary>
    public const int MaxSuggestions = 25;

    private const string EndOptionName = "to";
    private const int MinutesPerHour = 60;

    /// <summary>Serves time suggestions for the option being typed into.</summary>
    /// <param name="context">The interaction context.</param>
    /// <param name="autocompleteInteraction">The in-flight autocomplete interaction.</param>
    /// <param name="parameter">The parameter being completed.</param>
    /// <param name="services">The request service provider.</param>
    /// <returns>The suggestion set for Discord to display.</returns>
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var focused = autocompleteInteraction.Data.Current;
        var input = focused.Value?.ToString() ?? "";
        var asEnd = string.Equals(focused.Name, EndOptionName, StringComparison.Ordinal);
        return Task.FromResult(AutocompletionResult.FromSuccess(BuildSuggestions(input, asEnd)));
    }

    /// <summary>Pure suggestion shaping, public for direct testing. A start can be any time on the
    /// step from 12 AM to 11:30 PM; an end any from 12:30 AM to midnight. Each suggestion shows the
    /// 12-hour label and sends the 24-hour form, which reads back to the same time.</summary>
    /// <param name="input">The member's current (possibly partial) input.</param>
    /// <param name="asEnd">Whether the option ends a range rather than starts one.</param>
    /// <returns>Up to <see cref="MaxSuggestions"/> suggestions, in the order of the clock, with a
    /// time the input names exactly placed first.</returns>
    public static List<AutocompleteResult> BuildSuggestions(string? input, bool asEnd)
    {
        var step = AvailableHoursLimits.StepMinutes;
        var first = asEnd ? step : 0;
        var last = asEnd ? AvailableHoursLimits.MinutesPerDay : AvailableHoursLimits.MinutesPerDay - step;
        var candidates = Enumerable.Range(0, ((last - first) / step) + 1).Select(i => first + (i * step));

        var typed = Normalize(input);
        IEnumerable<int> picked;
        if (typed.Length == 0)
        {
            picked = candidates.Where(minutes => minutes % MinutesPerHour == 0);
        }
        else
        {
            var matches = candidates.Where(minutes => Forms(minutes, asEnd).Any(form => form.StartsWith(typed, StringComparison.Ordinal)));

            // What the text already names goes first, so pressing Enter takes it.
            picked = TimeOfDayText.TryParse(input, asEnd, out var exact) && exact % step == 0 && exact >= first && exact <= last
                ? matches.Where(minutes => minutes != exact).Prepend(exact)
                : matches;
        }

        return [.. picked.Take(MaxSuggestions).Select(minutes => new AutocompleteResult(TimeOfDayText.Format(minutes), TimeOfDayText.To24Hour(minutes)))];
    }

    /// <summary>The ways a time can be typed, for matching what has been typed so far: its label
    /// ("6:30pm"), its 24-hour form with and without the leading zero ("18:30", "6:30"), and the
    /// words for noon and midnight.</summary>
    /// <param name="minutes">The time, in minutes from midnight.</param>
    /// <param name="asEnd">Whether the option ends a range rather than starts one.</param>
    /// <returns>The forms, lower-case and without spaces.</returns>
    private static IEnumerable<string> Forms(int minutes, bool asEnd)
    {
        yield return Normalize(TimeOfDayText.Format(minutes));

        var padded = TimeOfDayText.To24Hour(minutes);
        yield return padded;
        yield return padded[0] == '0' ? padded[1..] : padded;       // "06:30" is also typed "6:30"

        if (minutes == 12 * MinutesPerHour)
        {
            yield return "noon";
        }

        // Midnight is the start of the day as a start and the end of it as an end.
        if (minutes == (asEnd ? AvailableHoursLimits.MinutesPerDay : 0))
        {
            yield return "midnight";
            yield return "12am";
        }
    }

    /// <summary>Text as it is compared: lower-case, with no spaces.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    private static string Normalize(string? text) =>
        (text ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
}
