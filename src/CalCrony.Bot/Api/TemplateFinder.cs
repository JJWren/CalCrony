using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

/// <summary>Resolves user-typed template references (autocomplete ids or names) to a single
/// template, with friendly ambiguity messages.</summary>
public static class TemplateFinder
{
    /// <summary>Finds exactly one template by id (what the autocomplete picker submits), exact
    /// name, or unambiguous name fragment (all case-insensitive).</summary>
    /// <param name="api">The CalCrony API client.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="name">Template name (or fragment), or an autocomplete-picked template id.</param>
    /// <returns>The single match, or a user-facing problem message.</returns>
    public static async Task<(EventTemplateDto? Template, string? Problem)> FindSingleAsync(
        CalCronyApiClient api, long guildId, string name)
    {
        var result = await api.ListTemplatesAsync(guildId);
        if (!result.Success || result.Value is null)
        {
            return (null, $"❌ {result.Error}");
        }

        return PickSingle(result.Value, name);
    }

    /// <summary>Pure match logic, public for direct testing.</summary>
    /// <param name="templates">The candidate templates.</param>
    /// <param name="name">Template name (or fragment), or a template id.</param>
    /// <returns>The single match, or a user-facing problem message.</returns>
    public static (EventTemplateDto? Template, string? Problem) PickSingle(
        IReadOnlyList<EventTemplateDto> templates, string name)
    {
        if (Guid.TryParse(name, out var id))
        {
            var byId = templates.FirstOrDefault(t => t.Id == id);
            return byId is not null ? (byId, null) : (null, "That template no longer exists.");
        }

        var exact = templates
            .Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1)
        {
            return (exact[0], null);
        }

        var matches = templates
            .Where(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            0 => (null, $"No template named \"{name}\". Use /template list to see what's saved."),
            1 => (matches[0], null),
            _ => (null, $"Multiple templates match \"{name}\": {string.Join(", ", matches.Select(m => $"**{m.Name}**"))}. Pick one from the option list, or be more specific."),
        };
    }
}
