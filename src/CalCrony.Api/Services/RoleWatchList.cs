using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Services;

/// <summary>Which Discord roles the API needs snapshots for: every role named by a LIVE signup
/// restriction — an option on a scheduled/started event, a running series' option template, or
/// an open poll. The set is derived on demand rather than stored, so it can never drift from the
/// restrictions themselves; the bot reads it at Ready (and after any command that names roles)
/// and pushes exactly those roles' snapshots back. Retention uses the same answer to drop the
/// snapshots of guilds whose restrictions have all ended (ADR 0004).</summary>
public static class RoleWatchList
{
    /// <summary>The watched role ids per guild, for every guild with at least one live
    /// restriction (bot-absent guilds included — callers filter). Guilds with none are absent.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Guild id to the distinct roles its live restrictions name.</returns>
    public static async Task<Dictionary<long, HashSet<long>>> WatchedByGuildAsync(
        CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var watched = new Dictionary<long, HashSet<long>>();

        // Cardinality on the array column keeps the scan to restricted options only.
        var fromEvents = await db.RsvpOptions
            .Where(o => o.AllowedRoleIds.Length > 0)
            .Join(
                db.Events.Where(e => e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started),
                o => o.EventId, e => e.Id,
                (o, e) => new { e.GuildId, o.AllowedRoleIds })
            .ToListAsync(cancellationToken);
        foreach (var option in fromEvents)
        {
            Add(watched, option.GuildId, option.AllowedRoleIds);
        }

        // A running series spawns its next occurrence from the template, so its restrictions are
        // live even between occurrences. Templates are JSON; the running set is small, so the
        // specs are read in memory rather than through a JSON query.
        var fromSeries = await db.EventSeries
            .Where(s => !s.Ended && s.RsvpOptionsJson != null)
            .Select(s => new { s.GuildId, s.RsvpOptionsJson })
            .ToListAsync(cancellationToken);
        foreach (var series in fromSeries)
        {
            foreach (var option in RsvpPolicy.OptionsFromTemplate(series.RsvpOptionsJson))
            {
                Add(watched, series.GuildId, option.AllowedRoleIds);
            }
        }

        var fromPolls = await db.Polls
            .Where(p => p.Status == PollStatus.Open && p.AllowedRoleIds.Length > 0)
            .Select(p => new { p.GuildId, p.AllowedRoleIds })
            .ToListAsync(cancellationToken);
        foreach (var poll in fromPolls)
        {
            Add(watched, poll.GuildId, poll.AllowedRoleIds);
        }

        return watched;
    }

    private static void Add(Dictionary<long, HashSet<long>> watched, long guildId, long[] roleIds)
    {
        if (roleIds.Length == 0)
        {
            return;
        }

        if (!watched.TryGetValue(guildId, out var set))
        {
            set = [];
            watched[guildId] = set;
        }

        set.UnionWith(roleIds);
    }
}
