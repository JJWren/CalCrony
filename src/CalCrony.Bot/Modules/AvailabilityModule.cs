using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

[RequireContext(ContextType.Guild)]
[Group("availability", "Check group calendar availability")]
public class AvailabilityModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaxUsersPerQuery = 50;

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
        await RunAndReplyAsync(role.Mention, memberIds, start, end);
    }

    [SlashCommand("event", "Check calendar availability for everyone RSVP'd Going to an event")]
    public async Task EventAsync([Summary("name", "Event title (or part of it)")] string name)
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
