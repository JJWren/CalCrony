# Code generation plan — RSVP v2 §3.5 role-restricted signup (issue #125)

Branch `feature/125-role-restricted-signup` → PR titled `feat: role-restricted signup for RSVPs and polls`.
Design: `docs/adr/0004-role-restrictions-via-bot-role-snapshots.md`. Decisions: per-option storage with an
event-level convenience; snapshot only watched roles; bot checks live, web checks the snapshot and fails
closed; events **and** polls (poll-level gate); seats survive role loss; manager/creator bypass; deleted
roles vacuous; configuration is bot-only, the web strips and carries over.

Steps run top to bottom; each layer builds and its tests pass before the next starts.

## 1. Contracts (`src/CalCrony.Contracts`)
- [x] 1.1 `Roles.cs` (new): `RoleRefDto(long Id, string? Name)`; `WatchedRolesResponse(IReadOnlyList<GuildWatchedRolesDto>)`, `GuildWatchedRolesDto(long GuildId, IReadOnlyList<long> RoleIds)`; `RoleSyncRequest(IReadOnlyList<RoleNameDto> Roles, IReadOnlyList<MemberRolesDto> Members)`, `RoleNameDto(long RoleId, string Name)`, `MemberRolesDto(long UserId, IReadOnlyList<long> RoleIds)`; `PutMemberRolesRequest(IReadOnlyList<long> RoleIds)`.
- [x] 1.2 `Events.cs`: `RsvpOptionSpec` += `IReadOnlyList<long>? AllowedRoleIds`; `RsvpOptionDto` += `IReadOnlyList<RoleRefDto> AllowedRoles` and `string? AttendeeRoleName` (the §3.6 chip retrofit); `CreateEventRequest` += `AllowedRoleIds` (event-level convenience); `UpdateEventRequest` += `AllowedRoleIds` + `bool ClearAllowedRoles`; `EventDto` += `IReadOnlyList<RoleRefDto>? AllowedRoles` (common set when every option agrees, null when they differ). XML docs state the "not both" rule and bot-only configuration.
- [x] 1.3 `Polls.cs`: `CreatePollRequest` += `AllowedRoleIds`; `PollDto` += `IReadOnlyList<RoleRefDto> AllowedRoles`. (Polls have no PATCH — restriction is fixed at creation.)

## 2. API data model (`src/CalCrony.Api/Data`)
- [x] 2.1 `Entities.cs`: `RsvpOption.AllowedRoleIds long[]` (empty = unrestricted), `Poll.AllowedRoleIds long[]`, `Guild.RolesSyncedAt Instant?`; new `GuildRole { GuildId, RoleId, Name, SnapshotAt }` and `GuildMemberRole { GuildId, UserId, RoleIds long[], SnapshotAt }`.
- [x] 2.2 `CalCronyDbContext.cs`: DbSets; composite PKs `(GuildId, RoleId)` / `(GuildId, UserId)`; `Name` max 100; array columns via Npgsql; the series `RsvpOptionsJson` template round-trips `AllowedRoleIds` (extend the spec serializer §3.6 used).
- [x] 2.3 Migration `AddRoleRestrictions`: additive only — new columns default to empty arrays, no backfill. Execute Up and Down against `postgres:17-alpine` over seeded rows; record the result in the PR body as §3.6 did.

