using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/availability — free/busy grids for a role's members or an event's attending list,
/// and free slots for a member or a role over one or more days.</summary>
/// <param name="api">The CalCrony API client.</param>
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[RequireBotInGuild]
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

    /// <summary>Free/busy for an event's attending members (whatever that option is labelled)
    /// over the event's own window.</summary>
    /// <param name="name">Event title (or fragment), or an autocomplete-picked event id.</param>
    [SlashCommand("event", "Check calendar availability for everyone attending an event")]
    public async Task EventAsync([Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        // Seated attending RSVPs only — waitlisted users aren't coming (yet). The attending
        // option carries a custom label ("Raider", "In"…), so replies name it rather than "Going".
        var attending = ev.AttendingOption;
        var attendingLabel = attending?.Label ?? "Going";
        var userIds = attending is not null
            ? ev.Rsvps.Where(r => r.OptionId == attending.Id && !r.Waitlisted).Select(r => r.UserId).ToList()
            : new List<long>();

        if (userIds.Count == 0)
        {
            await FollowupAsync($"Nobody has RSVP'd **{attendingLabel}** to **{ev.Title}** yet.", ephemeral: true);
            return;
        }

        if (userIds.Count > MaxUsersPerQuery)
        {
            await FollowupAsync(
                $"**{ev.Title}** has {userIds.Count} people RSVP'd **{attendingLabel}**; availability checks support at most {MaxUsersPerQuery} at a time.",
                ephemeral: true);
            return;
        }

        var start = ev.StartsAtUtc;
        var end = start.AddMinutes(ev.DurationMinutes ?? 60);
        await RunAndReplyAsync($"**{ev.Title}**", userIds, start, end);
    }

    /// <summary>Free slots for a member, or for everyone holding a role, over one or more whole
    /// days: the times every counted member is inside their available hours with nothing on a
    /// linked calendar. Unlike <c>role</c>, which checks one window someone already has in mind,
    /// this finds the windows.</summary>
    /// <param name="who">The member, or the role whose members are looked at.</param>
    /// <param name="when">The first day in natural language; today when omitted.</param>
    /// <param name="days">How many days to list.</param>
    /// <param name="duration">The shortest slot worth listing, in minutes.</param>
    [SlashCommand("slots", "Find free time slots for a member or role over one or more days")]
    public async Task SlotsAsync(
        [Summary("who", "A member or a role")] IMentionable who,
        [Summary("when", "First day, e.g. \"today\", \"saturday\", \"nov 12\" (default: today)")] string? when = null,
        [Summary("days", "How many days to list, 1-14 (default 7)"), MinValue(1), MaxValue(FreeSlotLimits.MaxDays)] int? days = null,
        [Summary("duration", "Shortest slot worth listing, in minutes (default 60)"), MinValue(FreeSlotLimits.MinMinimumMinutes), MaxValue(FreeSlotLimits.MaxMinimumMinutes)] int? duration = null)
    {
        await DeferAsync(ephemeral: true);

        FreeSlotTarget target;
        switch (who)
        {
            case IRole role:
                // Enumerating role members needs the member list downloaded, as in RoleAsync.
                await Context.Guild.DownloadUsersAsync();
                target = FreeSlotTarget.ForRole(
                    role.Name,
                    role.Mention,
                    Context.Guild.Users
                        .Where(u => u.Roles.Any(r => r.Id == role.Id))
                        .Select(u => ((long)u.Id, u.IsBot)));
                break;

            case IUser user:
                // Discord hands over a guild member when the user is in this server, and a bare
                // user when the id was resolved from outside it.
                target = FreeSlotTarget.ForUser(
                    (long)user.Id,
                    (user as IGuildUser)?.DisplayName ?? user.Username,
                    user.IsBot,
                    user is IGuildUser);
                break;

            default:
                target = FreeSlotTarget.Refused("Pick a member or a role.");
                break;
        }

        if (target.Refusal is { } refusal)
        {
            await FollowupAsync(refusal, ephemeral: true);
            return;
        }

        var minimumMinutes = duration ?? FreeSlotLimits.DefaultMinimumMinutes;
        var slots = await api.GetFreeSlotsAsync(new FreeSlotsRequest(
            target.UserIds,
            (long)Context.User.Id,
            (long)Context.Guild.Id,
            when,
            days ?? FreeSlotLimits.DefaultDays,
            minimumMinutes));
        if (!slots.Success || slots.Value is null)
        {
            await FollowupAsync($"❌ {slots.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            embed: FreeSlotsEmbedBuilder.Build(target.Subject, minimumMinutes, slots.Value), ephemeral: true);
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
