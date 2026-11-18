# Running without the Server Members intent — functional note and code-generation plan

**Task**: let the bot run when Discord does not grant it the Server Members intent
(`GUILD_MEMBERS`), so that losing the intent switches off the two features that need it instead
of taking the whole bot offline. Asked by Joshua on 2026-11-18 ("Yes, build it"), after Discord's
notice that the hosted app must apply to keep the intent.
**Baseline**: `master` `6e0d8b2` — release 0.37.2 (#200). Written on `4242144`; the branch was
moved onto `6e0d8b2` after the approval, before any code.
**Branch**: `feature/run-without-members-intent` (local, in a worktree of its own) → PR
`feat: keep the bot running without the Server Members intent`.
**Status**: written 2026-11-18 and approved by Joshua the same day, as written, with Q1–Q4 each
answered A ("Approve, all A").

A change inside existing components, so the workflow runs in one document, as on the other changes
of this kind: no user stories, application design, units, NFR or infrastructure stages; one unit,
and this file is both its functional note (sections 1–4) and its code-generation plan (sections
5–7), approved together.

## 1. Why

1. **The review.** Since 2026-06-10 Discord reviews privileged intents by reach: an app whose
   servers together give more than 10,000 unique users access to it must apply to keep them, and
   reapply each year. The hosted app has passed that line, and Discord's notice gives it until
   2027-02-02. An app that does not apply in time, or is turned down, loses the intent; Discord
   says the loss can come at once on a refusal.
2. **What losing it does today.** The bot asks for the intent at login (`Program.cs`). Discord
   refuses a connection that asks for an intent the app does not have (gateway close code 4014),
   so today the loss takes every feature offline — commands, reminders, posts, live lists — not
   only the two that use it. A self-hoster who never switched the intent on gets the same.
3. **What uses the intent.**
   1. `/availability role`, and `/availability slots` when `who` is a role: they download the
      server's member list to find who holds the role (`AvailabilityModule`). Discord offers no
      other way to list a role's holders.
   2. Role snapshots for the website (ADR 0004, `RoleSnapshotService`): the full member list at
      each sync, and member update and leave events between syncs, so that the website can check a
      restricted RSVP option or poll vote and name restricted and granted roles.
4. **What does not use it.** A click or command in Discord carries the member and their roles in
   the interaction itself, so restrictions are checked in Discord as now (`RoleRestrictionCheck`),
   and so are the manager checks. Granting and removing attendee roles and adding members to event
   threads fall back to a REST lookup when a member is not cached (`AttendeeRoleManager`,
   `EventThreadManager`), and so do DMs (`DeliveryPollerService`).

## 2. What this change ships

1. **A setting**, `Discord:ServerMembersIntent` (`DISCORD_SERVER_MEMBERS_INTENT` in both compose
   files): whether the bot asks Discord for the intent (Q1).
2. **A refusal while running** is logged plainly and, where the setting allows, ends in a restart
   that decides again (Q2).
3. **Role lists in `/availability`** answer with a sentence when the bot runs without the intent.
4. **Role snapshots without the intent** keep role names and keep no member rows; the website asks
   members to make a restricted choice from Discord (Q3).
5. **Documents**: README (feature note, setup step, configuration table), ADR 0004 (a refinement
   section), the live-test script, and the release summary (Q4).

With the intent, everything works as today. No migration.

## 3. Rules

### 3.1 Whether the bot asks for the intent (as written here: Q1 answered A)

1. `on`: the bot asks for it, as today. `off`: it does not.
2. `auto`, which is also what an empty or missing setting means: before connecting, the bot reads
   its own application from Discord with its token, and asks for the intent only when the
   application has it — approved, or switched on in the Developer Portal (the flags
   `GATEWAY_GUILD_MEMBERS` and `GATEWAY_GUILD_MEMBERS_LIMITED`).
3. In `auto`, if that read fails (Discord or the network down), the bot asks for the intent, as
   today, and logs a warning.
4. Any other value stops the bot at start, with a sentence that names the setting and its three
   values (the start-up fail-fast posture of milestone 15). Case does not matter.
5. One log line at start says what was decided and why, for example `Server Members intent: off
   (auto: the application does not have it). Role lists in /availability and role checks on the
   website are off.`
6. The decision holds until the bot restarts.

### 3.2 When Discord refuses the intent while the bot runs (as written here: Q2 answered A)

1. A gateway close with code 4014 is logged as critical, with what to do.
2. In `auto`, the bot then stops with a non-zero exit code, so the container restarts (both compose
   files have `restart: unless-stopped`) and the decision is made again from the application.
3. In `on`, it does not stop: the bot goes on as Discord.Net does today, and the log says to set
   `auto` or `off`.
4. In `off` the bot never asks for the intent, so this cannot happen for it.

### 3.3 Role lists in `/availability`, without the intent

1. `/availability role`, and `/availability slots` when `who` is a role, answer at once and only to
   the caller, before anything is downloaded, with a sentence of this kind (final wording at
   generation): `Listing who holds a role needs Discord's Server Members access, which CalCrony
   doesn't have right now. /availability event and /availability slots for yourself or a member
   still work.`
2. The commands stay registered as they are: Discord's command list does not change with the
   setting.
3. `/availability event` and `/availability slots` for yourself or a member are unchanged.

### 3.4 Role snapshots without the intent (as written here: Q3 answered A)

1. The bot never downloads a member list, and pushes nothing on member events.
2. Its syncs run when they run today — at Ready, every ten minutes, after a command that names a
   role, when a watched role is deleted or renamed, on joining a server — and carry the watched
   roles' names and tombstones, no members, and `MembersKnown: false`.
3. The API, on such a sync, stores the role names as today (watched roles only, a deleted role as
   its tombstone), deletes every member row of the server, stores none, and clears the server's
   sync marker (`RolesSyncedAt`).
4. With no marker, a website member's pick of a restricted RSVP option or vote in a restricted
   poll gets today's answer for a snapshot it cannot trust: `We can't confirm your roles right now
   — RSVP from Discord.` (409), or "vote" for a poll. The creator and managers still pass. In
   Discord nothing changes.
5. Restricted and granted roles keep showing by name on the website.
6. A sync without the field — from a bot older than this change — is read as `MembersKnown: true`,
   exactly as today.
7. Role names stored without a marker are still trimmed by the daily retention sweep and dropped
   when the bot leaves the server; today both paths look only at servers with a marker.
8. When the intent is back (a restart in `auto` or `on`), the next full sync stores members and
   sets the marker again, as today.

### 3.5 What does not change

1. Everything with the intent.
2. Restriction checks in Discord, attendee roles, threads, DMs, live lists, native events, name
   snapshots of servers and channels, and every web feature other than restricted choices.
3. The privacy policy: it lists what may be stored, and without the intent less is stored (no
   member rows at all).

## 4. Questions

Each has a recommendation. Answer with a letter after `[Answer]:`.

**Q1. How does the bot decide whether to ask for the intent?**
- A) A setting with `auto`, `on` and `off`, `auto` by default: the bot reads its application's
  flags at start (section 3.1). Prod and the test stack need no change, and a self-hoster who
  never switched the intent on gets a running bot **(recommended)**
- B) A setting with `on` and `off`, `on` by default: after Discord removes the intent, the bot
  stays offline until someone sets `off`
- C) No setting: always `auto`
- D) Other

[Answer]: A

**Q2. What happens when Discord refuses the intent while the bot runs?**
- A) In `auto`, the bot stops so its container restarts and decides again; in `on`, it logs only
  (section 3.2). The outage lasts one restart rather than until someone changes a setting. Known
  limit: if Discord's flags still showed the intent while refusing it, the container would restart
  in a loop, with Docker's growing delay, until set to `off` **(recommended)**
- B) Log only, in every mode
- C) Other

[Answer]: A

**Q3. What do role snapshots do without the intent?**
- A) Keep role names and drop member rows; the website asks members to make a restricted choice
  from Discord (section 3.4). One optional field on one bot-only route, no migration, and no member
  data kept that the bot can no longer keep current **(recommended)**
- B) Send nothing, with no change to the API: the website shows `role #…` instead of names,
  refuses restricted choices once the 30-minute lease runs out, and keeps the member rows already
  stored, frozen, until their restriction ends
- C) Other

