using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

public static class EventFinder
{
    /// <summary>Finds exactly one upcoming event whose title contains <paramref name="name"/> (case-insensitive).</summary>
    public static async Task<(EventDto? Event, string? Problem)> FindSingleAsync(
        CalCronyApiClient api, long guildId, string name)
    {
        var result = await api.ListEventsAsync(guildId, limit: 25);
        if (!result.Success || result.Value is null)
        {
            return (null, $"❌ {result.Error}");
        }

        var matches = result.Value
            .Where(e => e.Title.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => (null, $"No upcoming event matching \"{name}\"."),
            1 => (matches[0], null),
            _ => (null, $"Multiple events match \"{name}\": {string.Join(", ", matches.Select(m => $"**{m.Title}**"))}. Be more specific."),
        };
    }
}
