# Free slots and available hours — `/availability slots`: requirements analysis

**Issue**: #171 (filed 2026-11-11, after this analysis was approved) — the request came from the
community Discord on 2026-11-10. **Depth**: standard —
additive: a new bot subcommand and bot-only API route beside the existing availability check, plus a
per-member setting (one additive migration) with a surface in Discord and on the web.
**Date**: 2026-11-10, revised 2026-11-11. **Baseline**: `master` 4911a1f (v0.36.3, the version prod
runs); the audit trail records 810 tests green at that release — not re-run for this analysis.
**Status**: approved 2026-11-11. Q1–Q7 answered — A, A, **B**, A, A, A, A (Q6 and Q7 were opened by
the Q3 answer); the assumptions in section 5 stand.

## 1. Intent

A game master running an open table wants players to see when the GM is free — for a day, this week,
this month — and pick a session time from that. Their sketch:

```text
/availability role role:@GM when:nov 12

Availability for @GM
🗓️ Thursday, 12 November 2026

✅ 10am-2pm @GM — free
✅ 5pm-8pm @GM — free
```

Today the bot answers a narrower question: *who is busy in this one window?* A member has to guess a
time, ask, and repeat. The same gap sits under the feature's documented purpose — the Docs page tells
groups to check `/availability role` "before you pick a session time", yet it can only confirm or
reject a time someone already has in mind. A free-slots view turns that around: name who and which
days, get the open times.

A calendar only knows commitments, though: an empty one reads as free at 3 AM. Joshua's answer to Q3
makes the member the source of that missing fact — each member stores the hours they are available,
and slots are cut from those hours. That is the GM's "see *my* available slots" taken literally.

## 2. What exists today (verified against the code)

### 2.1 Availability

| Site | Today | For this change |
|---|---|---|
| `AvailabilityModule.RoleAsync` (`src/CalCrony.Bot/Modules/AvailabilityModule.cs:22`) | One window: `when` is parsed to the start, `duration` (minutes, default 60, no min or max) gives the end. Members come from `DownloadUsersAsync` (Server Members intent), capped at 50. Reply is ephemeral. Any member may run it. | The new subcommand reuses the member enumeration, the cap and the ephemeral reply. |
| `AvailabilityModule.EventAsync` (same file, `:69`) | The event's own window and its seated attending list. | Unchanged. |
| `AvailabilityEmbedBuilder` (`src/CalCrony.Bot/AvailabilityEmbedBuilder.cs:17`) | One line per member; busy blocks print as `<t:…:t>` — time only, so a window longer than a day lists times without dates. | A separate builder for slots, with day labels. |
| `NaturalDateTimeParser` (`src/CalCrony.Api/Services/NaturalDateTimeParser.cs:154`, `:115`) | A bare date resolves to 09:00 local; a result in the past is rejected. Run against the real parser on 2026-11-10: `nov 12` → Thu 09:00, `nov 12 8am` → Thu 08:00, `today 8am` (already past) → error. So `when:nov 12` checks 9–10 AM, and `when:today` fails after 09:00. | Slots need day-level resolution: a day stays valid while any of it remains. |
| `POST /tools/parse-datetime` (`src/CalCrony.Api/Endpoints/EventEndpoints.cs:1757`) | Zone = the caller's `/settings timezone`, else the server's, else UTC. | The same rule decides where the caller's days start and end, and — applied to each member — where that member's hours sit. |
| `POST /calendar/availability` (`src/CalCrony.Api/Endpoints/CalendarEndpoints.cs:159`) | Bot-only ("exposing it to web callers would let any signed-in user probe any Discord user's free/busy"); 1–50 distinct user ids; end after start; no upper bound on the window. | A new bot-only route beside it with the same id guard. |
| `CalendarAvailabilityService.CheckAsync` (`src/CalCrony.Api/Services/CalendarAvailabilityService.cs:35`) | One Google call per linked member for the window, token refresh included; returns each member's status and busy blocks. | Reused unchanged: one call per member covers the whole range. |
| `GoogleCalendarProvider.GetFreeBusyAsync` (`src/CalCrony.Api/Services/GoogleCalendarProvider.cs:112`) | Queries the `primary` calendar only. Google's reference documents no maximum range and does not say busy intervals are clipped to the query. | Unchanged; the slot calculation clips busy blocks itself. |
| `CalCrony.Contracts/Calendar.cs` | `AvailabilityRequest` / `AvailabilityResponse`, `UserAvailabilityDto`, `BusyBlockDto`, `CalendarAvailabilityStatus`. | New request and response records. |
| Web: `GET /events/{id}/availability`, `AvailabilityGrid.razor` | Event-scoped grid only — nothing caller-controlled to probe with. | Untouched; slots get no web view in this change. |
| ADR 0003 (`docs/adr/0003-google-stays-freebusy-only.md`) | Free/busy scope only, permanently. | The feature needs busy intervals and nothing else from Google. |

