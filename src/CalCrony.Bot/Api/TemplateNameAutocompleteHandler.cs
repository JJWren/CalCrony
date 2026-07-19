using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Bot.Api;

/// <summary>Autocomplete for template-name parameters: matching templates appear as the user
/// types, labeled with their title and content markers; the submitted value is the template id,
/// which TemplateFinder resolves directly. Free-typed names still resolve by name.</summary>
public class TemplateNameAutocompleteHandler : AutocompleteHandler
{
    /// <summary>Builds the suggestion list from the guild's templates as the user types.</summary>
    /// <param name="context">The current interaction context.</param>
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

        var result = await api.ListTemplatesAsync((long)context.Guild.Id);
        if (!result.Success || result.Value is null)
        {
            return AutocompletionResult.FromSuccess([]);
        }

        return AutocompletionResult.FromSuccess(BuildSuggestions(result.Value, input));
    }

    /// <summary>Pure suggestion shaping, public for direct testing.</summary>
    /// <param name="templates">The candidate templates.</param>
    /// <param name="input">The user's current (possibly partial) input.</param>
    /// <returns>Up to 25 labeled, id-valued suggestions.</returns>
    public static List<AutocompleteResult> BuildSuggestions(
        IReadOnlyList<EventTemplateDto> templates, string input)
    {
        return [.. templates
            .Where(t => t.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Select(t => new AutocompleteResult(Label(t), t.Id.ToString()))];
    }

    /// <summary>Suggestion label: name plus a content summary, capped at Discord's 100 chars.</summary>
    /// <param name="t">The template.</param>
    /// <returns>The display label.</returns>
    private static string Label(EventTemplateDto t)
    {
        var markers = "";
        if (t.Recurrence is not null)
        {
            markers += " · 🔁";
        }

        if (t.Notifications.Count > 0)
        {
            markers += $" · 🔔{t.Notifications.Count}";
        }

        var label = $"{t.Name} — {t.Title}{markers}";
        return label.Length <= 100 ? label : label[..100];
    }
}
