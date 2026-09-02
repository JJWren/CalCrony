using CalCrony.Contracts;

namespace CalCrony.Web;

/// <summary>How the web app names Discord roles: the API's name snapshot as "@Name" when it holds
/// one, the raw id as "role #123" when it doesn't (the ADR 0001 posture — never a placeholder,
/// never nothing). One place, so the event page, the poll page, the RSVP buttons and the edit
/// form can't drift apart on wording.</summary>
public static class RoleText
{
    /// <summary>"@Name" or "role #id".</summary>
    /// <param name="id">The Discord role id.</param>
    /// <param name="name">The name snapshot, or null when unknown.</param>
    /// <returns>The display label.</returns>
    public static string Label(long id, string? name) => name is null ? $"role #{id}" : $"@{name}";

    /// <summary>"@Name" or "role #id" for a role reference.</summary>
    /// <param name="role">The role reference.</param>
    /// <returns>The display label.</returns>
    public static string Label(RoleRefDto role) => Label(role.Id, role.Name);

    /// <summary>A comma-joined list of labels, in the order given.</summary>
    /// <param name="roles">The role references.</param>
    /// <returns>"@A, role #2, @C".</returns>
    public static string List(IEnumerable<RoleRefDto> roles) => string.Join(", ", roles.Select(Label));
}