[Answer]: A

**Q4. Release, and the release summary?**
- A) `feat:`, so 0.38.0, merged once 0.37.2 has been released and rolled out, with one summary line
  for self-hosters: the bot now starts without the intent, and what is off without it
  **(recommended)**
- B) As A, with no summary line
- C) Merged whenever ready, even into the open release (0.37.2 would become 0.38.0)
- D) Other

[Answer]: A

## 5. What changes, file by file (with Q1–Q4 answered A)

**Bot**
- `ServerMembersIntent.cs` (new): the setting read and checked; the pure decision from the setting,
  the application's flags and a failed read; the 4014 recognizer; the decision as a small object
  the services take.
- `Program.cs`: the decision made before the host is built (in `auto`, one REST read of the
  application, with the token the bot already has), `GatewayIntents` built from it, and the object
  registered.
- `DiscordBotService.cs`: the start-up log line; a `Disconnected` handler for 4014 (critical log,
  and in `auto` the host stopped with exit code 1).
- `Modules/AvailabilityModule.cs`: the sentence of section 3.3 when the intent is off.
- `RoleSnapshotService.cs`: when the intent is off, no member download, no member pushes, and
  names-only syncs.

**Contracts**
- `Roles.cs`: `RoleSyncRequest` gains `MembersKnown`, `true` when absent.

**API**
- `Endpoints/RoleSnapshotEndpoints.cs`: the names-only sync; the retention step reconciles a server
  that holds role rows without a marker.