### 2.2 Personal settings (where available hours will live)

| Site | Today | For this change |
|---|---|---|
| `UserProfile` (`src/CalCrony.Api/Data/Entities.cs:87`) | One row per Discord user, global across servers: optional timezone, DM switches, theme. | Gains the member's available hours (storage shape decided at application design). |
| `UserSettingsDto` (`src/CalCrony.Contracts/Events.cs:395`) | Timezone and DM confirmations are written as sent; theme and DM reminders follow "null on a write keeps the stored value", added so writers that don't handle a field can't wipe it. | Hours must follow the null-keeps rule, or travel on their own route. |
| `GET` / `PUT /users/{id}/settings` (`src/CalCrony.Api/Endpoints/SettingsEndpoints.cs:101`, `:136`) | Web callers may only read and write their own; the bot acts for the member who ran the command. | The same guard covers hours: only the member sets their own. |
| `SettingsModule` (`src/CalCrony.Bot/Modules/SettingsModule.cs:17`, `:80`) | `/settings view` shows server and personal settings; `/settings timezone` picks a zone through `TimeZoneAutocompleteHandler`; every write is read-modify-write. | Gains the hours commands; `view` shows the caller's hours. |
| `UserSettings.razor` (`src/CalCrony.Web/Pages/App/UserSettings.razor`) | "My settings": theme picker, a timezone and DM card, a Google Calendar card. | Gains an available-hours card. |
| Privacy policy (`PRIVACY_POLICY.md:30`, `Privacy.razor:39`) | Preferences are listed. The two copies already differ: the web copy names the DM-reminder setting, the repository copy does not. | Both name available hours; the existing difference is closed in the same edit. |

### 2.3 Docs and tests

