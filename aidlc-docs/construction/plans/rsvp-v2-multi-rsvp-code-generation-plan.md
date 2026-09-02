# RSVP v2 §3.3 — multiple RSVPs per user: application design + code generation plan

**Issue**: #125 (last box). **Requirements**: `aidlc-docs/inception/requirements/rsvp-v2-multi-rsvp-requirements.md`
(Q1–Q5 answered 2026-09-02, all recommended options). **Baseline**: `master` b75a67b (v0.34.0), 703 tests
(480 API, 113 bot, 110 web). **Branch**: `feature/125-multi-rsvp` → PR `feat: multiple RSVPs per user`.
**Approved** 2026-09-02; hand-off posted as https://github.com/JJWren/CalCrony/issues/125#issuecomment-5511795855 (this file is the copy to tick).
No ADR: nothing crosses the API↔Discord boundary; the one notable trade-off (a non-additive index swap) is
recorded here and in the PR body.

## Decisions (locked)

| # | Question | Decision |
|---|---|---|
| Q1 | Where does the switch live? | **Per event, opt-in, default off** — `AllowMultipleRsvps` on `Event` and as an `EventSeries` template field; `/create multi-rsvp:true`, `/edit multi-rsvp:`, a web checkbox. Existing events unchanged. |
| Q2 | What is "attending" with several seats? | **Unchanged** — the one flagged option decides who is going (threads, availability, DM reminders, live-list count, waitlist). Other seats are extra answers. |
| Q3 | Exclusive options? | **None** — every option is independent; the docs advise designing sets that combine. |
| Q4 | Turning multi off with multi-holders? | **Refuse with 409** naming the count, like "capacity can't go below seated". |
| Q5 | Capacity on non-attending options? | **Unchanged** — only the attending option queues; a full non-attending option refuses. |

Accepted assumptions: the web may set the flag (it hands out nothing, so no strip/carry-over); removal is an
option-scoped DELETE; the unique index becomes `(EventId, UserId, OptionId)`; roles become a per-user **set**
difference; a PUT on the full attending option waitlists the member even when seated elsewhere; `EventTemplate`
carries no RSVP settings today, so the flag does not go there either; polls untouched.

## Data model

| Where | Field | Notes |
|---|---|---|
| `Event` | `AllowMultipleRsvps bool` | Default false |
| `EventSeries` | `AllowMultipleRsvps bool` | Template field beside `WantsThread`; copied at materialization; Series-scope edits write it only when the request carried it |
| `Rsvp` | (no new column) | Unique index `(EventId, UserId)` → unique `(EventId, UserId, OptionId)`; `IX_Rsvps_EventId_Id` stays |
| `CreateEventRequest` | `bool AllowMultipleRsvps = false` | Honored for both caller types (like `WantsThread`) |
| `UpdateEventRequest` | `bool? AllowMultipleRsvps = null` | null = unchanged; false with multi-holders → 409 |
| `EventDto` | `bool AllowMultipleRsvps = false` (positional, after `AllowedRoles`) + helper `RsvpsFor(long userId)` | The helper is what the bot and web use for "what do I hold" |

## Rules

**PutRsvp** (`EventEndpoints.cs:1379`, after the option lookup, `CheckedRoleIds` compare, restriction gate and
closed check — all three already per option and unchanged):

1. `held` = the user's rows on this event. A row on the requested option → the no-op it is today (no queue move).
2. `rolesBefore` = union of the roles of the user's **seated** rows (`AttendeeRoleSync.RolesHeld`); `attendingSeatBefore` = held a seated attending row.
3. Single mode (`!ev.AllowMultipleRsvps`) with an existing row: **today's switch path verbatim** — the row moves
   (`OptionId`, `Waitlisted`, `CreatedAt`); `vacatedAttending` = that row was a seated attending row.
   Otherwise: **add** a row; waitlisted iff the option is the attending one and full; a full non-attending option → 409 `"{label}" is full.` (unchanged text); `vacatedAttending` = false.
4. `rolesAfter` over the user's rows now; `AttendeeRoleSync.Diff(before, after)` → revokes (before−after) then
   grants (after−before) through `EnqueueRoleChangeAsync` (coalescing reused) when `IsRoleActive(ev)`.
