# CalCrony — E2E Testing Guide (Milestone 15, gates v1.0.0)

The full manual pass over every feature area against a **real deployment** and a **real Discord
server**. Part A sets up an isolated test environment; Part B is the numbered pass itself; the
results land in [`build-and-test-summary.md`](build-and-test-summary.md) (a `fix:` PR per
finding). v1.0.0 ships only when the pass is declared complete. Section 12 is conditional on
deployment prerequisites and does not block the release.

Conventions: "embed" = the event's Discord message; "~15s" = one delivery-poller cycle;
web = the Blazor app signed in via Discord.

---

## Part A — Test environment setup

E2E runs in a **test environment that shares nothing with production**: its own Discord
application (bot identity), its own Discord server, its own database volume, its own secrets.
The prod and test stacks differ only in their `.env` files.

### A.1 Two Discord applications

Create (or reuse) a second application in the [Discord Developer Portal](https://discord.com/developers/applications)
— e.g. *CalCrony* (prod) and *CalCrony Test*. For the test application:

- [ ] Bot token generated → `DISCORD_BOT_TOKEN` for the test `.env` only.
- [ ] **Server Members Intent** enabled (Bot settings).
- [ ] For web-login testing: OAuth2 redirect `{Api__PublicBaseUrl}/auth/discord/callback` added,
      client id/secret → `DISCORD_OAUTH_CLIENT_ID/SECRET` in the test `.env`.
- [ ] Note the **Application ID** — it is both the invite `client_id` and the `DISCORD_APP_ID`
      below.

### A.2 Per-environment .env files

Keep one `.env` per environment (e.g. `.env.prod`, `.env.test` — compose reads them via
`--env-file`). The test file mirrors prod's variables with test values, plus:

```bash
# .env.test — differs from prod in every identity-bearing value
DISCORD_BOT_TOKEN=<test application's bot token>
DISCORD_APP_ID=<test application's id>        # web invite links advertise the TEST bot
DISCORD_OAUTH_CLIENT_ID=<test app id>         # web login against the test application
DISCORD_OAUTH_CLIENT_SECRET=<test app secret>
CALCRONY_API_KEY=<distinct from prod>
CALCRONY_JWT_SIGNING_KEY=<distinct from prod>
CALCRONY_DB_NAME=calcrony_test                # separate database (or a separate host)
```

`DISCORD_APP_ID` is what keeps the test web app from advertising the production invite — it
rides the same runtime-config injection as `API_BASE_URL`, so no rebuild is needed. Unset =
production id (correct for the prod stack, which needs no new variables).

### A.3 Standing up the stacks

- **Test** (from source or images): `docker compose --env-file .env.test up -d --build`
  (or `-f docker-compose.prod.yml` with the test env-file to test the released images —
  preferred for the v1.0.0 gate).
- **Prod**: `docker compose -f docker-compose.prod.yml --env-file .env.prod up -d`.
- [ ] If both stacks share one host, give the test stack its own ports and a distinct compose
      project name (`-p calcrony-test`) so volumes/networks don't collide.

### A.4 Test server

- [ ] A dedicated Discord server for the pass (never a live community): invite the TEST bot via
      the test application's URL —
      `https://discord.com/oauth2/authorize?client_id=<TEST-app-id>&permissions=335275969536&scope=bot+applications.commands`
      — or the test web app's landing button, which now advertises the same thing.
- [ ] A second account (or a willing friend) for multi-user RSVP/capacity/anonymous-poll checks.
- [ ] A role the bot can manage (below its top role) and one channel where Create Public Threads
      is denied to the bot — sections 6/7/13 use them.

---

## Part B — The pass

### 0. Deployment prerequisites & first steps

- [ ] `docker compose -f docker-compose.prod.yml up -d` with real `CALCRONY_API_KEY`,
      `CALCRONY_JWT_SIGNING_KEY`, `DISCORD_BOT_TOKEN` → all four containers healthy
      (`docker compose ps` shows api healthy; bot/web started after it).
- [ ] `GET /health` → `{status: ok, version}` with the release version; `GET /health/ready` → 200.
- [ ] **Fail-fast check**: stop the stack, boot once with `CALCRONY_JWT_SIGNING_KEY=short` →
      api exits with the "at least 32 characters" message (not a running-but-broken API). Restore.
- [ ] Retention: api boot logs show no retention errors (a "Retention purge removed N rows" line
      appears only when something was purged — absence is fine on a fresh DB).
- [ ] Invite the bot with the README URL (`permissions=335275969536`). Bot comes online.
- [ ] First steps as documented: `/settings server-timezone` (autocomplete offers zones with UTC
      offsets), `/settings default-channel` → `/settings view` reflects both.

### 1. Events CRUD & natural-language parsing

- [ ] `/create title:Kickoff when:"tomorrow 6pm"` → embed with 🗓️ local time, RSVP buttons;
      ephemeral confirmation.
- [ ] `/create` with `when:"in 5 hours"`, `when:"friday at 8"` → correct times in `/settings` zone.
- [ ] Timezone abbreviation: `when:"10:00 AM CST"` on a server whose zone is NOT Central →
      parses as Central wall time, not server zone.
- [ ] Validation: `/create` with a 129+ char title → friendly error (not an Internal Server Error);
      negative `duration` → friendly error.
- [ ] `/edit` (autocomplete picker offers events; "Test" vs "TestTitle" exact-match priority) →
      embed re-renders in place.
- [ ] `/delete` → embed gone, confirmation notes nothing else.

### 2. RSVPs & capacity

- [ ] Click ✅ Going → name appears on the embed within the interaction (bot edits inline).
- [ ] Click 🤔 Maybe → moves columns; click 🤔 again → un-RSVPs (toggle).
- [ ] Web: RSVP from the event page → embed updates in Discord within ~15s.
- [ ] Capacity: set a capacity on an option (bot/create flow default options have none — verify the
      full 409 copy via a second account clicking a full option if capacity is configured; otherwise
      mark N/A).

### 3. Reminders & notifications

- [ ] `/remind when:"in 2 minutes" about:"stand up"` → ping arrives in-channel at time.
- [ ] `/notify` a test event `minutes-before:1` (event ~3 min out) → ping fires at T−1.
- [ ] Add 5 notifications → 6th rejected with the max-5 message.
- [ ] Start ping posts at event start; event auto-ends after its duration.
- [ ] On a series occurrence, adding a notification asks for scope; `series` scope → next spawned
      occurrence carries it; `occurrence` scope → next spawn does not.

### 4. Recurrence — full verb set

- [ ] `/create repeat:weekly` → 🔁 line on embed; `/series info` shows the schedule.
- [ ] `/series skip` → occurrence cancelled (embed deleted), replacement posts immediately.
- [ ] `/series stop` → live occurrence survives; no next spawn after it ends.
- [ ] `/series edit` on the stopped series (any change, or none) → series revives, next occurrence
      appears after the current one completes.
- [ ] Deleting a live occurrence with `/delete` → series stops (confirmation says so).
- [ ] Rule edit that leaves no future occurrence (until-date in the past) → friendly 400 suggesting stop.
- [ ] Monthly nth-weekday: create "3rd Friday" style; `/series info` describes it correctly.

### 5. Templates

- [ ] Build an event with a description, 2 notifications, and a weekly rule → `/template save`.
- [ ] `/template list` shows it with 🔁/🔔 markers.
- [ ] `/create template:<pick> when:"next monday 7pm"` → content, reminders, and rule all carried;
      explicit `title:` overrides the template's.
- [ ] `repeat:no repeat` suppresses the template's rule (one-off created).
- [ ] Duplicate name on save → friendly "already exists" (case-insensitive).
- [ ] `/template edit` changes title/rule (creator-or-manager guard bails for plain members);
      web Templates tab Edit prefills, replaces the reminder list, and clears the rule.
- [ ] Web: Templates tab lists it; Use → prefilled form; save-as-template from an event page works.

### 6. Attendee roles

- [ ] Create a role `Attendee` below the bot's top role. `/create attendee-role:@Attendee` →
      🏷️ line on embed and reply.
- [ ] RSVP Going → role granted within ~15s. Switch to Maybe → revoked. Going again → granted.
- [ ] Un-RSVP entirely from Going → revoked.
- [ ] `/edit attendee-role:@Other` with existing Going RSVPs → old role removed, new granted.
- [ ] `/edit clear-attendee-role:true` → role removed from attendees, line drops from embed.
- [ ] Event ends (or `/delete`) → all grants revoked.
- [ ] Prechecks: `attendee-role:@everyone` → friendly bail; a role ABOVE the bot → friendly bail;
      an integration-managed role → friendly bail.
- [ ] Web event page shows the 🏷️ chip; web edit can clear the role but not set one.

### 7. Event threads

- [ ] `/create thread:true` → public thread on the embed, named after the event.
- [ ] RSVP Going → added to the thread within ~15s (thread appears in your channel list).
- [ ] Switch to Maybe → still a thread member (add-only by design).
- [ ] Event ends → thread archived. Repeat once for `/delete` and once for `/series skip`
      (the replacement occurrence opens its OWN thread).
- [ ] Precheck: deny Create Public Threads in one channel → `/create thread:true` there bails friendly.
- [ ] Web: create with the thread checkbox → thread appears; event page shows the 🧵 chip.

### 8. Native-event mirroring

- [ ] `/settings native-events on` (bot needs Manage Events — precheck bails if missing).
- [ ] New event → appears in the server's Events tab; description carries the RSVP pointer line.
- [ ] `/edit` the title → native event updates.
- [ ] Event starts → native event goes Active; ends → Completed.
- [ ] `/delete` a scheduled mirrored event → native twin deleted.
- [ ] Toggle `native-events off` → existing mirrored events keep syncing; new ones don't mirror.

### 9. Polls & time polls

- [ ] `/poll create` with 6+ options → select-menu voting; ≤5 options → buttons.
- [ ] ➕ voter-added option via the modal → components rebuild (button→select swap at 6).
- [ ] Anonymous poll: voters' names hidden on the embed; your own votes highlighted on the web.
- [ ] `closes:"in 3 minutes"` → auto-closes on time, embed renders closed.
- [ ] `/poll time` with 3 slots; vote; `/poll close`; `/poll convert` → event posts in the POLL's
      channel at the winning slot.

### 10. ICS feed & RRULE

- [ ] `/link` → subscribe URL; add to Google Calendar (or another real calendar app).
- [ ] One-off events appear; a weekly series shows FUTURE occurrences (RRULE), not just the next.
      **Pass criterion is the raw feed URL** (curl/browser) — Google Calendar re-fetches URL
      subscriptions only every 12–24h, so its display lag is expected, not a finding.
- [ ] A past occurrence renders as history; the live occurrence appears exactly once (no duplicate
      with the projection).

### 11. Web parity pass

- [ ] Discord login (identify + guilds only); guild list matches; re-sync link refreshes.
- [ ] Create / edit / delete an event from the browser → embed posts / updates / disappears in
      Discord (default channel).
- [ ] Web create without a default channel set → actionable error message.
- [ ] Notifications, polls (create/vote/close/convert), series (skip/stop/schedule edit with scope
      ask), templates, settings (timezone picker with UTC offsets) — one smoke action each.
- [ ] Docs page renders; invite link on the landing page carries `permissions=335275969536`.

### 12. Availability & Google OAuth — CONDITIONAL

Requires: public HTTPS `Api__PublicBaseUrl`, Google OAuth client (Testing status), redirect URI
registered, Server Members intent. **Skippable without blocking v1.0.0** (long-standing known
constraint) — mark the section skipped in the summary if prerequisites aren't in place.

- [ ] `/calendar connect` (works in DM) → Google consent (free/busy scope only) → linked.
- [ ] `/availability event` on an event with Going RSVPs → grid renders; linked users show busy blocks.
- [ ] `/availability role` → grid for role holders.
- [ ] Web: availability grid on the event page; My settings shows the connection; disconnect works.
- [ ] After 7 days (Google Testing mode) the 🔄 reconnect badge appears — verify the copy if observed.

### 13. Permission-precheck bails

- [ ] Remove Manage Events from the bot → `/settings native-events on` bails friendly.
- [ ] Remove Manage Roles → `/create attendee-role:` bails friendly.
- [ ] Deny Create Public Threads in a channel → `/create thread:true` bails friendly.
- [ ] Restore all permissions; each feature works again with no residue.

### 14. Restart resilience

- [ ] Queue a notification ~2 min out; `docker compose kill bot` before it fires; restart the bot →
      the ping arrives exactly once (ack-only-on-success).
- [ ] `docker compose kill api` mid-use → container restarts on its own (`restart: unless-stopped`);
      bot resumes polling; web recovers.
- [ ] `docker compose restart api` → boot logs clean; `/health/ready` 200 again; a Retention line
      appears at startup when anything qualified for purge.

---

## Part C — Recording results & the v1.0.0 gate

1. Tick items as they pass; for each failure, file the symptom in the results table in
   [`build-and-test-summary.md`](build-and-test-summary.md) — one `fix:` PR per finding, then
   re-run the affected section against the fixed build.
2. Section 12 may be marked **skipped** if the Google/HTTPS prerequisites aren't in place; every
   other section must pass.
3. When the table is fully green, the pass is complete: the final `docs: prepare v1.0.0` PR
   records the results, and its **squash commit footer** carries `Release-As: 1.0.0` (footer of
   the commit message in the merge dialog — never the PR description).