| Site | Today | For this change |
|---|---|---|
| `README.md:42`, `:63`, `:88`; `Docs.razor:53`, `:104`, `:260`, `:341`, `:347`; `HelpModule.cs:28` | One README bullet and two command rows; a Docs entry ("Google Calendar availability"), a quick-start step, and command rows for `/availability` and `/settings`; `/help` says "availability grids". | README bullet reworded inside the 400-word cap; Docs entries and command rows extended on both pages. |
| `PRIVACY_POLICY.md:48`, `Privacy.razor` | "Free/busy data is used solely to render availability grids to members of your Discord server, is never stored". | Still true — slots are computed live and nothing from Google is stored; "grids" becomes "grids and free-slot lists". |
| Tests | `CalendarAvailabilityTests` with `CalendarApiFixture` and `FakeCalendarProvider`, `EventAvailabilityTests`, `AvailabilityEmbedBuilderTests`, `NaturalDateTimeParserTests`, `UserSettingsApiTests`, `WebAuthorizationTests`, `ThemeComponentTests` (the theme picker's settings writes, including "without clobbering other settings"), `ModuleContextTests` (reflects over every module), `DocsPageTests`. | The fake provider already serves canned busy blocks per token; the module-context pin covers new subcommands through their modules. |

Checked outside the repo: Discord.Net 3.20.1 (the pinned version) ships `DefaultMentionableConverter`,
so a single option can accept a member *or* a role (Q2). Discord's embed limits are 4096 characters
for the description, 1024 per field value and 6000 for the whole embed.

## 3. Functional requirements (final — Q1–Q7 answered)

**Free slots**

- **FR1** A member can ask for the free slots of a target — a role or a single member — over one to
  fourteen whole days, without naming a time of day.
- **FR2** A slot is a stretch during which every counted member is inside their own available hours
  and has no busy block on a linked calendar, at least the minimum length long. Slots never start in
  the past: today's first slot starts at the current time rounded up to the next quarter hour.
- **FR3** A member is counted when their linked calendar could be read, or when they have saved
  hours and linked no calendar — then on those hours alone, marked "hours only" (Q7 A). A member with
  neither hours nor a calendar is not counted, and neither is one whose linked calendar could not be
  read (needs to reconnect, couldn't check). Every member who is not counted, or is counted on less
  than full information (hours only, or the default hours of FR13), is named with the reason. When
  nobody is counted the reply says so and lists no slots.
- **FR4** Days are calendar days in the caller's zone (their `/settings timezone`, else the server's,
  else UTC) and the reply names that zone. Slot times are Discord timestamps, shown in each viewer's
  local time. A slot belongs to the day it starts on.
- **FR5** The first day is given in natural language (`when:nov 12`, `when:saturday`, `when:today`)
  and defaults to today. Today stays valid until it ends — unlike the 09:00 rule in section 2. A day
  that is already over is refused.
- **FR6** The reply states the range, the minimum length and whose hours and calendars it rests on,
  then lists each day with its slots in order, or "nothing free". It fits Discord's embed limits at
  fourteen days; a day with more slots than fit shows the first ones and "+N more".
- **FR7** One Google free/busy query per linked member covers the whole range — no per-day fan-out.
- **FR8** The reply is ephemeral. `/availability role` and `/availability event` behave exactly as
  they do today.
- **FR9** Refusals are friendly and ephemeral: a day that can't be understood, a past day, a minimum
  longer than a day, more than 50 members, a role nobody holds.

**Available hours**

- **FR10** A member can store, see and reset their own available hours from Discord and from the
  web's My settings page. Nobody else can set them, and they are not listed to other members — only
  their effect on slots shows.
- **FR11** Hours are one range per weekday, each weekday separately available or not (Q6 A). They
  can be set for every day, for weekdays, for weekends or for a single day at a time.
- **FR12** Hours are wall-clock times in the member's zone, resolved as everywhere else: their
  `/settings timezone`, else the zone of the server the command runs in, else UTC. Saving hours
  without a personal timezone says which zone applies.
- **FR13** A member who never set hours gets 8 AM–11 PM every day under the same zone rule, and the
  slots reply names the members that default was applied to.
- **FR14** Existing settings writers (timezone, DM switches, theme) cannot wipe stored hours.
- **FR15** The migration is additive; rolling the image back stays safe.

**Both**

- **FR16** Nothing from Google is stored; no new Google scope, Discord permission or intent. ADR 0003
  holds. A member's hours are the only new stored data.
- **FR17** Docs: the README bullet (inside the 400-word cap) and command rows; the Docs page entries
  and command rows, each naming where it lives, who may use it and what is opt-in. Privacy policy,
  both copies: available hours under Preferences, "free-slot lists" in the Google Limited Use
  sentence, the date, and the DM-reminder wording brought level.
- **FR18** Tests: a pure slot calculator (clipping, overlapping blocks from several members, hours in
  different zones, minimum length, the rounded "now", a daylight-saving day, each counted and
  uncounted state); route tests on the fake provider; day resolution ("today" late in the day, a
  past day); settings round-trips including the cannot-wipe rule and the self-only guard; the bot's
  parsing and formatting of hours; the web card; the embed at its worst case against Discord's limits.

## 4. Design questions

Answer with the letter; a recommendation is marked.

**Q1 — How is it invoked?**

- A) A new subcommand, `/availability slots`, next to `role` and `event`, which stay as they are.
  **(Recommended — it shows up in the command picker with its own description, changes no existing
  behaviour, and every option means one thing.)**
- B) A `days:` option on `/availability role`: giving it switches the reply from the grid to slots.
  `duration` would then mean "window length" in one mode and "minimum slot length" in the other.
- C) `/availability role` switches by itself when `when` has no time of day (`when:nov 12` → slots,
  `when:nov 12 7pm` → grid). This is what the Discord user typed, but it changes today's
  "bare date means 09:00" behaviour and needs the parser to report whether a time was given.
- D) A, plus a hint from `/availability role` when `when` had no time of day ("Checked 9–10 AM — for
  the whole day use `/availability slots`"). Needs the same parser change as C.
- E) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-11.

**Q2 — Who can it look at?**

- A) A role or a single member, through one option: `who:@GM` or `who:@Sam` (Discord's mentionable
  picker). **(Recommended — an open table often has several GMs sharing one role, and players want
  one GM's slots, not the overlap of all of them. No new exposure in kind: `/availability role`
  already prints one line per member.)**