- `Services/RetentionService.cs`: servers with role rows and no marker join the sweep.
- `Endpoints/GuildPresenceEndpoints.cs`: the presence sync drops a departed server's role rows
  whether or not it has a marker.

**Tests**
- Bot: the setting read (three values, case, empty, refused values); the decision (each setting
  with and without each flag, and a failed read); the 4014 recognizer, also when wrapped; the
  availability sentence; the names-only sync request.
- API: a names-only sync stores names and tombstones, deletes member rows and clears the marker; a
  website pick of a restricted option or poll is then answered 409 and the creator and a manager
  pass; names still reach the event as the website reads it; a body without the field is read as
  today; retention and the presence sync drop names-only rows.

**Documents**
- README: the feature note on the intent, setup step 5, and a row for the setting in the bot's
  configuration table.
- Both compose files: `Discord__ServerMembersIntent: ${DISCORD_SERVER_MEMBERS_INTENT:-auto}`.
- ADR 0004: a section on snapshots without the intent.
- `construction/build-and-test/live-e2e-script.md`: a new section (section 7).
- The release summary file for 0.38.0 (Q4).

## 6. Execution plan

- [ ] 0.1 Baseline: Release build and the whole suite on `4242144`; warnings counted.
- [ ] 1.1 Tests first for the setting read, the decision and the 4014 recognizer; seen failing.
- [ ] 1.2 `ServerMembersIntent.cs`; those tests green.
- [ ] 1.3 `Program.cs` and `DiscordBotService.cs`: the decision at start, the log line, the 4014
  handler.
- [ ] 2.1 Tests first for the availability sentence; seen failing.
- [ ] 2.2 `AvailabilityModule.cs`; green.
- [ ] 3.1 API tests first for the names-only sync, the gate, names on the event, an older body,
  retention and the presence sync; seen failing.
- [ ] 3.2 The contract field, `RoleSnapshotEndpoints.cs`, `RetentionService.cs`,
  `GuildPresenceEndpoints.cs`; green.
- [ ] 3.3 Bot test first for the names-only sync request; `RoleSnapshotService.cs`; green.
- [ ] 4.1 README, both compose files, ADR 0004, the live-test section, the release summary.
- [ ] 5.1 Release build (no new warnings) and the whole suite.
- [ ] 5.2 Mutation checks: each rule of sections 3.1–3.4 taken out in turn fails a test.
- [ ] 5.3 The bot started locally against the test application with `off`, and with `auto`, to see
  the log line and that it connects (no server data touched).
- [ ] 5.4 Both review agents, one on security and one on correctness and quality, read the local
  commits; findings answered and recorded in section 8, and read again until neither has one.
- [ ] 5.5 Completion gate.

## 7. Delivery

1. Nothing is pushed before the completion gate: "Code generation complete. Choose: A) Request
   Changes  B) Continue to Next Stage". B is the go-ahead to take `master` in if it has moved, run
   the suite and the reviewers' read again, push the branch and open the pull request.
2. The merge, the release and the prod rollout each wait for their own go-ahead; by Q4 A the merge
   comes after 0.37.2 has been released and rolled out.
3. Live items on the test stack, before the release (new section of the live script):
   1. With the intent switched off for the test application in the Developer Portal, the test bot
      in `auto` connects without it and says so; commands, reminders and posts work.
   2. **Must pass**: with the intent off, a member without the role is refused a restricted RSVP
      option in Discord, and a member with it is seated. The Discord check reads the member from
      the click itself; this item shows that holds without the intent.
   3. `/availability role` answers with the sentence of section 3.3.
   4. On the website a restricted option answers "RSVP from Discord", and restricted and granted
      roles show by name.
   5. The intent switched on again: after a restart the bot asks for it, and the next sync stores
      members again.
   Switching the test application's intent in the Developer Portal is Joshua's to do.
4. Outside this change: the answers for Discord's review form. They can be drafted at any time
   from the Developer Portal's form, which needs Joshua's sign-in, and are his to submit.

## 8. Notes from generation

### 8.1 Baseline and tests first

- Step 0.1 on `6e0d8b2`: the Release build with the 22 warnings it had before; 2,591 tests
  passed and 17 skipped (Api 1,598, Bot 490, Web 154, ReleaseNotes 349 passed with 17 skipped,
  the 17 being the ones that need bash or a symbolic link on Windows).
- Step 3.1, with only the contract field added: the three API tests for the names-only sync
  failed — the marker was still set, a sync listing members while saying they are unknown was
  stored, and the website member got 403 "limited to @Raider" instead of 409 "RSVP from
  Discord", which is what a names-only sync read as a full one would do. The test of a body
  without the field passed, as it must. With the sync changed, the retention and presence-sync
  tests failed (both passed over a guild with names and no marker), and passed once both paths
  took such a guild in.
- Steps 1.1, 2.1 and 3.3: the bot's tests were written against types and members that did not
  exist yet, so they failed to compile until the code was added.