## 3. API logic (`src/CalCrony.Api`)
- [x] 3.1 `Rsvp/RoleRestriction.cs` (new, pure): `Evaluate(allowedRoleIds, rolesSyncedAt, knownRoles, memberRoleIds, bypass)` → `Allowed | Denied | Unverifiable`. Rules: empty allowed set → Allowed; bypass → Allowed; allowed roles absent from `GuildRoles` after a sync are dropped (deleted → vacuous); all dropped → Allowed; no `RolesSyncedAt`, or any remaining role unknown → Unverifiable; else Allowed iff the intersection is non-empty.
- [x] 3.2 `Endpoints/RoleSnapshotEndpoints.cs` (new, all BotOnly, mirroring `ChannelEndpoints`): `GET /guilds/roles/watched` (distinct role ids from unended events' options, running series templates, open polls — bot-present guilds only); `PUT /guilds/{guildId}/roles/sync` (replace `GuildRoles` + `GuildMemberRoles` for the guild, stamp `RolesSyncedAt`; members with empty lists are not stored); `PUT /guilds/{guildId}/members/{userId}/roles` (upsert; delete when empty; keeps only roles present in `GuildRoles`). Register in `Program.cs`.
- [x] 3.3 `EventEndpoints.cs` create: validate event-level `AllowedRoleIds` against spec-level (the same "not both" error `AttendeeLimit` gives); event-level writes every option; web callers stripped (`StripSpecRoles` gains `AllowedRoleIds`; the request-level field is ignored for web exactly like `AttendeeRoleId`); series templates store per-option sets.
- [x] 3.4 `EventEndpoints.cs` edit: `ClearAllowedRoles` accepted from any caller; per-option via specs, with `CarryOverSpecRoles` extended to carry `AllowedRoleIds` by label for web callers; `TryApplyOptionEdit` keeps a restriction exactly when an option keeps its RSVPs; Series-scope edits touch the template only when the request touched restrictions (the §3.6 rule).
- [x] 3.5 `EventEndpoints.PutRsvp`: after the option lookup, before the closed check. Bot caller trusted. Web caller evaluated through `RoleRestriction` using one guild-scoped query over `GuildMemberRoles` + `GuildRoles`. `Unverifiable` → 409 "We can't confirm your roles right now — RSVP from Discord." `Denied` → 403 "This option is limited to {role names}." Bypass = creator or `access.CanManage`.
- [x] 3.6 `PollEndpoints.cs`: `CreatePoll` accepts `AllowedRoleIds` (bot only; web stripped); `PutVotes` and `AddOption` enforce identically to 3.5 (bypass = creator/manager).
- [x] 3.7 DTO mapping: `ToDto` resolves `AllowedRoles` names and `AttendeeRoleName` from `GuildRoles` in one guild-scoped lookup; `EventDto.AllowedRoles` derived per 1.2; poll mapping likewise. Names nullable — ADR 0001 posture, id fallback on the consumer side.
- [x] 3.8 `Services/RetentionService.cs`: purge `GuildRoles`/`GuildMemberRoles` for guilds with no live restriction; drop them on the bot-absent presence path as well.
- [x] 3.9 Action log: `EventEdited` detail mentions restriction changes where it already lists role/limit changes.

## 4. Bot (`src/CalCrony.Bot`)
- [x] 4.1 `Api/CalCronyApiClient.cs`: `GetWatchedRolesAsync`, `SyncGuildRolesAsync`, `PutMemberRolesAsync`.
- [x] 4.2 `RoleSnapshotService.cs` (new): `SyncGuildAsync(guild, watchedRoleIds)` — `DownloadUsersAsync` first (the member cache is lazy; `AlwaysDownloadUsers` is off), resolve role names, collect members holding any watched role, PUT sync. `ReconcileAllAsync()` at Ready from `GET /guilds/roles/watched`. `OnMemberUpdatedAsync(before, after)` pushes only when the role delta touches a watched role (watched set cached from the last sync). Best-effort, never throws — the `AttendeeRoleManager` posture.
- [x] 4.3 `DiscordBotService.cs`: `client.GuildMemberUpdated += ...`; Ready reconcile after the channel reconcile.
- [x] 4.4 `RoleRestrictionSpec.cs` (new, pure): parse `restrict-to:` (one or more role mentions) and per-option role restrictions inside `rsvp-options` with a grammar that does not collide with §3.6's grant mention; prechecks: role exists, not `@everyone`. No hierarchy check — the bot never grants these roles.
- [x] 4.5 `Modules/EventModule.cs`: `/create restrict-to:` and `/edit restrict-to:` + `clear-restriction:`; after any successful create/edit naming roles, `RoleSnapshotService.SyncGuildAsync` for that guild so the watched set is authoritative before the embed can be clicked. Reply lines list the restriction.
- [x] 4.6 `Modules/PollModule.cs`: `/poll create restrict-to:` (standard and time polls); same post-create sync.
- [x] 4.7 `Modules/RsvpComponentModule.cs` and `PollComponentModule.cs`: live check from `SocketGuildUser.Roles` before the API call; ephemeral "This option is limited to @Tank." on denial; creator/manager bypass matching the API.
- [x] 4.8 `EventEmbedBuilder.cs` / poll embed: 🔒 line per restricted option ("🔒 Tank — @Tank only") or one event-level line when all options share a set. Buttons stay enabled — disabled-with-explanation on click, never hidden.
- [x] 4.9 `AttendeeRoleManager` / `SeriesModule` untouched — restrictions never participate in grants.

## 5. Web (`src/CalCrony.Web`)
- [x] 5.1 `EventDetail.razor`: restriction chips from `AllowedRoles` names ("🔒 limited to @Tank"); retrofit the §3.6 chip to `AttendeeRoleName` with id fallback.
- [x] 5.2 `RsvpButtons.razor`: no client-side prediction (the web has no role knowledge) — surface the API's 403/409 text inline next to the buttons, unchanged.
- [x] 5.3 `EventForm.razor`: edit mode shows a "Remove signup restriction" checkbox (`ClearAllowedRoles`) when any option is restricted, mirroring the attendee-role clear; no way to set one, with helper text saying restrictions are set in Discord.
- [x] 5.4 `PollDetail.razor` / `PollVotePanel.razor`: restriction chip; 403/409 surfaced inline; add-option form hidden after a refused vote.
- [x] 5.5 `EventCard` / `GuildPolls` rows: 🔒 badge when restricted.

## 6. Docs and policy
- [x] 6.1 `PRIVACY_POLICY.md`: role-membership snapshots — what is stored, why (only roles named by a live restriction), retention (dropped when the restriction ends, the role is deleted, or the bot leaves).
- [x] 6.2 `README.md` + web `Docs` page: `restrict-to:` syntax, per-option grammar, the "RSVP from Discord" web behaviour, manager/creator bypass, seats survive role loss.
- [ ] 6.3 `aidlc-state.md` marks §3.5 shipped on merge; `audit.md` entries throughout.

## 7. Tests (each layer's tests pass before the next layer starts)
- [x] 7.1 API `RoleRestrictionTests` (pure): the evaluation matrix — deleted role vacuous, all-deleted allowed, unknown role unverifiable, no sync unverifiable, bypass, empty set allowed.
- [x] 7.2 API `RoleRestrictedRsvpApiTests`: web allowed / 403 / 409; bot trusted; creator and manager bypass; event-level convenience writes every option; "not both" 400; mirror-back common vs null; `ClearAllowedRoles` from web; `CarryOverSpecRoles` preserves restrictions a web edit cannot see; strip on web create; series template carries restrictions to spawned occurrences; seat survives role loss (snapshot changes, existing RSVP untouched).
- [x] 7.3 API `RoleRestrictedPollApiTests`: `PutVotes` + `AddOption` enforcement; poll-level only; web create stripped.
- [x] 7.4 API `RoleSnapshotEndpointTests`: watched list from events/series/polls (closed/ended and bot-absent guilds excluded); sync replaces rows, drops empty members, stamps `RolesSyncedAt`, unknown roles vacuous; member upsert/delete; retention purge.
- [x] 7.5 Bot: `RoleRestrictionSpecTests` (parse + prechecks), `RoleSnapshotServiceTests` (delta detection, sync payload shape), embed-line tests.
- [x] 7.6 Web (bUnit): chips render names with id fallback; 403/409 text surfaced; clear checkbox present only when restricted; poll add-option hidden after refusal.
- [x] 7.7 Migration Up/Down against `postgres:17-alpine`.
- [x] 7.8 Full-solution `dotnet test` green before the PR opens.

## 8. Delivery
- [x] 8.1 Ship #148 (the `GuildHeading` sidebar change) first as its own PR (`feat: name the server above the guild section tabs`) so this PR stays single-purpose.
- [x] 8.2 Open the PR with the §3.6-style body: what it does, shape of the change, behaviour changes for existing servers (none expected — restrictions are opt-in), migration verification, test counts.
- [ ] 8.3 Copilot review loop to zero comments; squash-merge; release; upgrade test then prod (pg_dump first); update the #125 checklist and `aidlc-state.md`.
