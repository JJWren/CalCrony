namespace CalCrony.Bot;

/// <summary>Pure validation for picking an attendee role: can the bot actually assign it? Called
/// from /create and /edit with values off the interaction context, so the user gets a friendly
/// bail instead of silent grant failures later.</summary>
public static class AttendeeRoleSpec
{
    /// <summary>Validates that the bot can assign the role.</summary>
    /// <param name="roleName">The role's display name, used in the messages.</param>
    /// <param name="botHasManageRoles">Whether the bot holds Manage Roles.</param>
    /// <param name="botTopRolePosition">The bot's highest role position (its hierarchy).</param>
    /// <param name="rolePosition">The picked role's position.</param>
    /// <param name="roleIsEveryone">Whether the picked role is @everyone.</param>
    /// <param name="roleIsManaged">Whether the role is integration-managed (bot/booster roles).</param>
    /// <returns>Null when the role is assignable, else the friendly refusal message.</returns>
    public static string? Validate(
        string roleName,
        bool botHasManageRoles,
        int botTopRolePosition,
        int rolePosition,
        bool roleIsEveryone,
        bool roleIsManaged)
    {
        if (roleIsEveryone)
        {
            return "@everyone can't be an attendee role — pick a specific role.";
        }

        if (roleIsManaged)
        {
            return $"**{roleName}** is managed by an integration and can't be assigned manually.";
        }

        if (!botHasManageRoles)
        {
            return "I need the **Manage Roles** permission to grant attendee roles.";
        }

        if (rolePosition >= botTopRolePosition)
        {
            return $"**{roleName}** is at or above my highest role — move it below mine (or move mine up) and try again.";
        }

        return null;
    }
}
