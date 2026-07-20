using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/availability — free/busy grids for a role's members or an event's Going list.</summary>
/// <param name="api">The CalCrony API client.</param>
[RequireContext(ContextType.Guild)]
[Group("availability", "Check group calendar availability")]
public class AvailabilityModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaxUsersPerQuery = 50;

    /// <summary>Free/busy for everyone holding a role over an ad-hoc window.</summary>
    /// <param name="role">The Discord role whose members are checked.</param>
    /// <param name="when">Natural-language start time.</param>
    /// <param name="duration">Duration in minutes.</param>
    [SlashCommand("role", "Check calendar availability for everyone with a role")]
    public async Task RoleAsync(
        [Summary("role", "The role to check")] IRole role,
        [Summary("when", "When to check, e.g. \"tomorrow 2pm\"")] string when,
        [Summary("duration", "Duration in minutes (default 60)")] int? duration = null)
    {
        await DeferAsync(ephemeral: true);

        // Enumerating role members needs the guild's member list downloaded (requires the
        // privileged GUILD_MEMBERS intent to be enabled for the bot application).
        await Context.Guild.DownloadUsersAsync();
        var memberIds = Context.Guild.Users
            .Where(u => u.Roles.Any(r => r.Id == role.Id))
            .Select(u => (long)u.Id)
            .ToList();

        if (memberIds.Count == 0)
        {
            await FollowupAsync($"Nobody currently has the {role.Mention} role.", ephemeral: true);
            return;
        }

        if (memberIds.Count > MaxUsersPerQuery)
        {
            await FollowupAsync(
                $"The {role.Mention} role has {memberIds.Count} members; availability checks support at most {MaxUsersPerQuery} at a time.",
                ephemeral: true);
            return;
        }

        var parsed = await api.ParseDateTimeAsync(new ParseDateTimeRequest(when, (long)Context.User.Id, (long)Context.Guild.Id));
        if (!parsed.Success || parsed.Value is null)
        {
            await FollowupAsync($"❌ {parsed.Error}", ephemeral: true);
            return;
        }

        var start = parsed.Value.Utc;
        var end = start.AddMinutes(duration ?? 60);
        // Plain name, not role.Mention: Discord never renders mention markup inside embed
        // titles, so the mention form would display as raw <@&id> text.
        await RunAndReplyAsync($"@{role.Name}", memberIds, start, end);
    }

    /// <summary>Free/busy for an event's Going members over the event's own window.</summary>
    /// <param name="name">Event title (or fragment), or an autocomplete-picked event id.</param>
    [SlashCommand("event", "Check calendar availability for everyone RSVP'd Going to an event")]
    public async Task EventAsync([Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        var going = ev.Options.FirstOrDefault(o => o.SortOrder == 0);
        var userIds = going is null
            ? new List<long>()
            : ev.Rsvps.Where(r => r.OptionId == going.Id).Select(r => r.UserId).ToList();

        if (userIds.Count == 0)
        {
            await FollowupAsync($"Nobody has RSVP'd Going to **{ev.Title}** yet.", ephemeral: true);
            return;
        }

        if (userIds.Count > MaxUsersPerQuery)
        {
            await FollowupAsync(
                $"**{ev.Title}** has {userIds.Count} people RSVP'd Going; availability checks support at most {MaxUsersPerQuery} at a time.",
                ephemeral: true);
            return;
        }

        var start = ev.StartsAtUtc;
        var end = start.AddMinutes(ev.DurationMinutes ?? 60);
        await RunAndReplyAsync($"**{ev.Title}**", userIds, start, end);
    }

    /// <summary>Runs the availability check and replies with the grid embed.</summary>
    /// <param name="subject">Display name for the checked group.</param>
    /// <param name="userIds">The Discord user ids to check.</param>
    /// <param name="start">Window start (UTC).</param>
    /// <param name="end">Window end (UTC).</param>
    private async Task RunAndReplyAsync(string subject, List<long> userIds, DateTimeOffset start, DateTimeOffset end)
    {
        var availability = await api.CheckAvailabilityAsync(new AvailabilityRequest(userIds, start, end));
        if (!availability.Success || availability.Value is null)
        {
            await FollowupAsync($"❌ {availability.Error}", ephemeral: true);
            return;
        }

        var embed = AvailabilityEmbedBuilder.Build(subject, start, end, availability.Value.Results);
        await FollowupAsync(embed: embed, ephemeral: true);
    }
}
