using Discord;

namespace CalCrony.Bot;

/// <summary>Who may change an event from Discord: its creator, or a member who holds Manage
/// Server. The API applies the same rule to web callers and takes the bot's word for callers in
/// Discord. <c>/notify</c> asks here; the commands that edit and delete events, series, polls
/// and templates each hold the same rule in their own module.</summary>
public static class EventAccess
{
    /// <summary>Whether a member may change an event.</summary>
    /// <param name="user">The member who invoked the command.</param>
    /// <param name="creatorId">The event's creator.</param>
    /// <returns>True for the creator and for a holder of Manage Server.</returns>
    public static bool CanManage(IUser user, long creatorId) =>
        (long)user.Id == creatorId || (user is IGuildUser member && member.GuildPermissions.ManageGuild);
}
