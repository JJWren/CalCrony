using CalCrony.Bot;

namespace CalCrony.Bot.Tests;

public class AttendeeRoleSpecTests
{
    [Fact]
    public void Assignable_role_passes()
    {
        Assert.Null(AttendeeRoleSpec.Validate(
            "Attendee", botHasManageRoles: true, botTopRolePosition: 10,
            rolePosition: 5, roleIsEveryone: false, roleIsManaged: false));
    }

    [Fact]
    public void Everyone_is_rejected_first()
    {
        var message = AttendeeRoleSpec.Validate(
            "@everyone", botHasManageRoles: false, botTopRolePosition: 0,
            rolePosition: 0, roleIsEveryone: true, roleIsManaged: false);
        Assert.Contains("@everyone", message);
    }

    [Fact]
    public void Managed_roles_are_rejected()
    {
        var message = AttendeeRoleSpec.Validate(
            "Some Bot", botHasManageRoles: true, botTopRolePosition: 10,
            rolePosition: 5, roleIsEveryone: false, roleIsManaged: true);
        Assert.Contains("integration", message);
    }

    [Fact]
    public void Missing_manage_roles_is_rejected()
    {
        var message = AttendeeRoleSpec.Validate(
            "Attendee", botHasManageRoles: false, botTopRolePosition: 10,
            rolePosition: 5, roleIsEveryone: false, roleIsManaged: false);
        Assert.Contains("Manage Roles", message);
    }

    [Fact]
    public void Role_at_or_above_the_bot_is_rejected()
    {
        var atBot = AttendeeRoleSpec.Validate(
            "High Role", botHasManageRoles: true, botTopRolePosition: 5,
            rolePosition: 5, roleIsEveryone: false, roleIsManaged: false);
        Assert.Contains("highest role", atBot);

        var aboveBot = AttendeeRoleSpec.Validate(
            "Higher Role", botHasManageRoles: true, botTopRolePosition: 5,
            rolePosition: 6, roleIsEveryone: false, roleIsManaged: false);
        Assert.Contains("highest role", aboveBot);
    }
}
