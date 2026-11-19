using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot;

/// <summary>Applies attendee-role grant/revoke deliveries to Discord. Strictly best-effort: every
/// bail condition (guild/role/user gone, missing Manage Roles, role at/above the bot) is
/// non-transient, so each method catches and logs instead of throwing and the delivery always
/// acks — retrying could otherwise reorder a grant past a later revoke.</summary>
/// <param name="client">The Discord socket client.</param>
/// <param name="logger">The host logger.</param>
/// <param name="membersIntent">Whether the bot has the Server Members intent, without which the
/// member cache is not kept current.</param>
public class AttendeeRoleManager(DiscordSocketClient client, ILogger<AttendeeRoleManager> logger, ServerMembersIntent membersIntent)
{
    /// <summary>Gives the user the role, skipping when the member cache shows they already have it
    /// (with the Server Members intent; without it the cache is not current and the grant is sent).</summary>
    /// <param name="guildId">The Discord guild id.</param>
    /// <param name="roleId">The Discord role id.</param>
    /// <param name="userId">The Discord user id.</param>
    public Task TryGrantAsync(long guildId, long roleId, long userId) =>
        TryApplyAsync(guildId, roleId, userId, grant: true);

    /// <summary>Removes the role from the user, skipping when the member cache shows they don't have it
    /// (with the Server Members intent; without it the cache is not current and the removal is sent).</summary>
    /// <param name="guildId">The Discord guild id.</param>
    /// <param name="roleId">The Discord role id.</param>
    /// <param name="userId">The Discord user id.</param>
    public Task TryRevokeAsync(long guildId, long roleId, long userId) =>
        TryApplyAsync(guildId, roleId, userId, grant: false);

    private async Task TryApplyAsync(long guildId, long roleId, long userId, bool grant)
    {
        try
        {
            if (client.GetGuild((ulong)guildId) is not SocketGuild guild)
            {
                return;
            }

            var role = guild.GetRole((ulong)roleId);
            if (role is null)
            {
                return; // Role deleted Discord-side; the stale id on the event is harmless.
            }

            if (!guild.CurrentUser.GuildPermissions.ManageRoles || role.Position >= guild.CurrentUser.Hierarchy)
            {
                // Permissions changed since the role was picked — non-transient, don't retry.
                logger.LogWarning(
                    "Cannot {Action} role {RoleId} in guild {GuildId}: missing Manage Roles or role at/above the bot.",
                    grant ? "grant" : "revoke", roleId, guildId);
                return;
            }

            // Socket cache first; REST fallback covers members outside the cache. Without the
            // Server Members intent no member update reaches the cache — not even for a role this
            // bot granted — so a cached member's roles can be stale, and a revoke would wrongly
            // read "already gone": every change then goes through REST.
            SocketGuildUser? user = membersIntent.Enabled ? guild.GetUser((ulong)userId) : null;
            if (user is null)
            {
                var restUser = await client.Rest.GetGuildUserAsync((ulong)guildId, (ulong)userId);
                if (restUser is null)
                {
                    return; // User left the guild; nothing to do.
                }

                // Idempotency can't be checked via the socket cache — apply through REST directly.
                if (grant)
                {
                    await restUser.AddRoleAsync((ulong)roleId);
                }
                else
                {
                    await restUser.RemoveRoleAsync((ulong)roleId);
                }

                return;
            }

            var hasRole = user.Roles.Any(r => r.Id == (ulong)roleId);
            if (grant == hasRole)
            {
                return; // Already in the target state; save the rate limit.
            }

            if (grant)
            {
                await user.AddRoleAsync(role);
            }
            else
            {
                await user.RemoveRoleAsync(role);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Best-effort attendee-role {Action} failed for user {UserId} / role {RoleId} in guild {GuildId}.",
                grant ? "grant" : "revoke", userId, roleId, guildId);
        }
    }
}
