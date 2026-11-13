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
| 12. Availability/OAuth (conditional, but for its one **Must pass** item) | ☐ | |
| 13. Precheck bails | ☐ | |
| 14. Restart resilience | ☐ | |

**v1.0.0 gate:** all non-conditional sections pass, and so does section 12's one item marked
**Must pass** (or their findings are fixed and re-verified);
the release PR's squash commit carries `Release-As: 1.0.0` in its footer.

## Free slots and available hours (v0.37.0, issue #171)

Build and test for this feature takes the collapsed form described at the top: the suite on
`master`, the live items added to section 12 of [`live-e2e-script.md`](live-e2e-script.md), and
this section. Both units are on `master`: `/availability slots` (PR #172, `a079441`) and each
member's own available hours (PR #177, `b5b90d6`).

### Automated coverage (at `b5b90d6`, 2026-11-13)

| Project | Tests | Added since v0.36.4 | What the feature's tests cover |
|---|---|---|---|
| CalCrony.Api.Tests | 721 | 201 | The day rule on the date parser (`TryResolveDay`); the pure slot calculation; the week of hours — each check with its message, the stored form, what does not read as a week; the three hours routes against PostgreSQL (defaults, round trips, every refusal, the web's own-account rule, hours and the other settings never touching each other); `POST /calendar/free-slots` against PostgreSQL with the fake calendar provider — who is counted and on what, zones, a day off, midnight to midnight, and the by-name rule, where the provider's call log shows that no calendar is read for a member who has not saved hours |
| CalCrony.Bot.Tests | 418 | 236 | Who can be named and the check of the answer (`FreeSlotTarget`); the reply — its sentences, both member fields, "or later", and the worst case at 5,887 of Discord's 6,000 characters; typed times and suggestions; the week in words, which week a save starts from, what `off` refuses, and every reply; the command tree as Discord.Net builds it, the nested group included |
| CalCrony.Web.Tests | 153 | 24 | The hours card (bUnit) and My settings hosting it; the Docs page entries; the privacy page |
| **Total** | **1,292** | **461** | Green in CI (`build-test`) on the merge commit |

For unit 2, fourteen rules were each taken out once on a copy of the code, and each time the
tests meant to pin that rule failed (section 9 of the unit's plan).

### What no automated test reaches

- Discord itself: how the commands and the nested `/settings available-hours` group show in the
  client, the suggestions for `from` and `to` as they appear while typing, and the lengths Discord
  lets a member type (100 characters in `when`, 20 in `from` and `to`).
- The lines in two modules that only a real interaction runs. In `AvailabilityModule.SlotsAsync`:
  telling a member of this server from anyone else, handing the by-name rule on, and checking the
  answer before replying. The item marked **Must pass** runs all three: a pass shows the first
  two working and the third letting through an answer that was checked by name; that it refuses
  an answer not marked that way is pinned in `FreeSlotTargetTests`. In `SettingsModule`: `view`
  appending the hours line, and `set` and `off` reading before they write.
- A real Google account (one 15-day query for `days:14`), and the reply and the web card at phone
  width.

### On the test stack (2026-11-13)

- For the live pass the stack ran the merged build, `0.0.0-main.b5b90d6`: API, bot and web
  restarted at 20:27Z, the database container left as it was. The API applied
  `AddAvailableHours` at start; the migration history lists it, and
  `UserProfiles.AvailableHoursJson` is a nullable `character varying(512)`.
- Discord's command list for the test server showed the registration as built: `/settings` with
  the group `available-hours` holding `set`, `off` and `reset`; `days` with its ten choices;
  `from` and `to` with suggestions on and a length limit of 20; `/availability slots` beside
  `role` and `event`, its `who` reading "Yourself, a role, or a member who has saved their
  available hours".
- The stack's database was new that morning — its volume dates from 09:20Z, before this refresh
  and not part of it — and held one server and no events, personal settings or linked calendars.
  The items that name a calendar needed `/calendar connect` on the test application first.

### Live results (filled during the pass)

| Section 12 block | Result | Findings / fix PRs |
|---|---|---|
| **Must pass** — naming another member: refused, listed once they have saved hours, refused again after `reset`; an account outside the server refused | ✔ 2026-11-13, on Joshua's reports, with the stack agreeing as far as it can show: refused, listed, refused again (second run); the account outside the server refused (third run). Not run live: the refusal of a member who has a calendar linked | none reported |
| Free slots — the other items | In part (first run, below) | none reported |
| Available hours — the commands, their replies and refusals | In part: `set`, `reset` and `/settings view`, from two accounts | none reported |
| Available hours — the web card | In part: Save (21:01:16Z, between the first two runs); Reset to default not run | none reported |
| Available hours — slots on saved hours (own hours, hours only, a mixed role, midnight to midnight) | In part, second run, as Joshua reported it: own hours with a calendar linked (the lookup at 21:23:14Z), and hours only (the list at 21:21:50Z, the account named having no calendar linked); a mixed role and midnight to midnight not run | none reported |
| A member who never saved hours is treated as before | Reported as passed (first run) | none reported |

**First run, 2026-11-13.** Joshua ran the feature on the test stack and reported that all of it
passed. What the stack shows of that run (20:55Z–21:00Z, one account): two free-slot lookups,
both answered, with a calendar linked between them; hours saved with
`/settings available-hours`, saved once more and reset, with a personal timezone set in between;
and a sign-in on the test web app. Not among it: a lookup that was refused, a lookup after hours
had been saved or reset, and hours saved by a second account. The item marked **Must pass** needs
all three, and a second person in the server — the test server had one, beside three bots — so
it was still open after that run, as were the items on slots cut from saved hours and the web
card's Save and Reset.

**Second run, 2026-11-13.** With the item's steps written out for two accounts, a second account
joined the test server (21:19:57Z) and Joshua reported every step as expected. The stack's log
shows three free-slot lookups in the order of the steps — answered with an error (21:21:05Z), a
list (21:21:50Z) and an error (21:22:30Z) — on either side of that account's
`/settings available-hours set` (21:21:37Z) and its `reset` (21:22:25Z). The log does not
record who a lookup names or why one is refused: that these were lookups of that account by
name, refused for want of saved hours, rests on that order and on Joshua's report. The account
had no calendar linked, so the list at 21:21:50Z was cut from its saved hours alone. Also in
the log: a lookup answered with a list right after the first account's own reset and save
(21:23:14Z), taken from the steps as the one on its own saved hours, and the reads
`/settings view` makes (21:23:53Z); and, between the two runs, a save of that account's hours
that did not come through the bot (21:01:16Z), taken as the web card's Save, with lookups after
it. What the log cannot show is the refusal of an account from outside the server, which the
bot makes without asking the API. No lookup was answered with an error before the second
account joined, as one naming it would have been had the bot handed it on; but it had not
signed in to the web app or saved hours by then, so that part was not run in the form the
script gives it — an outside account that has saved hours. That one check was left to be run
on its own.

**Third run, 2026-11-13.** The check on its own, in the form the script gives it: the second
account saved hours again (21:31:11Z) and left the test server, and Joshua reported that naming
its id in `who:` got "That user isn't a member of this server." The stack is consistent with
that and cannot confirm it: the account was outside the server with its hours saved — Discord
shows it back in the server since 21:33:02Z, where its earlier join was at 21:19:57Z — and no
lookup reached the API in that time or after it, which is what a working check leaves behind
and also what not naming the id would. With the second run, every part of the item marked
**Must pass** that was run has passed. One case of it was not run live: the script has the
refusal be the same whether or not the member named has linked a calendar, and the member named
in these runs had none linked. That case rests on the API's tests (`FreeSlotsApiTests`, a
member with a linked calendar and no saved hours).

**Before 0.37.0 was released:** the item marked **Must pass** had to pass, and it did, as far
as the runs above go. The table says what was run, block by block; anything not named there
was not run, and whether any of it had to be run first was Joshua's call when he approved this
stage. A failure would have meant a `fix:` PR and a re-run of its block; none was reported.

**Approved, 2026-11-13.** Joshua approved build and test ("Yes, proceed") with the items in the
table as they stood. Release 0.37.0 followed the same day on his go-ahead — PR #175
squash-merged at 22:01:58Z as `f904e33`, `v0.37.0` tagged and published — and prod was rolled
to it at 22:16Z.