- B) A role only (`role:@GM`), mirroring `/availability role`. A GM then needs a role of their own.
- C) A role, a member, or an event's attending list.
- D) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-11.

**Q3 — Which hours of the day count?** A calendar only knows commitments: an empty one reads as free
at 3 AM.

- A) The caller sets them per command — `earliest:` and `latest:` hour pickers, default 8 AM to
  11 PM in the caller's zone — and the reply states the hours used. Nothing is stored. A member keeps
  time off the list by blocking it on their own calendar (a recurring busy event). **(Recommended.)**
- B) Each member stores their own available hours (`/settings available-hours`, plus the web settings
  page) and slots respect every counted member's hours in that member's zone. The closest fit to
  "see *my* available slots", but it adds a profile field, a migration, a settings surface on both
  sides and a privacy-policy line — roughly double the change — and one daily range still can't say
  "evenings on weekdays, all day at weekends".
- C) No bound: every free minute of the day is listed.
- D) A now; B filed as its own follow-up issue.
- E) Other (describe).

[Answer]: B — not the recommendation; Joshua, 2026-11-11: "Let's go with each member stores their own available hours with a new setting." This opens Q6 and Q7.

**Q4 — How far ahead in one reply?**

- A) `days:` from 1 to 14, default 7, counted from `when` (default today). One embed, one line per
  day; a month is two or three commands. **(Recommended.)**
- B) One day only — the literal request ("for a day").
- C) Seven days per reply with ◀ ▶ buttons to move week by week. Reaches "this month" in clicks, at
  the cost of a component handler that queries Google again on every click.
- D) Up to 31 days in one reply, compact: at most two slots shown per day, the rest as "+N".
- E) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-11.

**Q5 — Who sees the reply?**

- A) Only the caller (ephemeral), as with `/availability role` and `event`. A GM tells players to run
  it; every run is live. **(Recommended — no new exposure, and the day boundaries are computed for
  the one person reading.)**
- B) Ephemeral by default, plus `share:true` to post it in the channel — allowed only when the target
  is the caller themself (a GM publishing their own slots). A posted list is a snapshot that goes
  stale, and it needs a date on every slot because readers sit in different zones.
- C) `share:true` for any target.
- D) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-11.

**Q6 — What shape do a member's available hours take?** (Opened by Q3 B.)

- A) One range per weekday, each day separately on or off, set in bulk (every day, weekdays,
  weekends) or one day at a time. **(Recommended — the simplest shape that can say "evenings on
  weekdays, all day at weekends", which is the GM's case. Gaps inside a day come from the calendar.)**
- B) One range that applies every day.
- C) Two ranges: weekdays and weekends.
- D) Several ranges per weekday (10 AM–2 PM and 5–8 PM on the same day).
- E) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-11.

**Q7 — Does a member need a linked Google calendar to be counted?** (Opened by Q3 B.)

- A) No. A member who set hours but linked no calendar is counted on those hours alone, and the
  reply marks them "hours only". A GM can publish availability without touching Google; a member
  with neither hours nor a calendar stays uncounted. **(Recommended — it makes the feature work for
  everyone who won't go through Google's consent screen, and what the reply rests on stays visible.)**
- B) Yes. Hours only narrow what a linked calendar shows; without a calendar a member stays
  "not linked" and uncounted, as on the grids today.
- C) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-11.

## 5. Assumptions (accepted unless Joshua says otherwise)

- **Several members**: a slot means every counted member is available for all of it. Ranking partial
  overlaps ("4 of 5 free") is out of scope.
- **Names**: subcommand `slots` with options `who`, `when`, `days`, `duration`; the setting is
  "available hours", reached as `/settings available-hours`. Subcommand description: "Find free time
  slots for a member or role over one or more days".
- **No caller-side hour options**: with hours coming from members, `earliest:` and `latest:` from
  Q3 A are dropped rather than kept as an extra filter.
- **Minimum length**: `duration:` in minutes, default 60 — the name and default `/availability role`
  already uses — from 15 to 1440.
- **Hours detail**: times in 30-minute steps, picked from suggestions or typed (`6:30pm`, `18:30`);
  the end may be midnight; a range starts before it ends, so no overnight ranges. Once a member has
  saved hours, a weekday without a range is unavailable; saving none at all resets to the default.
  *Changed at application design (D7, approved 2026-11-11): a week with no day at all is refused,
  and the member is pointed at reset.*
