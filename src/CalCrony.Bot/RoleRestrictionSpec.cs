using System.Text.RegularExpressions;

namespace CalCrony.Bot;

/// <summary>Pure parsing and checks for signup restrictions (RSVP v2 §3.5): the <c>restrict-to</c>
/// mention list, the pre-checks a named role must pass, and the LIVE check the bot runs against
/// a member's roles before it calls the API. Static and guild-less like <see cref="AttendeeRoleSpec"/>
/// so it tests directly; the modules feed it values off the interaction context.</summary>
public static partial class RoleRestrictionSpec
{
    /// <summary>Cap on the roles one restriction may name — the API's rule of record
    /// (RsvpPolicy.MaxAllowedRoles), repeated here so the friendly bail happens before the call.</summary>
    public const int MaxRoles = 5;

    /// <summary>A Discord role mention, which is what a typed <c>@Role</c> becomes inside a string
    /// command option. Snowflakes are unbounded here — existence is checked against the guild.</summary>
    [GeneratedRegex(@"<@&(\d{1,20})>")]
    private static partial Regex Mention();

    /// <summary>Parses a mention list such as <c>&lt;@&amp;1&gt; &lt;@&amp;2&gt;</c> (spaces or
    /// commas between). Anything that is not a mention or a separator is an error — a typed name
    /// that never became a mention would otherwise silently restrict to nobody.</summary>
    /// <param name="input">The raw <c>restrict-to</c> value.</param>
    /// <param name="roleIds">The distinct role ids on success, in the order given.</param>
    /// <param name="error">The user-facing problem on failure.</param>
    /// <returns>True when the input parsed.</returns>
    public static bool TryParseMentions(string input, out List<long> roleIds, out string? error)
    {
        roleIds = [];
        error = null;

        var parsed = new List<long>();
        var leftover = Mention().Replace(input, match =>
        {
            if (long.TryParse(
                    match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                parsed.Add(id);
            }

            return " ";
        });

        var junk = leftover.Trim().Trim(',').Trim();
        if (junk.Any(c => !char.IsWhiteSpace(c) && c != ','))
        {
            error = $"Give roles as mentions, e.g. `@Raiders @Officers` — couldn't read \"{junk}\".";
            return false;
        }

        if (parsed.Count == 0)
        {
            error = "Mention at least one role, e.g. `@Raiders`.";
            return false;
        }

        roleIds = [.. parsed.Distinct()];
        if (roleIds.Count > MaxRoles)
        {
            error = $"A signup restriction can name at most {MaxRoles} roles.";
            roleIds = [];
            return false;
        }

        return true;
    }

    /// <summary>Pre-checks one named role: it must exist in the server and not be @everyone.
    /// There is deliberately no hierarchy or Manage Roles check — the bot never grants a
    /// restriction role, it only reads who holds it.</summary>
    /// <param name="roleId">The role id, for the message.</param>
    /// <param name="exists">Whether the guild has the role.</param>
    /// <param name="isEveryone">Whether it is the guild's @everyone role.</param>
    /// <returns>Null when the role may be used, else the friendly refusal message.</returns>
    public static string? Validate(long roleId, bool exists, bool isEveryone)
    {
        if (!exists)
        {
            return $"❌ <@&{roleId}> isn't a role in this server — pick one that exists here.";
        }

        if (isEveryone)
        {
            return "❌ @everyone can't be a signup restriction — that's everyone already.";
        }

        return null;
    }

    /// <summary>The live check: may this member take a restricted option? Mirrors the API's rule
    /// for web callers minus the unverifiable state, which can't arise here — the socket cache IS
    /// Discord's answer. Roles the guild no longer has drop out (a deleted role can't be held, so
    /// it stops gating), a restriction whose roles are all gone is vacuous, the creator and
    /// managers bypass, and otherwise holding any remaining role is enough.</summary>
    /// <param name="allowedRoleIds">The option's or poll's restriction.</param>
    /// <param name="roleExists">Whether the guild still has a role.</param>
    /// <param name="memberRoleIds">The roles the member holds right now.</param>
    /// <param name="bypass">True for the creator and server managers.</param>
    /// <param name="effective">The allowed roles still in force — what a denial names.</param>
    /// <returns>True when the member may proceed.</returns>
    public static bool IsAllowed(
        IReadOnlyList<long> allowedRoleIds,
        Func<long, bool> roleExists,
        IReadOnlySet<long> memberRoleIds,
        bool bypass,
        out IReadOnlyList<long> effective)
    {
        effective = [.. allowedRoleIds.Where(roleExists).Distinct()];
        if (allowedRoleIds.Count == 0 || bypass || effective.Count == 0)
        {
            return true;
        }

        return effective.Any(memberRoleIds.Contains);
    }

    /// <summary>Renders role ids as Discord mentions: <c>&lt;@&amp;1&gt;, &lt;@&amp;2&gt;</c>.
    /// Discord shows the current name and "@deleted-role" for a gone one, so no snapshot is needed.</summary>
    /// <param name="roleIds">The roles to mention.</param>
    /// <returns>The comma-joined mentions.</returns>
    public static string Mentions(IEnumerable<long> roleIds) => string.Join(", ", roleIds.Select(id => $"<@&{id}>"));
}
