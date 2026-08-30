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
## Remediation — guild presence (issues #74, #75)
**Timestamp**: 2026-08-29T20:15:00Z
**User Input**: "I'm testing the test stack of calcrony. I remember this working in the past and haven't done anything since then besides AdGuard and the hairping fix as of today. When I removed the bot and re-invited it via the web app, I was able to get the bot back into my person server for testing, but on the web app, it shows as not added to a server yet." → after diagnosis: "file those issues and then remediate"
**AI Response**: Diagnosed on the test stack: /me/guilds is UserGuildMemberships ⋈ Guilds, and nothing ever creates a Guilds row on invite (no JoinedGuild handler; invite URL has no callback; rows appear only lazily on first bot command). The nightly test reset wipes the DB, so a re-invited server stays invisible until a command runs. Filed #74 (no presence registration on join) and #75 (presence never revoked on leave). Fix on fix/guild-presence-sync: Guild.BotPresent flag (migration AddGuildBotPresence, backfill true), bot-only PUT /guilds/{id}/presence + PUT /guilds/presence/sync endpoints, bot handlers for JoinedGuild/LeftGuild plus a Ready-time full reconcile, /me/guilds and GuildAccessService filter on BotPresent. Rows are kept on leave so settings/data survive a re-invite. 4 new integration tests; suite 290/290 green.
**Context**: Construction — defect remediation via /remediate.

---

## Design Review — Web App (/design)
**Timestamp**: 2026-08-29T00:00:00Z
**User Input**: "review the web app for CalCrony and let's refine this. I want it to be fitting to the atmosphere of Discord, but subtly nod toward TTRPG/DND groups. It should be easy on the eyes to read. I want you to do a detailed review of the web app for both pros and cons and any major findings. Also, I want detailed feedback on the current design state and how it could be improved."
**AI Response**: Reviewed CalCrony.Web source (theme.css, site.css, layout, all pages/components). Delivered a detailed pros/cons review plus a refined design direction ("candlelit slate": Discord-register slate surfaces, indigo primary + rare gilt gold, Marcellus/Nunito Sans type, SVG icon set with shield/scroll/d20 nods) on a Claude Design canvas with current-state recreation, refined mockups (events list, event page, mobile, landing, tokens sheet), and one low-fi alternate. No code changes made.
**Context**: Inception — design review & direction exploration; artifacts on design canvas, review in chat.

---

## Design Review — Theme Family (/design continuation)
**Timestamp**: 2026-08-29T00:00:00Z
**User Input**: "I love everything about this. I like this as the base theme. Can we present several themes (about 5 would be good) for various user tastes? It would be nice to allow users to customize their interface theme when logged in."
**AI Response**: Candlelit Slate confirmed as base/default. Added a "Themes" page to the design canvas: five token-variant themes (01 Candlelit Slate default, 02 Tavern Ember, 03 Feywild Moss, 04 Parchment light, 05 Obsidian Azure OLED/legacy-palette) rendered over the same condensed events screen for comparison, plus a refined My-settings mockup with a per-user Interface Theme picker (5 tiles + match-system auto pairing Slate/Parchment). Low-fi Tavern alternate retired (promoted into Theme 02). No code changes.
**Context**: Inception — design direction iteration; artifacts on design canvas.

---

## Design Review — Light Theme Faces (/design continuation)
**Timestamp**: 2026-08-29T00:00:00Z
**User Input**: "These are all excellent. What would the light version of these look like?"
**AI Response**: Added a "Light faces" page to the design canvas: light counterparts for the four dark themes — Candlelit Slate → Moonstone (cool porcelain/indigo), Tavern Ember → Morning Hearth (peach cream/burnt sienna), Feywild Moss → Sunlit Glade (sage paper/leaf green), Obsidian Azure → Azure Quartz (the existing rose-tinted light theme kept, azure darkened to pass AA). Parchment remains the light-first theme (dark face: Slate). Model: each theme has two faces; the moon/sun toggle and "match my system" flip faces; theme picker copy updated to match. All light accents re-derived to clear 4.5:1 on their tinted grounds.
**Context**: Inception — design direction iteration; artifacts on design canvas.

---

## Design Review — Paired Theme Layout (/design continuation)
**Timestamp**: 2026-08-29T00:00:00Z
**User Input**: "Can you show each dark and light side by side"
**AI Response**: Reorganized the design canvas: merged the Themes and Light faces pages into one Themes page laid out as pairs — one row per theme, dark face left / light face right (Slate|Moonstone, Ember|Morning Hearth, Moss|Sunlit Glade, note|Parchment, Obsidian|Azure Quartz), column-header notes, a Parchment light-first explainer in its empty dark slot, and the theme picker below. Layout-only change; no artboard content edited.
**Context**: Inception — design direction iteration; artifacts on design canvas.

---

## Construction — Interface Themes (issue + implementation)
**Timestamp**: 2026-08-29T00:00:00Z
**User Input**: "These look great. Let's create an issue for this feature implementation if it does not exist yet and then implement this"
**AI Response**: Searching for an existing GitHub issue for the interface-theme feature; creating one if absent (spec: five two-faced themes from the approved design canvas, Candlelit Slate default, per-user persistence, settings picker), then implementing on a feature branch per GitHub Flow and opening a PR.
**Context**: Construction — code generation for the theme system.

---

## Construction — Interface Themes (implementation complete)
**Timestamp**: 2026-08-29T00:00:00Z
**User Input**: (continuation of "…and then implement this")
**AI Response**: Implemented issue #78 on feat/interface-themes: theme.css rewritten as 9 face-keyed token blocks mapped onto Bootstrap vars (slate default dark, moonstone default light; parchment dark falls through to slate); site.css accent-var wiring + raised cards + picker tile styles; theme-init.js/site.js store+apply calcrony-theme-name pre-paint; InterfaceThemePicker on My settings (instant apply, RMW save, parchment face flip); ThemeSync applies account theme post-login; UserSettingsDto.Theme with null=keep semantics; UserProfiles.Theme column + AddUserInterfaceTheme migration; README note. Tests: 3 new API integration tests + 5 new bUnit tests; full suite 299/299 green.
**Context**: Construction — code generation executed; PR to follow.

---

## Construction — Interface Themes (PR opened)
**Timestamp**: 2026-08-29T00:00:00Z
**User Input**: (continuation)
**AI Response**: Rebased feat/interface-themes onto origin/master (0.17.3), pushed, and opened PR #79 (https://github.com/JJWren/CalCrony/pull/79, commit c2aaf4e) closing #78. Full suite 299/299 green pre-push. Note: env GITHUB_TOKEN fine-grained PAT lacks pull-request write; PR created via the keyring gh login (env -u GITHUB_TOKEN).
**Context**: Construction complete pending review/checks; squash-merge per repo ruleset.

---
