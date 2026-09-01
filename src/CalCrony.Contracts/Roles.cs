namespace CalCrony.Contracts;

/// <summary>A Discord role reference with the API's name snapshot, when one is held. Names come
/// from the bot's role snapshots (ADR 0004) and are null when the role is not one CalCrony
/// watches or no longer exists — consumers fall back to the id.</summary>
/// <param name="Id">The Discord role id.</param>
/// <param name="Name">The role's last-known name, or null when unknown.</param>
public record RoleRefDto(long Id, string? Name);

/// <summary>The roles one guild's live signup restrictions name — what the bot snapshots.</summary>
/// <param name="GuildId">The Discord guild id.</param>
/// <param name="RoleIds">The watched role ids.</param>
public record GuildWatchedRolesDto(long GuildId, IReadOnlyList<long> RoleIds);

/// <summary>Every guild with a live signup restriction and the roles it names (bot-present
/// guilds only) — the bot's Ready-time reconcile input.</summary>
/// <param name="Guilds">The per-guild watched role sets.</param>
public record WatchedRolesResponse(IReadOnlyList<GuildWatchedRolesDto> Guilds);

/// <summary>One watched role as the bot resolved it. A null name means the bot checked and the
/// role no longer exists in Discord — the API records that as a deleted role, which makes the
/// restriction naming it vacuous rather than unsatisfiable.</summary>
/// <param name="RoleId">The Discord role id.</param>
/// <param name="Name">The role's current Discord name, or null when it no longer exists.</param>
public record RoleNameDto(long RoleId, string? Name);

/// <summary>One member holding at least one watched role, and which of them.</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="RoleIds">The watched roles the member holds.</param>
public record MemberRolesDto(long UserId, IReadOnlyList<long> RoleIds);

/// <summary>A full role snapshot for one guild: every watched role the bot was asked about and
/// every member holding at least one of them. Replaces the guild's stored snapshot wholesale
/// and stamps the guild as synced.</summary>
/// <param name="Roles">The watched roles, resolved.</param>
/// <param name="Members">The members holding any of them; members holding none are omitted.</param>
public record RoleSyncRequest(IReadOnlyList<RoleNameDto> Roles, IReadOnlyList<MemberRolesDto> Members);

/// <summary>One member's current watched roles, pushed by the bot on a member update. An empty
/// list removes the member's row.</summary>
/// <param name="RoleIds">The watched roles the member now holds.</param>
public record PutMemberRolesRequest(IReadOnlyList<long> RoleIds);
