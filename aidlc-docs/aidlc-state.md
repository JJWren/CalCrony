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
- [ ] Milestone 2: Events + RSVPs (API + bot commands)
- [ ] Milestone 3: Reminders & notifications (scheduler, outbox, bot poller)
- [ ] Milestone 4: ICS feed export (/link)

## Operations
- [ ] Placeholder (deployment target TBD — docker compose on personal machine/VPS)
