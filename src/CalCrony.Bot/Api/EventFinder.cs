using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

public static class EventFinder
{
    /// <summary>Finds exactly one event whose title contains <paramref name="name"/>
    /// (case-insensitive). Upcoming events only unless <paramref name="includePast"/> — needed to
    /// reach a fully-ended series, whose occurrences are all in the past.</summary>
    public static async Task<(EventDto? Event, string? Problem)> FindSingleAsync(
        CalCronyApiClient api, long guildId, string name, bool includePast = false)
    {
        var result = await api.ListEventsAsync(guildId, limit: 25, includePast: includePast);
        if (!result.Success || result.Value is null)
        {
            return (null, $"❌ {result.Error}");
        }

        var matches = result.Value
            .Where(e => e.Title.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => (null, $"No {(includePast ? "" : "upcoming ")}event matching \"{name}\"."),
            1 => (matches[0], null),
            _ => (null, $"Multiple events match \"{name}\": {string.Join(", ", matches.Select(m => $"**{m.Title}**"))}. Be more specific."),
        };
    }
}
