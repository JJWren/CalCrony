# CalCrony — Build & Test Summary (Milestone 15)

## Workflow adaptation note

The AI-DLC Build-and-Test stage prescribes five instruction files (build, unit-test,
integration-test, performance-test, summary). For CalCrony that set collapses deliberately:

- **Build + unit + integration instructions** are two commands, documented in README §Running
  locally and executed by CI (`build-test`) on every PR:
  `docker compose up` (stack) and `dotnet test CalCrony.slnx` (full suite; integration tests run
  against real PostgreSQL via Testcontainers — Docker must be running).
- **Performance testing** is not applicable at this project's scale (a personal-server Discord
  bot; the heaviest loop is a 15-second poller over a bounded outbox).
- What CANNOT be automated — real Discord surfaces, real calendar apps, container recovery — is
  covered by [`live-e2e-script.md`](live-e2e-script.md), which gates v1.0.0.

## Automated coverage (at M15 PR5)

| Project | Tests | What it covers |
|---|---|---|
| CalCrony.Api.Tests | 217 | Endpoint integration against Testcontainers PostgreSQL: guards/auth (404 anti-probe, BotOnly/UserOnly, self-or-bot), events/RSVPs, recurrence (calculator + scheduler sweeps + series verbs), polls, templates, attendee roles, threads, notifications, ICS/RRULE, calendar availability (fake provider), web auth dance, validation 400s, retention purge, startup config validation |
| CalCrony.Bot.Tests | 31 | Pure logic: embed builders, finders/autocomplete suggestion builders, recurrence descriptions, native-event spec mapping, attendee-role spec prechecks, thread-name mapping |
| CalCrony.Web.Tests | 29 | bUnit components: RSVP panel, forms (create/edit incl. scope asks, template picker, thread checkbox, role clearing), polls, templates, chips, invite-URL pin |
| **Total** | **277** | Green as of PR5 |

Deliberately untested by convention: best-effort Discord side-effect helpers
(`NativeEventMirror.Try*`, `AttendeeRoleManager.Try*`, `EventThreadManager.Try*`) — never-throw
wrappers over Discord.Net whose bail conditions are exercised live in E2E sections 6–8 and 13.

## Feature-area coverage map

| Area | Automated | Live E2E section |
|---|---|---|
| Events CRUD + NL parsing | ✔ heavy | 1 |
| RSVPs/capacity | ✔ | 2 |
| Reminders/notifications | ✔ | 3 |
| Recurrence verbs | ✔ heavy | 4 |
| Templates | ✔ | 5 |
| Attendee roles | ✔ (outbox) | 6 (Discord grants) |
| Threads | ✔ (outbox) | 7 (Discord threads) |
| Native mirroring | ✔ (outbox/spec) | 8 (Events tab) |
| Polls | ✔ heavy | 9 |
| ICS/RRULE | ✔ (content) | 10 (real calendar app) |
| Web parity | ✔ (bUnit) | 11 (real browser + sync) |
| Google availability | ✔ (fake provider) | 12 (conditional) |
| Permission prechecks | ✔ (pure specs) | 13 (live bails) |
| Ops: restart/health/retention | ✔ (purge, /health/ready) | 0, 14 |

## Live E2E results (filled during the pass)

| Section | Result | Findings / fix PRs |
|---|---|---|
| 0. Deployment & first steps | ☐ | |
| 1. Events CRUD | ☐ | |
| 2. RSVPs | ☐ | |
| 3. Reminders/notifications | ☐ | |
| 4. Recurrence | ☐ | |
| 5. Templates | ☐ | |
| 6. Attendee roles | ☐ | |
| 7. Threads | ☐ | |
| 8. Native mirroring | ☐ | |
| 9. Polls | ☐ | |
| 10. ICS/RRULE | ☐ | |
| 11. Web parity | ☐ | |
| 12. Availability/OAuth (conditional) | ☐ | |
| 13. Precheck bails | ☐ | |
| 14. Restart resilience | ☐ | |

**v1.0.0 gate:** all non-conditional sections pass (or their findings are fixed and re-verified);
the release PR's squash commit carries `Release-As: 1.0.0` in its footer.
