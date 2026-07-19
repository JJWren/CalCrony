using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

/// <summary>Resolves user-typed event references (autocomplete ids or title fragments) to a
/// single event, with friendly ambiguity messages.</summary>
public static class EventFinder
{
    /// <summary>Finds exactly one event. Accepts an event id (what the autocomplete picker
    /// submits) or a title fragment (case-insensitive; a single exact title match wins over
    /// fragment-matches of longer titles, so "Test" isn't ambiguous with "TestTitle").
    /// Upcoming events only unless <paramref name="includePast"/> — needed to reach a
    /// fully-ended series, whose occurrences are all in the past.</summary>
    public static async Task<(EventDto? Event, string? Problem)> FindSingleAsync(
        CalCronyApiClient api, long guildId, string name, bool includePast = false)
    {
        if (Guid.TryParse(name, out var id))
        {
            var byId = await api.GetEventAsync(id);
            if (!byId.Success)
            {
                // Only genuine not-found reads as "gone" — API outages surface as themselves.
                return byId.Error?.Contains("404") == true
                    ? (null, "That event no longer exists.")
                    : (null, $"❌ {byId.Error}");
            }

            return byId.Value is not null && byId.Value.GuildId == guildId
                ? (byId.Value, null)
                : (null, "That event no longer exists.");
        }

        var result = await api.ListEventsAsync(guildId, limit: 25, includePast: includePast);
        if (!result.Success || result.Value is null)
        {
            return (null, $"❌ {result.Error}");
        }

        return PickSingle(result.Value, name, includePast);
    }

    /// <summary>Pure match logic, public for direct testing.</summary>
    public static (EventDto? Event, string? Problem) PickSingle(
        IReadOnlyList<EventDto> events, string name, bool includePast)
    {
        var matches = events
            .Where(e => e.Title.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
        {
            var exact = matches
                .Where(e => e.Title.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
            {
                return (exact[0], null);
            }
        }

        return matches.Count switch
        {
            0 => (null, $"No {(includePast ? "" : "upcoming ")}event matching \"{name}\"."),
            1 => (matches[0], null),
            _ => (null, $"Multiple events match \"{name}\": {string.Join(", ", matches.Select(m => $"**{m.Title}**"))}. Pick one from the option list, or be more specific."),
        };
    }
}
