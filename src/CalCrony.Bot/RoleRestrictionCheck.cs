using CalCrony.Contracts;
using Discord;
using Discord.WebSocket;

namespace CalCrony.Bot;

/// <summary>The guild-aware wrappers around <see cref="RoleRestrictionSpec"/>: pre-checks for the
/// roles a command names, and the live check a button click runs before calling the API. Kept
/// out of the modules so /create, /edit, /poll, and both component modules apply one rule.</summary>
public static class RoleRestrictionCheck
{
    /// <summary>Pre-checks every named role against the guild.</summary>
    /// <param name="guild">The guild the command runs in.</param>
    /// <param name="roleIds">The roles a restriction names.</param>
    /// <returns>The first problem, or null when every role may be used.</returns>
    public static string? Validate(SocketGuild guild, IEnumerable<long> roleIds)
    {
        foreach (var roleId in roleIds.Distinct())
        {
            var role = guild.GetRole((ulong)roleId);
            if (RoleRestrictionSpec.Validate(roleId, role is not null, roleId == (long)guild.Id) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Pre-checks the restrictions inside a parsed option set (the <c>only:</c> entries).</summary>
    /// <param name="guild">The guild the command runs in.</param>
    /// <param name="specs">The parsed option specs.</param>
    /// <returns>The first problem, or null.</returns>
    public static string? ValidateSpecs(SocketGuild guild, IEnumerable<RsvpOptionSpec> specs) =>
        Validate(guild, specs.SelectMany(s => s.AllowedRoleIds ?? []));

    /// <summary>The live check: whether the clicking member is refused by a restriction. The
    /// creator and ManageGuild holders bypass, matching the API's rule for web callers.</summary>
    /// <param name="user">The interaction's user.</param>
    /// <param name="creatorId">The event or poll creator.</param>
    /// <param name="allowedRoles">The restriction, as the DTO carries it.</param>
    /// <param name="effective">The roles still in force — what the denial names.</param>
    /// <returns>True when the member must be refused.</returns>
    public static bool Denied(
        IUser user, long creatorId, IReadOnlyList<RoleRefDto>? allowedRoles, out IReadOnlyList<long> effective)
    {
        effective = [];
        if (allowedRoles is not { Count: > 0 } || user is not SocketGuildUser member)
        {
            return false;
        }

        var bypass = (long)member.Id == creatorId || member.GuildPermissions.ManageGuild;
        var held = member.Roles.Select(r => (long)r.Id).ToHashSet();
        return !RoleRestrictionSpec.IsAllowed(
            [.. allowedRoles.Select(r => r.Id)],
            id => member.Guild.GetRole((ulong)id) is not null,
            held, bypass, out effective);
    }

    /// <summary>The ephemeral refusal: "🔒 **Tank** is limited to @Raiders."</summary>
    /// <param name="subject">What is limited, e.g. the option label or "This poll".</param>
    /// <param name="effective">The roles in force.</param>
    /// <returns>The message text.</returns>
    public static string Refusal(string subject, IReadOnlyList<long> effective) =>
        $"🔒 {subject} is limited to {RoleRestrictionSpec.Mentions(effective)}.";
}
