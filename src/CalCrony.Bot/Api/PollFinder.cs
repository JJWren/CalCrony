using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

public static class PollFinder
{
    /// <summary>Finds exactly one poll whose question contains <paramref name="name"/>
    /// (case-insensitive), optionally filtered by status.</summary>
    public static async Task<(PollDto? Poll, string? Problem)> FindSingleAsync(
        CalCronyApiClient api, long guildId, string name, PollStatus? status = null)
    {
        var result = await api.ListPollsAsync(guildId, status, limit: 25);
        if (!result.Success || result.Value is null)
        {
            return (null, $"❌ {result.Error}");
        }

        var matches = result.Value
            .Where(p => p.Question.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => (null, $"No {(status is null ? "" : $"{status.ToString()!.ToLowerInvariant()} ")}poll matching \"{name}\"."),
            1 => (matches[0], null),
            _ => (null, $"Multiple polls match \"{name}\": {string.Join(", ", matches.Select(m => $"**{m.Question}**"))}. Be more specific."),
        };
    }
}
