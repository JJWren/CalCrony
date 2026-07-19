using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Bot.Api;

/// <summary>Autocomplete for timezone parameters so nobody types IANA ids from memory: an empty
/// box shows major zones across the globe; typing filters the full canonical list ("chi" →
/// "America/Chicago — UTC-05:00"). The zone list rarely changes, so it's fetched from the API
/// once and cached for the process lifetime; free-typed ids still validate server-side.</summary>
public class TimeZoneAutocompleteHandler : AutocompleteHandler
{
    /// <summary>Shown for an empty search box: one familiar anchor per major region.</summary>
    private static readonly string[] CommonZones =
    [
        "UTC",
        "America/New_York",
        "America/Chicago",
        "America/Denver",
        "America/Los_Angeles",
        "America/Anchorage",
        "Pacific/Honolulu",
        "America/Sao_Paulo",
        "Europe/London",
        "Europe/Paris",
        "Europe/Berlin",
        "Europe/Moscow",
        "Africa/Cairo",
        "Africa/Johannesburg",
        "Asia/Dubai",
        "Asia/Kolkata",
        "Asia/Shanghai",
        "Asia/Tokyo",
        "Asia/Singapore",
        "Australia/Sydney",
        "Pacific/Auckland",
    ];

    // Labels carry the CURRENT UTC offset, so the cache must refresh across DST transitions —
    // 12h keeps labels accurate without per-keystroke API calls.
    private sealed record CacheEntry(IReadOnlyList<TimeZoneOptionDto> Options, DateTimeOffset Expires);

    private static volatile CacheEntry? cache;

    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var entry = cache;
        if (entry is null || entry.Expires <= DateTimeOffset.UtcNow)
        {
            var api = services.GetRequiredService<CalCronyApiClient>();
            var result = await api.ListTimeZonesAsync();
            if (!result.Success || result.Value is null)
            {
                return AutocompletionResult.FromSuccess([]);
            }

            entry = new CacheEntry(result.Value, DateTimeOffset.UtcNow.AddHours(12));
            cache = entry;
        }

        var options = entry.Options;

        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        return AutocompletionResult.FromSuccess(BuildSuggestions(options, input));
    }

    /// <summary>Pure suggestion shaping, public for direct testing.</summary>
    public static List<AutocompleteResult> BuildSuggestions(
        IReadOnlyList<TimeZoneOptionDto> options, string input)
    {
        IEnumerable<TimeZoneOptionDto> picked;
        if (string.IsNullOrWhiteSpace(input))
        {
            var byId = options.ToDictionary(o => o.Id);
            picked = CommonZones.Where(byId.ContainsKey).Select(id => byId[id]);
        }
        else
        {
            picked = options.Where(o => o.Id.Contains(input.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return [.. picked.Take(25).Select(o => new AutocompleteResult(o.Label, o.Id))];
    }
}
