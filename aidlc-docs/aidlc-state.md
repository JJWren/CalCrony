# CalCrony — AIDLC State

**Project type**: Greenfield
**Workflow**: Inception executed via Claude Code plan mode (2026-07-17); Construction in progress.

## Inception (complete)
- [x] Workspace detection — greenfield, new repo `CalCrony`
- [x] Requirements analysis — standard depth (see `inception/requirements/requirements.md`)
- [x] Workflow planning — approved plan at Claude plan file; milestones 0–4 defined
- [ ] User stories — skipped (personal project, single stakeholder, requirements captured directly)
- [ ] Application design — captured inside approved plan (architecture, data model, API surface)

## Construction (in progress)
- [x] Milestone 0: Repo bootstrap (private GitHub repo JJWren/CalCrony + local repo)
- [x] Milestone 1: Foundation — solution scaffold, EF Core + Postgres, API-key auth middleware, health endpoint, docker-compose, InitialCreate migration, smoke tests green
- [x] Milestone 2: Events + RSVPs — event/RSVP/settings endpoints, NL datetime parser (Recognizers + NodaTime), bot /create /list /edit /delete /settings /timestamp, RSVP buttons with live embed updates; 15 tests green (incl. Testcontainers Postgres integration)
- [x] Milestone 3: Reminders & notifications — DeliveryScheduler sweep (notifications due, Scheduled→Started ping, Started→Ended), Delivery outbox + pending/ack endpoints, /reminders endpoint, bot DeliveryPollerService + /remind + /notify; 18 tests green
- [x] Milestone 4: ICS feed export — IcsFeedToken entity, anonymous /feeds/{token}.ics (Ical.Net), POST /guilds/{id}/feed-token, bot /link; 20 tests green; full docker-compose E2E verified (health, NL event create, ICS feed)

## Operations
- [x] CI/CD & process (2026-07-17): GitHub Actions CI (build-test + pr-title lint), release-please auto-versioning from conventional commits, GHCR image publishing (calcrony-api / calcrony-bot), squash-only GitHub Flow with CONTRIBUTING.md, issue forms + label taxonomy. First release: v0.1.0.
- [ ] Deployment target TBD — docker compose pulling GHCR images on personal machine/VPS

## Construction (continued) — post-v0.1.0 milestones
- [x] Milestone 5: Google Calendar Availability (2026-07-17) — CalendarConnection/CalendarLinkToken entities + AddCalendarAvailability migration; ICalendarProvider abstraction + GoogleCalendarProvider (Google.Apis.Auth, hand-rolled freebusy POST); token encryption via ASP.NET Core Data Protection persisted to a new `dpkeys` volume; anonymous /oauth/google/start+callback (API's first non-JSON surface) with the link-token doubling as OAuth `state`; live (uncached) free/busy via CalendarAvailabilityService with concurrency-safe per-user token refresh; POST /calendar/availability + connection endpoints; bot /calendar connect|status|disconnect (DM-capable) and /availability role|event (on-demand grid, not a /create or RSVP hook); 39 tests green (19 new: FakeCalendarProvider substituted via a new ApiFixture ConfigureTestServices/ConfigureWebHost extension point). Branch `feature/calendar-availability`.

- [x] Milestone 7 PR 1 (2026-07-18): Polls & time polls — Poll/PollOption/PollVote model (+AddPolls migration), nine endpoints with event-parity guards, atomic set-replacement voting, voter-added options, NL close deadlines with scheduler auto-close, computed winners, time-poll→event conversion via the PostEventMessage outbox (poll's channel, 128-char title truncation, ConvertedEventId idempotency); bot /poll create|time|close|convert, button voting ≤5 / select menu 6-10 with automatic boundary swap, first Discord modal (➕ add option), three new poller delivery handlers; delivery types 6/7/8. 110 tests green.
- [x] Milestone 7 PR 2 (2026-07-18): Polls on the web — 8 CalCronyWebApiClient poll methods, Events|Polls pill switcher (GuildTabs) on both guild pages, GuildPolls list with open/closed toggle, PollForm (standard/time toggle, dynamic 2–10 option rows with live NL slot previews, closes preview, single-vote hidden for time polls), PollDetail + PollVotePanel (single-vote set/clear + multi-vote XOR toggles as full set-replacement PUTs, own-vote highlighting that survives anonymity, proportional bars, add-option with preview, close/delete confirm-then-act, convert card → navigates to the created event); Docs + README rows. 116 tests green (6 new bUnit).

## Known constraints
- ~~Branch protection/rulesets unavailable (free-plan private repo)~~ Resolved 2026-07-17: repo made **public** (Joshua's call, aware of the one GitGuardian alert on the initial docker-compose dev-placeholder credentials — now parameterized via `CALCRONY_DB_*` env vars, though the literals remain in git history) and the `protect-master` ruleset is active, mirroring FairShare's ruleset adapted to squash-only + required `build-test`/`pr-title` checks, with repository-admin bypass for solo-maintainer merges.
- Google OAuth consent screen must stay in "Testing" status at this project's scale — refresh tokens expire after 7 days; `CalendarAvailabilityStatus.ReconnectRequired` surfaces this distinctly so the fix (`/calendar connect` again) is obvious rather than a generic error.
- `/availability role` requires the privileged "Server Members Intent" enabled in the Discord Developer Portal, and real end-to-end verification against live Google accounts (§15 of the milestone plan) has not yet been run — deployment prerequisites (Google Cloud OAuth app, public HTTPS reachability) are still outstanding.