- **Setting hours in Discord**: set a range for every day, weekdays, weekends or one day; mark days
  off; reset. The confirmation spells out the whole week and the zone it will be read in.
  `/settings view` shows the caller's hours.
- **Setting hours on the web**: a card on My settings — seven rows (available, from, to), Save,
  Reset — with the zone rule stated beside it.
- **First day**: the time of day in `when` is ignored. Resolution happens in the API through a new
  day-level entry point on `NaturalDateTimeParser`.
- **Daylight saving**: each member's hours are built from local times in their zone, so a 23- or
  25-hour day still runs 6 PM to 11 PM.
- **API**: a bot-only `POST /calendar/free-slots` taking the user ids (1–50 distinct), the caller and
  guild ids, the `when` text, days and minimum length, and returning the caller's zone, the resolved
  range, each day's slots as UTC instants and each member's status. The calculation is a pure
  NodaTime function over interval sets; `CalendarAvailabilityService.CheckAsync` is called as it is.
  How hours are stored and carried (columns, a child table, a field on `UserSettingsDto` or a route
  of their own) is an application-design decision.
- **Bot**: `AvailabilityModule` gains the subcommand; a pure `FreeSlotsEmbedBuilder` renders it. The
  role path reuses the member download and the 50 cap; the member path checks one id.
- **Reply layout** (single member; a role adds the notes below the days):

  ```text
  Free slots for @Sam
  🗓️ Thu Nov 12 – Wed Nov 18 · 60 min or longer
  Inside Sam's available hours, with nothing on their linked calendar.

  Thu Nov 12 · 6:00 PM–8:00 PM
  Fri Nov 13 · 6:00 PM–11:00 PM
  Sat Nov 14 · 10:00 AM–2:00 PM · 5:00 PM–8:00 PM
  Sun Nov 15 · nothing free
  …
  Days are in America/Chicago · times show in your local time

  🕒 Hours only, no calendar linked: @Ana
  ℹ️ No hours set, 8 AM–11 PM assumed: @Bo
  ⚪ Not counted — no hours and no calendar: @Cy
  ```

  Day labels are literal text in the bot's existing `ddd MMM d` style; times are Discord timestamps.
- **No web view of slots, no new configuration.**
- **Delivery**: proposed at workflow planning as two units, each releasable on its own — slots first
  (every member on the default hours), then the hours setting and the "hours only" state.
- **Release**: `feat` → minor versions from 0.37.0; rollout by the standing backup-then-upgrade
  procedure.

## 6. Out of scope (candidates for follow-up issues)

- Caller-side hour filters on the command; several ranges per weekday (Q6 D); overnight ranges.
- Showing a member's hours on the existing grids ("outside their hours").
- Posting a slots list in the channel (Q5 B), or a board the bot keeps current in the Live List style.
- Ranked partial availability — "when are most of us free".
- Week-by-week buttons or a month view (Q4 C, D).
- Starting an event from a slot; slots for an event's attending list.
- A web slots view; calendars other than `primary`.

## 7. Risks

- **Scope.** Q3 B roughly doubles the change: a stored setting with two surfaces, a migration and a
  privacy edit. Two units keep each review small — the longest review loops in this repo were on its
  largest PRs.
- **The default can mislead.** A linked member who never set hours is assumed available 8 AM–11 PM.
  The reply names them, and the docs say to set hours.
- **Hours without a personal timezone** follow the server the command runs in, so the same stored
  hours can mean different instants in two servers. The save confirmation says which zone applies and
  points at `/settings timezone`.
- **New stored personal data.** Hours are a preference the member enters themself, visible to others
  only through their effect on slots; both privacy-policy copies name it.
- **Range limit at Google is undocumented.** Fourteen days is one request per member and well inside
  what scheduling tools ask for; confirm on the live pass. A rejection would surface as the existing
  "couldn't check right now" status rather than a failure.
- **Two weeks of a member's free/busy in one reply.** The same data any member can already get from
  `/availability role` window by window (its `duration` has no upper bound); the reply stays
  ephemeral. Worth a line in the PR body.
- **Larger roles**: up to 50 Google calls per command, as today — not multiplied by the days.
- **Embed size**: bounded by the per-day cap and pinned by a worst-case test.
- **Two clocks**: days are cut in the caller's CalCrony zone while Discord renders times in the
  device's zone. When they differ a slot can appear under a neighbouring day's label; the footer names
  the zone. The same mismatch already exists wherever `when:` is parsed.
