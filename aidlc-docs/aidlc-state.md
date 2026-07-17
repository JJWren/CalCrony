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

## Known constraints
- Branch protection/rulesets unavailable (free-plan private repo) — no-direct-push is enforced by convention; enable the protect-master ruleset if the repo goes public or the plan upgrades.
