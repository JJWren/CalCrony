using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Bot.Api;

/// <summary>Autocomplete for event-name parameters: as the user types, matching events appear
/// with their start time (and a past marker) so same-prefix titles like "Test" vs "TestTitle"
/// are distinguishable and selectable. The submitted value is the event id, which
/// EventFinder.FindSingleAsync resolves directly; free-typed text still falls back to
/// name matching, so nothing breaks without a selection.</summary>
public class EventNameAutocompleteHandler : AutocompleteHandler
{
    /// <summary>Builds the suggestion list from the guild's recent and upcoming events as the user types.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="autocompleteInteraction">The in-flight autocomplete interaction.</param>
    /// <param name="parameter">The parameter being completed.</param>
    /// <param name="services">The request service provider.</param>
    /// <returns>The suggestion set for Discord to display.</returns>
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var api = services.GetRequiredService<CalCronyApiClient>();
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";

        var result = await api.ListEventsAsync((long)context.Guild.Id, limit: 25, includePast: true);
        if (!result.Success || result.Value is null)
        {
            return AutocompletionResult.FromSuccess([]);
        }

        return AutocompletionResult.FromSuccess(
            BuildSuggestions(result.Value, input, DateTimeOffset.UtcNow));
    }

    /// <summary>Pure suggestion shaping, public for direct testing: filter by fragment, upcoming
    /// first (soonest at the top) then past (most recent first, marked), capped at Discord's 25.</summary>
    /// <param name="events">The candidate events.</param>
    /// <param name="input">The user's current (possibly partial) input.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Up to 25 labeled, id-valued suggestions.</returns>
    public static List<AutocompleteResult> BuildSuggestions(
        IReadOnlyList<EventDto> events, string input, DateTimeOffset now)
    {
        var matches = events
            .Where(e => e.Title.Contains(input, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ordered = matches.Where(e => e.StartsAtUtc >= now).OrderBy(e => e.StartsAtUtc)
            .Concat(matches.Where(e => e.StartsAtUtc < now).OrderByDescending(e => e.StartsAtUtc));

        return [.. ordered.Take(25).Select(e => new AutocompleteResult(Label(e, now), e.Id.ToString()))];
    }

    /// <summary>Suggestion label: title plus start time in the event's zone, marked when past, capped at 100 chars.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The display label, capped at 100 chars.</returns>
    private static string Label(EventDto ev, DateTimeOffset now)
    {
        string when;
        try
        {
            var local = TimeZoneInfo.ConvertTime(ev.StartsAtUtc, TimeZoneInfo.FindSystemTimeZoneById(ev.TimeZone));
            when = local.ToString("ddd MMM d, h:mm tt");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            when = ev.StartsAtUtc.ToString("ddd MMM d, h:mm tt 'UTC'");
        }

        var marker = ev.StartsAtUtc < now ? " (past)" : "";
        var label = $"{ev.Title} — {when}{marker}";
        return label.Length <= 100 ? label : label[..100];
    }
}