5. Thread: add-only when the user did not hold a seated attending row and now does (`EventThreadSync.EnqueueMemberAddAsync`).
6. `vacatedAttending` → `RsvpPolicy.PromoteAsync` (unchanged; it may now promote a member who holds other seats).
7. Embed sync + live-list sync + save + commit, as today.

**DeleteRsvp** — one core `RemoveRsvpsAsync(ev, userId, Guid? optionId)` behind two routes:
`DELETE /events/{id}/rsvps/{userId}` (every row the user holds; single mode's one row is the degenerate case) and
`DELETE /events/{id}/rsvps/{userId}/options/{optionId}` (that row; missing → the no-op OK). Closed → 409 as today.
Same set difference for roles; a removed **seated attending** row promotes; embed + live list sync.

**PromoteAsync** refinement: skip the `GrantAttendeeRole` for a promoted user whose other seated rows already
carry the attending role (`RolesHeld` over their other seats) — a duplicate grant is harmless at the bot but is
noise in the outbox and in `RoleDeliveriesAsync`-style tests.

**Edit path** (`UpdateEvent`): validation block (after `staysLive`, `EventEndpoints.cs:888`): `request.AllowMultipleRsvps == false`
while `ev.AllowMultipleRsvps` and any user holds >1 row → 409
`"{n} members hold more than one RSVP — keep multiple RSVPs on, or ask them to pick one."` Apply:
`ev.AllowMultipleRsvps = flag; if (applyToSeries) series!.AllowMultipleRsvps = flag;` (the `RsvpCloseMinutesBefore`
pattern at ~1050, not the option-template rewrite). `SeatedRoles(ev, live)` returns `Dictionary<long, HashSet<long>>`;
the per-user diff at ~1142–1150 becomes set differences feeding the same one-query batch load. Action log gains
`("multiple RSVPs", request.AllowMultipleRsvps is not null)` in `ActionLog.Changed` (~1190).

**Create** (`EventEndpoints.cs:444` area): `var allowMultiple = request.AllowMultipleRsvps;` for both caller types;
`EventSeries` (~570) and `Event` (~597) initializers set it; `SeriesMaterializer.cs:57` copies `series.AllowMultipleRsvps`.

**Everything else is already multi-safe and must not change**: `RsvpPolicy.SeatedCount / CapacityBelowSeated /
SeatWaitlist / TryApplyOptionEdit` (rows are seats), `DmReminderFanOut`, `DmReminderEndpoints` claim check,
`CalendarEndpoints` availability, `AvailabilityModule`, `LiveListEmbedBuilder`, `EventThreadSync`, `CsvExport`
(one row per RSVP), `RoleRestrictionGate`, `RsvpRequest.CheckedRoleIds`, polls.

## Verified code facts (master b75a67b, 2026-09-02)

- `CalCronyDbContext.cs:160-167`: `Entity<Rsvp>` — `HasIndex(EventId, UserId).IsUnique()` (EF default name
  `IX_Rsvps_EventId_UserId`, created in `20260717163504_AddEventModel`) and `IX_Rsvps_EventId_Id`.
- `EventEndpoints.cs`: routes at 28–29; `SeatedRoles` 125; `StripSpecRoles` 152 / `CarryOverSpecRoles` 166
  (untouched — the flag is not role-bearing); create's caller-type block 440–446; series initializer 548–578,
  event initializer 584–605; `UpdateEvent` locals `isLive` 819, `applyToSeries` 831, `staysLive` 888,
  `rolesBefore = SeatedRoles(ev, isLive)` 894; series-template rule 1021–1048; `SeatWaitlist` call 1134;
  role diff 1142–1150; `ActionLog.Changed` ~1190; `PutRsvp` 1379–1533; `DeleteRsvp` 1545–1607.
- `AttendeeRoleSync.cs`: `AttendeeRoleChange(Revoke, Grant)` record 9–20 and `Decide` 52–59 are the one-seat
  shape to replace with `RolesHeld(options, seatedOptionIds)` + `Diff(before, after)`; `ApplyRoleChangeAsync`
  187–203 revokes before granting — keep that order; `EnqueueRoleChangeAsync` 171 coalesces; the many-roles
  `LoadPendingRoleChangesAsync` 104 is what the edit path batches through.
- `RsvpPolicy.cs`: `AttendingOption` 27, `SeatedCount` 48, `PromoteAsync` (grant per promoted user via
  `EnqueueRoleChange` with `pendingRoles`), `SeatWaitlist`. `EventThreadSync.IsThreadActive` / `EnqueueMemberAddAsync`.
- `SeriesMaterializer.cs:57` (`WantsThread = series.WantsThread`) — the flag copy goes beside it.
- `Mapping.cs:36` builds `RsvpDto` per row ordered by `CreatedAt`; `EventDto` is positional — append the new
  parameter after `AllowedRoles` (`Events.cs:237-261`).
- Contracts: `CreateEventRequest` `Events.cs:52-71` (`WantsThread` at 67), `UpdateEventRequest` 105-123,
  `RsvpDto` 205, `RsvpRequest` 349, `EventDto` helpers 262-323 (`SeatedCount`, `Waitlist`).
- Bot: `CalCronyApiClient.cs:124` `PutRsvpAsync`, `:132` `DeleteRsvpAsync`; `EventModule.cs` `/create`
  params 58–76 (`thread` 72 is the bool precedent), request 157–166, reply notes 195–207; `/edit` params
  299–315, nothing-to-change guard 318–325, request 382–398, reply 405–413. `RsvpComponentModule.cs` (96 lines):
  `alreadyOnOption` 37, delete-or-put 52–54, confirmation switch 71–79, DM offer 84–92. `EventEmbedBuilder.cs`
  60–88 meta lines (🏷️, 🔒, cutoff), 100–146 member lists.
- Web: `CalCronyWebApiClient.cs:50` `PutRsvpAsync`, `:58` `DeleteRsvpAsync`; `RsvpButtons.razor` (154 lines):
  `MyOptionId` 55, click 139–153; `EventForm.razor`: options editor 77–108, thread checkbox 127–133 (create-only —
  the new checkbox is NOT create-only), edit prefill 485–500, update request 655–671, create request 693–705;
  `EventDetail.razor` meta chips 40–62, "Who's in" columns 277–315.
- Docs: `README.md` lines 24 (RSVPs bullet), 57 (`/create` row), 60 (`/edit` row); `Docs.razor` lines 128
  (`/create` row) and 155 (custom RSVPs bullet).
- Tests: `RsvpV1ApiTests` (`CreateAsync` helper 655; concurrency test 636 is the pattern for the parallel PUT
  test), `PerOptionRoleApiTests` (`CreateRaidAsync` 313, `RoleDeliveriesAsync` 346, `MarkServedAsync` 366),
  `AttendeeRoleSyncTests` (pure `Decide` tests to migrate to `Diff`), `RsvpPromotionQueryCountTests` (pinned
  SQL counts — the set logic is in-memory, so the counts must not move), bot `EventEmbedBuilderTests`,
  web `RsvpV1ComponentTests` (bunit 2: `cut.Render(p => ...)`; each file has its own `CapturingHandler`).
- Migration reference for data-moving Up/Down: `20260901114347_PerOptionAttendeeRoles.cs`; execute both
  directions against `postgres:17-alpine` and record it in the PR body.

## Execution plan

Layers run top to bottom; each layer builds and its tests pass before the next starts. Tick boxes in the same
turn a step completes.

**1. Contracts** (`src/CalCrony.Contracts/Events.cs`)
- [x] 1.1 `CreateEventRequest` += `bool AllowMultipleRsvps = false`; `UpdateEventRequest` += `bool? AllowMultipleRsvps = null`. XML docs: opt-in, both caller types, 409 rule on turning off.
- [x] 1.2 `EventDto` += `bool AllowMultipleRsvps = false` (after `AllowedRoles`) and helper `IReadOnlyList<RsvpDto> RsvpsFor(long userId)` (seated and waitlisted rows, display order).

**2. API data model** (`src/CalCrony.Api/Data`)
- [x] 2.1 `Entities.cs`: `Event.AllowMultipleRsvps`, `EventSeries.AllowMultipleRsvps` (doc comments state the template rule).
- [x] 2.2 `CalCronyDbContext.cs`: replace the `(EventId, UserId)` unique index with unique `(EventId, UserId, OptionId)`; update the "one RSVP per user in v1" comment.
- [x] 2.3 Migration `AllowMultipleRsvps`: add both columns (default false); drop `IX_Rsvps_EventId_UserId`; create unique `IX_Rsvps_EventId_UserId_OptionId`. Down: keep one row per `(EventId, UserId)` — the member's seated attending row when they hold one, else the earliest (`ROW_NUMBER() OVER (PARTITION BY "EventId","UserId" ORDER BY <seated attending first>, "CreatedAt","Id")`; refined in review so no attending seat is freed behind a waitlist), enqueue a `RevokeAttendeeRole` per (event, member, role) the discarded seats carried and the kept seat does not, and a `SyncEventMessage` per affected event with a posted message, then delete the rest, restore the old index and drop the columns. Execute Up and Down against `postgres:17-alpine` over seeded multi rows; record row counts before/after in the PR body.

**3. API logic** (`src/CalCrony.Api`)
- [x] 3.1 `AttendeeRoleSync`: `RolesHeld(options, seatedOptionIds) → HashSet<long>` and `Diff(before, after) → (revokes, grants)`; `Decide`/`AttendeeRoleChange` retired (or kept as a thin two-seat wrapper if fewer test edits result — pick one, don't keep both paths live).
- [x] 3.2 `EventEndpoints.SeatedRoles` → user → role set; edit-path diff (~1142–1150) as set differences through the existing batch load.
- [x] 3.3 `PutRsvp` per the rules (no-op per option; single-mode switch verbatim; multi-mode add; set-diff roles; attending-seat thread add; promotion on a vacated attending seat).
- [x] 3.4 `DeleteRsvp` core + option-scoped route `DELETE /events/{id}/rsvps/{userId}/options/{optionId}`; bare route keeps "remove every RSVP the member holds".
- [x] 3.5 `RsvpPolicy.PromoteAsync`: skip the grant when the promoted user's other seats already carry the attending role.
- [x] 3.6 Create: flag on `Event` and `EventSeries` for both caller types; `SeriesMaterializer` copies it.
- [x] 3.7 Edit: 409 on turning off with multi-holders (validated under the event lock before any mutation); apply to event and, Series-scope, to the template; action-log field "multiple RSVPs".
- [x] 3.8 `Mapping`: `EventDto.AllowMultipleRsvps`.

**4. Bot** (`src/CalCrony.Bot`)
- [x] 4.1 `CalCronyApiClient`: `DeleteRsvpOptionAsync(eventId, userId, optionId)`; drop the now-unused bare `DeleteRsvpAsync` if nothing else calls it.
- [x] 4.2 `EventModule`: `/create multi-rsvp:` (bool, default false) → `AllowMultipleRsvps`; reply note `· ☑️ multiple RSVPs allowed`. `/edit multi-rsvp:` (bool?) → `AllowMultipleRsvps`; added to the nothing-to-change guard.
- [x] 4.3 `RsvpReplyText.cs` (new, pure): confirmation lines. Single mode: today's three texts verbatim. Multi mode: `Added {emote} **{label}** to your RSVPs for **{title}** (<t:F>).` + ` You're also marked: {others}.` when other seats are held; `Removed {emote} **{label}** from your RSVPs for **{title}**.` + ` You're still marked: {others}.`; the waitlist text unchanged plus the "also marked" tail.
- [x] 4.4 `RsvpComponentModule`: `alreadyOnOption` → the option-scoped delete (both modes — in single mode the one row is on that option); PUT path unchanged; confirmations via `RsvpReplyText`; DM offer condition unchanged.
- [x] 4.5 `EventEmbedBuilder`: `☑️ Pick every option that applies — click a choice again to remove it` after the 🔒 lines, before the cutoff line, when `AllowMultipleRsvps`.

**5. Web** (`src/CalCrony.Web`)
- [x] 5.1 `CalCronyWebApiClient`: `DeleteRsvpOptionAsync`; drop the bare `DeleteRsvpAsync` if unused.
- [x] 5.2 `RsvpButtons.razor`: `MyOptionIds` from `Event.RsvpsFor(UserId)`; several `selected`; click held → option-scoped delete; helper line in multi mode `☑️ Pick every option that applies — click a selected one to remove it.`
- [x] 5.3 `EventForm.razor`: checkbox "☑️ Allow more than one RSVP per member" under the options editor, create **and** edit; edit prefill from the event; sent on update only when changed; form-text mentions the refusal while anyone holds more than one. The API's 409 lands in the existing error line.
- [x] 5.4 `EventDetail.razor`: meta chip `☑️ multiple RSVPs` beside the 🔒 chips.
- [x] 5.5 `Docs.razor` 128 and 155: `multi-rsvp:true` / the web checkbox, toggle semantics, the turn-off rule.

**6. Docs and bookkeeping**
- [x] 6.1 `README.md` 24 / 57 / 60: the switch, `multi-rsvp`, "clicking a chosen option again removes it", turn-off rule.
- [x] 6.2 `CalCronyDbContext` comment and any "switching revokes the previous choice" wording (README 36, Docs 58/65 describe single-mode role swaps — still true in single mode; qualify with "in single-RSVP mode" only where the text would otherwise mislead).
- [ ] 6.3 `aidlc-state.md` marks §3.3 shipped on merge; `audit.md` entries throughout.

**7. Tests** (each layer's tests pass before the next layer starts)
- [x] 7.1 API `AttendeeRoleSyncTests`: `RolesHeld` / `Diff` matrix — same role via two seats nets to nothing, dropping one of two different roles revokes only that one, empty↔set.
- [x] 7.2 API `MultiRsvpApiTests` (new): single-mode behaviour unchanged (switch, re-click no-op, bare delete); multi add leaves other seats alone; option-scoped delete; bare delete clears all; capacity counts seats (one member takes a seat on two capped options); attending waitlist while seated elsewhere; promotion when the attending seat is removed via the option route and NOT when a non-attending seat is; Tank+Healer both granting `@raider` → one grant, no revoke on dropping one; Tank(@tank)+Healer(@healer) → two grants, dropping one revokes only its role; promotion skips an already-held role; edit-path diff with multi-holders (role moved between options, option dropped); turning off with multi-holders → 409 with the count, turning off with none → 200; turning on never fails; series template carries the flag to the next occurrence; Series-scope edit writes the template only when the request carried the flag; Occurrence-scope leaves it; restriction gate still per option (entry to a second option is gated, re-click is not); DM fan-out sends one DM per member; `/calendar/availability` for the event lists a member with two seats once; CSV export emits one row per seat; action log names "multiple RSVPs"; concurrent PUTs by one member to two options both land (no unique violation), concurrent PUTs to the same option yield one row.
- [x] 7.3 `RsvpPromotionQueryCountTests` still pin the same counts.
- [x] 7.4 Bot: `RsvpReplyTextTests` (six texts), `EventEmbedBuilderTests` (☑️ line present/absent; a member seated on two options appears in both columns and is counted once per column; the waitlist column still lists them once).
- [x] 7.5 Web (bUnit): several selected buttons; click held sends the option-scoped delete; helper line; create form sends the flag; edit form sends it only when changed; detail chip.
- [x] 7.6 Migration Up/Down against `postgres:17-alpine` (Down keeps each member's seated attending row, else their earliest; enqueues the revokes and embed re-renders the discarded seats owe — verified: one revoke, one re-render over the seeded rows).
- [x] 7.7 Full-solution `dotnet test` green before the PR opens.

**8. Delivery**
- [x] 8.1 First commit on the branch: `docs: RSVP v2 §3.3 design — requirements and code generation plan` (this file, the requirements doc, `aidlc-state.md`, `audit.md`).
- [x] 8.2 PR `feat: multiple RSVPs per user` with the #150-style body: what it does, shape of the change, behaviour changes for existing servers (none — opt-in; the index swap is invisible to single-mode events), rollback note (rolling the image back is safe only while no member holds more than one row — 0.34.0's PutRsvp would move a member's first row onto an option they already hold and hit the widened index; once multi rows exist, run Down first, which collapses seats, revokes the roles they carried and re-renders the embeds), migration verification, test counts. Conventional title `feat:` (never `feat!:`).
- [ ] 8.3 Copilot review loop to zero comments (re-query PR state each turn; `env -u GITHUB_TOKEN` for gh writes; merge via the REST call); squash-merge; release-please release; upgrade test (`:main`) then prod (pg_dump to `backups/` first, bump `CALCRONY_IMAGE_TAG` in a clean shell); verify `/health` and `__EFMigrationsHistory`; tick §3.3 on #125 and close it (all three boxes done); mark shipped in `aidlc-state.md`.
