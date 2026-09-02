using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>The signup-restriction check the RSVP and poll routes share. Enforcement is split by
/// caller (ADR 0004): the bot checks the member's roles live from its socket cache BEFORE it
/// calls the API and is trusted here; a web caller is answered from the guild's role snapshot
/// and fails closed — an unverifiable snapshot refuses with "RSVP from Discord" rather than
/// admitting. The creator and server managers always pass.</summary>
internal static class RoleRestrictionGate
{
    /// <summary>Applies one restriction to the current caller.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source (the sync marker's lease is measured against it).</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="creatorId">The event or poll creator, who bypasses.</param>
    /// <param name="allowedRoleIds">The restriction (empty = unrestricted, always passes).</param>
    /// <param name="subject">What the denial names, e.g. "This option" or "This poll".</param>
    /// <param name="discordVerb">What to do from Discord when unverifiable, e.g. "RSVP" or "vote".</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Null when the caller may proceed; the 403 (denied) or 409 (unverifiable) response otherwise.</returns>
    internal static async Task<IResult?> CheckAsync(
        HttpContext context,
        GuildAccessService access,
        CalCronyDbContext db,
        IClock clock,
        long guildId,
        long creatorId,
        long[] allowedRoleIds,
        string subject,
        string discordVerb,
        CancellationToken cancellationToken)
    {
        if (context.User.IsBot() || allowedRoleIds.Length == 0)
        {
            return null;
        }

        if (context.User.WebUserId() is not { } userId)
        {
            return GuildAccessService.Forbidden();
        }

        // Bypass is decided before the snapshot is read: a creator or manager passes even when
        // the guild has never been synced.
        if (userId == creatorId
            || await access.CheckAsync(userId, guildId, cancellationToken) == GuildAccess.Manager)
        {
            return null;
        }

        // One guild-scoped lookup: the sync marker, THIS restriction's roles (with their
        // tombstone state — the rest of the guild's watched set is irrelevant here), and this
        // member's held set.
        var snapshot = await db.Guilds
            .Where(g => g.Id == guildId)
            .Select(g => new
            {
                g.RolesSyncedAt,
                Roles = db.GuildRoles
                    .Where(r => r.GuildId == guildId && allowedRoleIds.Contains(r.RoleId))
                    .Select(r => new { r.RoleId, r.Name })
                    .ToList(),
                Held = db.GuildMemberRoles
                    .Where(m => m.GuildId == guildId && m.UserId == userId)
                    .Select(m => m.RoleIds)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        IReadOnlyDictionary<long, string?> checkedRoles =
            snapshot?.Roles.ToDictionary(r => r.RoleId, r => r.Name) ?? new Dictionary<long, string?>();
        var result = RoleRestriction.Evaluate(
            allowedRoleIds,
            RoleRestriction.IsSnapshotFresh(snapshot?.RolesSyncedAt, clock.GetCurrentInstant()),
            checkedRoles, snapshot?.Held ?? [], bypass: false);

        // Both refusals carry the same code: the route's other 403s and 409s (self-only, closed,
        // a vote race) are not role refusals, and a client hiding participation on a role refusal
        // must not hide it on those.
        return result.Verdict switch
        {
            RoleRestrictionVerdict.Allowed => null,
            RoleRestrictionVerdict.Denied => Results.Json(
                new ErrorResponse(
                    $"{subject} is limited to {RoleRestriction.DescribeRoles(result.EffectiveRoleIds, checkedRoles)}.",
                    ErrorCodes.RoleRestricted),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Conflict(new ErrorResponse(
                $"We can't confirm your roles right now — {discordVerb} from Discord.",
                ErrorCodes.RoleRestricted)),
        };
    }
}
