# CalCrony Audit Trail

## Initial User Request
**Timestamp**: 2026-07-17T00:00:00Z
**User Input**: "I want you to review sesh.fyi (https://sesh.fyi/) and all of its features. I want to create a clone of it for my personal discord servers. I want it built in .NET. The core backend of it will be an API. A bot's interface will be the core way to interact with the API via an API key. Let's design this application together starting with determining what the core of the application is and then extra features."
**AI Response**: Researched sesh.fyi and its manual; presented full feature inventory; asked clarifying questions (core scope, bot library, database, extras priority).
**Context**: Inception — Requirements Analysis (greenfield project, executed via Claude Code plan mode).

---

## Requirements Clarification (multiple choice answers)
**Timestamp**: 2026-07-17T00:00:00Z
**User Input**: Core scope: "Option 1 of the choices you gave, but include calendar sync" (Option 1 = Events + RSVPs + reminders); Bot library: "Discord.Net (Recommended)"; Database: "PostgreSQL (Recommended)"; Extras: "Polls / time polls, ICS feed / calendar export, Web dashboard, Recurring events".
**AI Response**: Asked follow-up to disambiguate "calendar sync" (ICS export vs one-way GCal push vs two-way sync).
**Context**: Inception — Requirements Analysis.

---

## Calendar Sync Clarification
**Timestamp**: 2026-07-17T00:00:00Z
**User Input**: "ICS feed export (Recommended)"
**AI Response**: Locked v1 core = Events + RSVPs + reminders/notifications + ICS feed export; wrote full design plan (architecture, data model, API surface, bot commands, milestones).
**Context**: Inception — Workflow Planning.

---

## Plan Revision Request
**Timestamp**: 2026-07-17T00:00:00Z
**User Input**: "I am going to go with option 1, but first thing that is needed is a private repo establish on github for this. We shall call the app CalCrony. This should link to a local repo by the same name located within \"C:\\Users\\joshu\\source\\repos\"."
**AI Response**: Renamed project to CalCrony throughout plan; added Milestone 0 (private GitHub repo + local repo at C:\Users\joshu\source\repos\CalCrony); resubmitted plan.
**Context**: Inception — Workflow Planning revision.

---

## Plan Approval
**Timestamp**: 2026-07-17T00:00:00Z
**User Input**: Plan approved via Claude Code plan-mode approval.
**AI Response**: Began Construction. Milestone 0 complete (private repo https://github.com/JJWren/CalCrony created and pushed). Milestone 1 in progress (solution scaffold, EF Core + Postgres, API-key auth, health endpoint, docs).
**Context**: Construction — Milestone 0/1.

---

## Milestone 2 Completion
**Timestamp**: 2026-07-17T00:00:00Z
**User Input**: (none — autonomous execution of approved plan)
**AI Response**: Implemented events + RSVPs end to end. Notable defects found and fixed during verification: (1) ASP.NET minimal APIs treated NaturalDateTimeParser's TryParse method as a bindable-primitive signature and threw at route building — renamed to TryResolve; (2) new Rsvp entities with client-set Guid keys attached via navigation fixup were issued as UPDATEs — switched to explicit DbSet.Add; (3) duplicate collection entry after fixup — removed manual nav add. All 15 tests pass.
**Context**: Construction — Milestone 2 code generation + verification.

---

## Milestones 3 & 4 Completion
**Timestamp**: 2026-07-17T00:00:00Z
**User Input**: (none — autonomous execution of approved plan)
**AI Response**: Milestone 3: EventNotification/Delivery entities, DeliveryScheduler sweep, outbox pending/ack endpoints, /reminders, bot poller, /remind, /notify. Milestone 4: IcsFeedToken, anonymous ICS feed endpoint (Ical.Net), /link command. 20 automated tests green. Full docker-compose E2E verified: health, NL event creation ("tomorrow 6pm" parsed correctly), 3 default RSVP options, feed-token mint, ICS feed served with correct DTSTART/SUMMARY. Compose stack and test volume torn down after verification.
**Context**: Construction — Milestones 3–4 code generation + verification.

---

## CI/CD & Process Setup
**Timestamp**: 2026-07-17T17:45:00Z
**User Input**: "Immediate need: the project needs github workflow for ci/cd operations. We should have PR review cycles. We need versioning and releases. We also need standardized branching for developing/testing/implementing new features, taking care of bugs, etc. We should have a branching strategy and documentation of such. Additionally, we should have a standardized way of opening and closing issues/requests."
**AI Response**: Plan approved with decisions: GitHub Flow branching, conventional commits + release-please auto-versioning, GHCR images + GitHub Releases, squash-only merges. Implemented via PR #1 (CI workflow, release-please workflow, PR/issue templates, CONTRIBUTING.md, version-stamped Dockerfiles, /health version). Repo configured: squash-only + auto-delete branches, label set, Actions allowed to create PRs. Branch protection attempt rejected (403 — free-plan private repo), documented as convention instead. Two bootstrap findings: Release-As footer was swallowed by the markdown PR body (fixed with a clean empty bootstrap commit), and "Allow GitHub Actions to create and approve pull requests" was off by default. Release PR #2 merged → v0.1.0 tag, GitHub Release, GHCR images.
**Context**: Operations — CI/CD pipeline and engineering process.

---
