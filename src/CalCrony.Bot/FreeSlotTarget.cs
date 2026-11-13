using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>Who a free-slots lookup is about: the members to ask the API for and the plain name
/// for the reply's title — or the reason the target is refused. Pure, so the rules can be tested
/// without Discord: the module only hands over what Discord resolved.</summary>
/// <param name="UserIds">The members to look at; empty when refused.</param>
/// <param name="Subject">The target's plain name for the reply's title; empty when refused.</param>
/// <param name="Refusal">The reply to give instead of looking anything up, or null to go ahead.</param>
/// <param name="ByName">True when one member was named rather than a role. The API then lists a
/// member other than the caller only if they have saved their own available hours.</param>
/// <param name="NamesSomeoneElse">True when the member named is not the caller. Their list is
/// shown only when the answer says it was checked by name (<see cref="RefusalFor"/>).</param>
public sealed record FreeSlotTarget(
    IReadOnlyList<long> UserIds, string Subject, string? Refusal, bool ByName = false, bool NamesSomeoneElse = false)
{
    /// <summary>What a caller is told when a list about someone else comes back without the API
    /// having checked it by name — an API that does not know the rule, which must not be asked
    /// around it.</summary>
    public const string NotCheckedByName = "That member's free slots can't be listed by name right now.";

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

    /// <summary>One member, by name. Naming a member lists that one person's free time, so the
    /// caller can always name themself, and anyone else only once that member has saved their own
    /// available hours — saving them is how a member makes their slots available by name. That
    /// rule is the API's: it holds the hours, and decides before any calendar is read. What is
    /// checked here is what only Discord knows: a user option can carry anyone Discord can
    /// resolve by id, a bot or someone from outside this server included.</summary>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="displayName">The name to show for them.</param>
    /// <param name="isBot">Whether the user is a bot.</param>
    /// <param name="isMemberOfThisServer">Whether Discord resolved the user as a member of this server.</param>
    /// <param name="callerId">The Discord user id of the member running the command.</param>
    /// <returns>The target, marked as by name, or the refusal for a bot or a user from outside
    /// the server.</returns>
    public static FreeSlotTarget ForUser(long userId, string displayName, bool isBot, bool isMemberOfThisServer, long callerId)
    {
        if (isBot)
        {
            return Refused("Bots don't keep a calendar — pick a member or a role.");
        }

        if (!isMemberOfThisServer)
        {
            return Refused("That user isn't a member of this server.");
        }

        return new FreeSlotTarget([userId], WithAtSign(displayName), null, ByName: true, NamesSomeoneElse: userId != callerId);
    }

    /// <summary>The check on the answer, before it is shown. A list about a member other than the
    /// caller is shown only when the API says it answered by name, which is when it applied the
    /// saved-hours rule. The rule then does not rest on which of the API and the bot was updated
    /// first, nor on this module having asked by name.</summary>
    /// <param name="response">The API's answer.</param>
    /// <returns>The reply to give instead of the list, or null to show it.</returns>
    public string? RefusalFor(FreeSlotsResponse response) =>
        NamesSomeoneElse && !response.ByName ? NotCheckedByName : null;

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
