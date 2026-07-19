using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Auth;

/// <summary>
/// Guild-scoped authorization for JWT web callers, backed by the login-time
/// UserGuildMembership snapshot intersected with guilds CalCrony actually knows
/// (the bot-present set). Bot callers bypass these checks entirely at the call sites.
/// </summary>
public sealed class GuildAccessService(CalCronyDbContext db, IClock clock)
{
    /// <summary>Snapshots older than this are refused — bounds how long a removed member
    /// keeps access. Re-syncing is a near-instant prompt=none login bounce.</summary>
    public static readonly Duration MaxSnapshotAge = Duration.FromDays(7);

    /// <summary>Resolves a web user's standing in a guild from their membership snapshot (bot-present guilds only).</summary>
    public async Task<GuildAccess> CheckAsync(long userId, long guildId, CancellationToken cancellationToken)
    {
        var membership = await db.UserGuildMemberships
            .FirstOrDefaultAsync(m => m.UserId == userId && m.GuildId == guildId, cancellationToken);
        if (membership is null)
        {
            return GuildAccess.None;
        }

        if (membership.SnapshotAt + MaxSnapshotAge < clock.GetCurrentInstant())
        {
            return GuildAccess.Stale;
        }

        if (!await db.Guilds.AnyAsync(g => g.Id == guildId, cancellationToken))
        {
            return GuildAccess.None;
        }

        return membership.CanManage ? GuildAccess.Manager : GuildAccess.Member;
    }

    /// <summary>403 for callers outside the guild.</summary>
    public static IResult Forbidden() =>
        Results.Json(new ErrorResponse("You don't have access to this server."), statusCode: StatusCodes.Status403Forbidden);

    /// <summary>403 telling the caller their guild snapshot is stale — re-login refreshes it.</summary>
    public static IResult StaleSnapshot() =>
        Results.Json(
            new ErrorResponse("Your server list is out of date — sign in again to re-sync your servers."),
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>403 for acting on another user's data where only self-service is allowed.</summary>
    public static IResult SelfOnly() =>
        Results.Json(new ErrorResponse("You can only do that for your own account."), statusCode: StatusCodes.Status403Forbidden);
}

/// <summary>A web caller's standing in a guild, from None through Manager; Stale means the snapshot aged out.</summary>
public enum GuildAccess
{
    None = 0,
    Stale = 1,
    Member = 2,
    Manager = 3,
}
