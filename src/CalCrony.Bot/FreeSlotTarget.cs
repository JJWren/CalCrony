using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>Who a free-slots lookup is about: the members to ask the API for and the plain name
/// for the reply's title — or the reason the target is refused. Pure, so the rules can be tested
/// without Discord: the module only hands over what Discord resolved.</summary>
/// <param name="UserIds">The members to look at; empty when refused.</param>
/// <param name="Subject">The target's plain name for the reply's title; empty when refused.</param>
/// <param name="Refusal">The reply to give instead of looking anything up, or null to go ahead.</param>
public sealed record FreeSlotTarget(IReadOnlyList<long> UserIds, string Subject, string? Refusal)
{
    /// <summary>A role: everyone holding it except bots, which keep no calendar and would only
    /// pad the reply's "not counted" list.</summary>
    /// <param name="roleName">The role's name.</param>
    /// <param name="roleMention">The role's mention markup, for the refusal text.</param>
    /// <param name="holders">Everyone holding the role, with whether each is a bot.</param>
    /// <returns>The target, or the refusal when nobody holds the role, only bots do, or too many
    /// members do.</returns>
    public static FreeSlotTarget ForRole(string roleName, string roleMention, IEnumerable<(long UserId, bool IsBot)> holders)
    {
        var everyHolder = holders.ToList();
        var userIds = everyHolder.Where(holder => !holder.IsBot).Select(holder => holder.UserId).Distinct().ToList();
        if (userIds.Count == 0)
        {
            return Refused(everyHolder.Count == 0
                ? $"Nobody currently has the {roleMention} role."
                : $"Only bots have the {roleMention} role, and bots don't keep a calendar.");
        }

        if (userIds.Count > FreeSlotLimits.MaxUsers)
        {
            return Refused(
                $"The {roleMention} role has {userIds.Count} members; free-slot lists support at most {FreeSlotLimits.MaxUsers} at a time.");
        }

        // Plain name, not the mention: Discord never renders mention markup inside embed titles.
        return new FreeSlotTarget(userIds, WithAtSign(roleName), null);
    }

    /// <summary>One user, who has to be the caller. Naming a member lists that one person's free
    /// time, so it is theirs to ask for: anyone else is reached through a role, as on the grid.
    /// Another member can be named once members can save their own available hours and that
    /// member has. The picker still offers everyone, so the other refusals stay specific: a user
    /// option can carry anyone Discord can resolve by id, in this server or not.</summary>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="displayName">The name to show for them.</param>
    /// <param name="isBot">Whether the user is a bot.</param>
    /// <param name="isMemberOfThisServer">Whether Discord resolved the user as a member of this server.</param>
    /// <param name="callerId">The Discord user id of the member running the command.</param>
    /// <returns>The target, or the refusal for a bot, a user from outside the server, or a member
    /// other than the caller.</returns>
    public static FreeSlotTarget ForUser(long userId, string displayName, bool isBot, bool isMemberOfThisServer, long callerId)
    {
        if (isBot)
        {
            return Refused("Bots don't keep a calendar — pick yourself or a role.");
        }

        if (!isMemberOfThisServer)
        {
            return Refused("That user isn't a member of this server.");
        }

        if (userId != callerId)
        {
            return Refused("Another member's free slots can't be listed yet — pick yourself or a role.");
        }

        return new FreeSlotTarget([userId], WithAtSign(displayName), null);
    }

    /// <summary>A target that is turned down with the given reply.</summary>
    /// <param name="reason">The reply to give.</param>
    /// <returns>The refused target.</returns>
    public static FreeSlotTarget Refused(string reason) => new([], "", reason);

    /// <summary>A name as the title shows it: behind an at sign, unless it already starts with one
    /// — the everyone role's name does, and a display name may.</summary>
    /// <param name="name">The role's or member's name.</param>
    /// <returns>The name, with an at sign put in front when it has none.</returns>
    private static string WithAtSign(string name) => name.StartsWith('@') ? name : $"@{name}";
}
