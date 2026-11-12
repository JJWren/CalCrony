# Free slots — unit 1 (`free-slots`): functional design and code-generation plan

**Feature**: free slots and available hours (asked for in the community Discord on 2026-11-10;
tracked in #171). **Inputs**: the approved requirements
(`inception/requirements/availability-free-slots-requirements.md`, FR1–FR18), workflow plan and
application design (`inception/application-design/availability-free-slots-design.md`, D1–D10).
**Baseline**: `master` 4911a1f (v0.36.3; the audit trail records 810 tests green at that release —
re-run as step 0.1). **Branch**: `feature/availability-free-slots` → PR
`feat: free slots for a member or role (/availability slots)`.
**Status**: approved by Joshua 2026-11-11, as written; code generated and approved at the completion gate the same day (section 9 records what differs from the plan); delivery under way (section 8). Amended 2026-11-12 for D11, Joshua's decision on who can be named in `who:` — sections 1, 2, 4.6 and 4.8 are changed in place, and section 9 records it.

As the workflow plan set out, this file is the unit's functional design (sections 1–5) followed by
its code-generation plan (sections 6–8), approved together. Unit 2 (`available-hours`) gets its own
file after this unit merges.

## 1. What this unit ships

`/availability slots who [when] [days] [duration]`: an ephemeral reply that lists, day by day, when
the caller — or everyone holding a role — is free. In this unit every counted member is on the
default hours, 8 AM–11 PM in their own zone, and a member is counted only when their linked calendar
could be read. The stored hours setting and the "hours only" state are unit 2, and so is naming
another member, who must have saved their hours first (D11; as approved, the first sentence
read "when a member — or everyone holding a role — is free").

No migration, no configuration, no new Google scope, Discord permission or intent. Nothing from
Google is stored (FR16). `/availability role` and `/availability event` are not touched (FR8).

Requirements covered: FR1, FR2, FR4–FR9, FR13, FR16; FR3 without "hours only"; the slots half of
FR17 and FR18; FR19 as it applies before hours exist.

## 2. Carried in (locked)

| From | Decision |
|---|---|
| Q1, Q2, Q4, Q5 | A new subcommand beside `role` and `event`; `who:` takes a member or a role; `days:` 1–14, default 7; the reply is ephemeral. |
| D11 (added 2026-11-12, FR19) | A member other than the caller can be named only once they have saved their own hours. No hours are saved before unit 2, so here the member in `who:` is the caller, and anyone else is refused before any lookup. A role is as it was. |
| Q3, Q6, Q7 | Stored per-member hours, their shape, and "hours only" — unit 2. Here everyone counted gets the default week. |
| Assumptions | A slot needs every counted member free for all of it; `duration:` is the shortest slot listed, 15–1440 minutes, default 60; the time of day in `when` is ignored; the route is bot-only; no web view. |
| D4 | The calculation takes, per member, a zone, a week of hours and busy blocks. It and its tests are final in this unit. |
| D5 | The lookup reaches one day past the last requested day; a slot is kept when it starts inside the range. |
| D8 | Limits live once, in Contracts. |
| D9 | A pure member-zone helper for the new code; the datetime route keeps its inline copy; a test pins that both agree. |

## 3. Refinements made here

Details the design left to this stage, plus the places this plan moves something. Each can be
overruled.

| # | Refinement | Why |
|---|---|---|
| R1 | A range in `when` that has begun and not ended — `this week`, `this month` — resolves to today; one that hasn't begun resolves to its first day. | The request's own words were "this week, this month". The recognizer gives `this week` as a range that started on Monday, which a plain "first day" rule would refuse as past. |
| R2 | The reply's range line carries the year. | A bare date already behind us (`nov 5` typed on Nov 11) rolls to next year, as every `when:` in the bot does. The year makes that visible instead of silently listing 2027. |
| R3 | A single target must be a member of this server and not a bot; a role's bots are left out. | A user option can carry someone who is not in the server (Discord resolves a pasted id). Without the check, a member could list the free time of a CalCrony user from outside it — the privacy policy says free/busy is shown to members of your server. |
| R4 | `AvailableHoursLimits` enters Contracts in this unit with the two default values (8 AM, 11 PM). Its step size stays in unit 2. | The default week (API) and the reply's sentence (bot) both need the numbers now, and D8 wants them in one place. |
| R5 | The bot's `TimeOfDayText` enters in this unit with formatting only ("8 AM", "6:30 PM"). Parsing typed times stays in unit 2. | The reply's sentence is built from R4's values rather than repeating them as text. |
| R6 | `WeeklyHours` has a public constructor that rejects a range outside 0–1440 or not start-before-end. | D4: the calculator's tests need weeks other than the default (a day off, a range ending at midnight) for the calculation to be final here. The 30-minute grid is input validation and stays in unit 2. |
| R7 | A slot lasting 24 hours or more shows its end with the date. | It cannot happen on the default hours (no slot here exceeds 15 hours), but unit 2's hours can produce one and the renderer is final in this unit. |
| R8 | A test builds `/availability slots` through Discord.Net's own command builder, offline. | The design put "the registered command tree" under unit 2. It works without a gateway connection (tried), so the new command's option types and bounds are pinned now. |
| R9 | The member-or-role picker comes off the live-pass-only list. | `/notify mention:` has used the same option type since v0.1.0. What only the live pass can show: a 15-day free/busy query on a real Google account, and the reply at phone width. |
| R10 | `CONTEXT.md` gains three terms: Free Slot, Counted Member, Available Hours. | The design's docs table did not list the glossary. Earlier features added their terms to it, and these three are used throughout this unit's code and replies. |

## 4. Rules

### 4.1 The first day

`NaturalDateTimeParser.TryResolveDay(text, zone, out day, out error, referenceInstant)` — a new
method beside `TryResolve`, which is not changed.

Rules 2 to 4 were amended during generation, after the independent review (section 9). As approved,
every recognizer value was a candidate and the earliest one today or later won, so a today that was
only implied — by a detached time or a range under way — beat a day the text named.

1. "Today" is the local date of one captured `now` in the caller's zone.
2. Each phrase the recognizer finds is read on its own. A date names that day; a date with a time
   names its date; a range names its first day; a time with no date names no day. A range that
   began before today and has not ended is *under way*. Its last day is the day before the
   recognizer's end for a calendar unit (`this week` ends, exclusively, on next Monday), the end
   itself for a range the text spells out (`nov 11 to nov 13`), and the date of a date-and-time end.
   A spelled-out range is told apart by its timex: in parentheses, with a length counted in days.
   A quarter is in parentheses too, but its length is in months, and it is a calendar unit —
   narrowed in review round 1 (section 9), when `last quarter` on October 1 read as today.
3. The recognizer offers several readings of a phrase whose year or week is open (`saturday`,
   `nov 5`, `nov 11 to nov 13`). They are alternatives for that phrase: if one of them is a range
   under way, the phrase means today; otherwise it names the earliest of its days that is today or
   later, or — when none is — only days that have passed.
4. Across phrases: the earliest named day that is today or later wins. Failing that, a range under
   way means today. Failing that, a phrase naming only passed days is refused:
   `"{text}" is a day that has already passed.` Failing that, a time on its own means today.
   Nothing recognized → `Could not understand "{text}" as a day. Try "today", "saturday" or "nov 12".`
5. A zone abbreviation in the text does not move the day: days are always the caller's (FR4).
   `TryResolve` switches zone on one; this method does not.
6. Text over 100 characters (`DateTextLimits.MaxLength`) is refused before it is read, with
   `TryResolve`'s refusal: `The date/time text must be at most 100 characters.` Added when
   `master` was merged on 2026-11-12 (section 9), which brought that limit to `TryResolve`.

Run against the real recognizer (a scratch build of the repository's parser file), Wednesday
2026-11-11, 10:00 in America/Chicago; the same answers at 23:30:

| Input | The recognizer returns | Day |
|---|---|---|
| `today`, `now`, `tonight`, `6pm`, `9am` | today's date, a moment or a stretch of today, or a time with no date | Wed Nov 11 — the existing `TryResolve` refuses `today` after 9 AM |
| `wednesday` | last Wednesday and today | Wed Nov 11 |
| `saturday`, `nov 12`, `nov 12 8am`, `2026-11-14`, `in 3 days` | a date, with or without a time | that date |
| `this week`, `this month` | a range that began before today | Wed Nov 11 (R1) |
| `next week`, `this weekend`, `december` | a range that hasn't begun | its first day |
| `nov 12 CST`, `today UTC` | the date; the abbreviation is ignored | the caller's Nov 12; the caller's today |
| `yesterday`, `last friday`, `last week`, `nov 5 2026` | past days only | refused: already passed |
| `nov 5` | Nov 5 of this year and of next | Nov 5, 2027 (R2) |
| `sat`, `banana hammock` | nothing | refused: not understood |
| `friday at night`, `friday (6pm)`, `tomorrow, after 6pm` | a day, and separately a time with no date | the named day — the time alone would be today |
| `friday, this week`, `nov 20 this year` | a day, and separately a range under way | the named day |
| `yesterday (6pm)` | a passed day, and separately a time | refused: already passed |
| `nov 11 to nov 13`, `monday to friday` — asked on Friday Nov 13 | a spelled-out range ending today | Fri Nov 13; read with an exclusive end it rolled to 2027 or was refused |

Known and left as they are: the recognizer reads `mon` to `fri` as weekdays but not `sat` or `sun`,
so `sat` is refused with the hint and `sat 6pm` reads as today (the time alone) — true of every
`when:` in the bot; `tuesday, next week` gives Monday, the earlier of the two days named, as
`TryResolve` does; `past 3 days`, which has the shape of a spelled-out range ending today, reads as
today.

### 4.2 The route

`POST /calendar/free-slots`, bot-only for the reason `/calendar/availability` is: it accepts
arbitrary user ids. `FreeSlotsRequest` → `FreeSlotsResponse`, or 400 with an `ErrorResponse`.

The endpoint validates, in this order:

| Check | 400 text |
|---|---|
| distinct `UserIds`: 1 to 50 | `userIds must have between 1 and 50 distinct entries.` (the existing route's wording) |
| `Days`: 1 to 14 | `days must be between 1 and 14.` |
| `MinimumMinutes`: 15 to 1440 | `minimumMinutes must be between 15 and 1440.` |
| `When`: at most 100 characters (`DateTextLimits.MaxLength`, the limit every date typed in words has; this unit's own `FreeSlotLimits.MaxWhenLength` until `master` was merged on 2026-11-12, section 9) | `when must be at most 100 characters.` — added in review round 1 (section 9); checked before the text is read. Since that merge `TryResolveDay` refuses longer text as well. |

`FreeSlotsService.GetAsync` then, with one clock read used throughout:

1. Loads the server's timezone and the profiles of the members and the caller — two read-only
   queries, no row created.
2. Caller's zone = `MemberZone.Resolve(caller's timezone, server's timezone)`.
3. First day = today in that zone when `When` is null or blank, else `TryResolveDay`; its error
   becomes the 400. A first day more than 366 days ahead (`FreeSlotLimits.MaxDaysAhead`) is refused:
   `"{text}" is more than a year away; free slots can be listed up to a year ahead.` — added during
   generation (section 9): natural language reaches any year, and near year 9999 the day arithmetic
   overflowed into a 500.
4. Asks `FreeSlotCalculator.QueryWindow` for the window and makes **one**
   `CalendarAvailabilityService.CheckAsync` call for it — one Google query per linked member (FR7).
5. Maps each member to a state (4.4), runs the calculation over the counted ones, builds the
   response.

### 4.3 Zones

`MemberZone.Resolve(memberTimeZone, guildTimeZone)`: the member's IANA id when it resolves, else
the server's, else UTC. A stored id that no longer resolves falls through; a server with no row
reads as UTC. It is the expression `EventEndpoints.ResolveZoneAsync` already uses for `/create`,
polls and reminders; that method loads one profile per call and wants a tracked `Guild`, where
slots resolve up to 51 zones from one query, so it is left alone, as D9 leaves the datetime route.

### 4.4 Who is counted

| Availability check says | State returned | Counted | Hours used |
|---|---|---|---|
| `Free` or `Busy` | `DefaultHours` | yes | 8 AM–11 PM every day, in the member's zone |
| `NotConnected` | `NotCounted` | no | — |
| `ReconnectRequired` | `ReconnectRequired` | no | — |
| `Error` | `Error` | no | — |

`Counted` and `HoursOnly` are defined in the enum and never returned by this unit. `Members` lists
every distinct requested id once, in request order. When nobody is counted, `CountedMembers` is 0
and `Days` is empty.

### 4.5 The calculation

`FreeSlotCalculator`, pure: no clock, no database.

1. **Range.** `rangeStart` is the later of the first day's midnight in the caller's zone and `now`
   rounded up to the next quarter hour (an exact quarter hour stays). `lastStart` is the midnight
   that ends the last requested day. `windowEnd` is one day after `lastStart` (D5).
   `QueryWindow` returns `rangeStart` and `windowEnd`: what Google is asked for and what is computed.
2. **A member's hours.** For each local date in the member's zone from `rangeStart`'s to
   `windowEnd`'s, the weekday's range, if it has one, runs from `date + StartMinute` to
   `date + EndMinute` (1440 is the next day's midnight). Local times map leniently: one skipped by
   daylight saving moves forward by the gap, a repeated one takes its first occurrence — so a 23- or
   25-hour day keeps its wall-clock hours. Intervals are clipped to the window; touching ones join,
   so hours that run to midnight and resume at midnight are one stretch.
3. **Busy blocks.** A block that ends at or before its start is ignored. The rest are sorted,
   merged, clipped to the window and subtracted — Google does not promise clipped or merged blocks.
4. **Everyone at once.** The members' lists are intersected. Each list stays sorted, disjoint and
   merged.
5. **Slots.** An interval is a slot when it starts before `lastStart` and is at least the minimum
   long. It belongs to the caller's local date of its start. Every requested day is returned in
   order, with its slots in order or with none. A day that begins inside a slot listed under an
   earlier day also carries the instant that slot ends (`OpenUntil`) — added in review round 1
   (section 9), so that such a day is not reported as having nothing free. A slot that starts
   exactly at the day's first instant is the day's own; one that ends exactly then is over.
6. `Compute` with no members returns every day empty; the service never calls it that way.

What follows from these: today's first slot starts at the rounded `now`, and what is left of a
stretch is dropped if it is shorter than the minimum (FR2); in the last minutes of a one-day
request `rangeStart` can equal `lastStart`, and the day is listed with nothing free; a member in another
zone shifts the hours the caller sees, so a slot can run past the caller's midnight — it stays one
slot, under the day it starts on (FR4), including on the last requested day (D5), and the day it
runs into says until when it is free.

### 4.6 The command

| Option | Type | Required | Bounds | Description in Discord |
|---|---|---|---|---|
| `who` | member or role (the mentionable picker) | yes | — | `Yourself or a role` (D11; `A member or a role` as approved) |
| `when` | text | no | at most 100 characters (review round 1); default: today | `First day, e.g. "today", "saturday", "nov 12" (default: today)` |
| `days` | integer | no | 1–14, default 7 | `How many days to list, 1-14 (default 7)` |
| `duration` | integer | no | 15–1440, default 60 | `Shortest slot worth listing, in minutes (default 60)` |

Subcommand description: `Find free time slots for yourself or a role over one or more days` (D11;
`…for a member or role…` as approved). Bounds come from `FreeSlotLimits`, so Discord enforces them
before the command runs. Any member may run it, as with `role` and `event`. A test pins both
descriptions, so that they move together with the rule in unit 2.

Resolving `who`, after the ephemeral defer:

| `who` is | Members asked about | Title subject | Refusal |
|---|---|---|---|
| a role | its holders that are not bots, through the member download `role` already uses | `@{role name}` | nobody holds it → `Nobody currently has the {role mention} role.`; only bots do → `Only bots have the {role mention} role, and bots don't keep a calendar.` (review round 1); more than 50 → `The {role mention} role has {n} members; free-slot lists support at most 50 at a time.` |
| the caller | that member | `@{display name}` — one at sign, as for a role, when the name already starts with one (review round 1) | — |
| a bot | — | — | `Bots don't keep a calendar — pick yourself or a role.` |
| a user who is not in this server | — | — | `That user isn't a member of this server.` (R3) |
| another member of this server | — | — | `Another member's free slots can't be listed yet — pick yourself or a role.` (D11) |

The three refusals for one user are checked in that order — a bot, a user from outside the
server, another member — so the first two are told the more specific reason, and the caller is
what is left. The picker still offers every member: Discord has no option type for "me or a
role", and unit 2 needs the member choice. As approved, the caller's row read "a member of this
server", there was no row for another member, and the bot refusal ended "pick a member or a
role".

Every refusal is ephemeral. A refusal from the API prints as `❌ {error}`, as on the other
subcommands.

### 4.7 The reply

```text
Free slots for @GM
🗓️ Thu Nov 12 – Wed Nov 18, 2026 · 60 min or longer
When all 3 counted members are free on their linked calendars, each between 8 AM and 11 PM in their own timezone (the server's for anyone who hasn't set one).

**Thu Nov 12** · 6:00 PM–8:00 PM
**Fri Nov 13** · 6:00 PM–11:00 PM
**Sat Nov 14** · 10:00 AM–2:00 PM · 5:00 PM–8:00 PM
**Sun Nov 15** · nothing free
(one line per requested day)

Not counted
⚪ No calendar linked — @Cy
🔄 Needs to reconnect with `/calendar connect` — @Di

Days are in America/Chicago · times show in your local time
```

- **Title**: `Free slots for {subject}` — plain text, since mention markup does not render in titles.
- **Range line**: first and last day in the bot's `ddd MMM d` style and the last day's year; one
  day alone when `days` is 1; both years when the range crosses New Year
  (`Tue Dec 29, 2026 – Mon Jan 4, 2027`). Then `{n} min or longer`.
- **Basis sentence**, with the hours formatted from `AvailableHoursLimits`:

  | Case | Sentence |
  |---|---|
  | one member asked about, counted | `Free on their linked calendar, between 8 AM and 11 PM in their timezone (the server's if they haven't set one).` |
  | several asked about, two or more counted | `When all {n} counted members are free on their linked calendars, each between 8 AM and 11 PM in their own timezone (the server's for anyone who hasn't set one).` |
  | several asked about, one counted | `When the one counted member is free on their linked calendar, between 8 AM and 11 PM in their timezone (the server's if they haven't set one).` |
  | nobody counted, none of them with a calendar | `Nobody could be counted, so there are no slots to list. A member is counted once they link a calendar with /calendar connect.` (the command in code style) — and no day lines |
  | nobody counted, at least one for another reason | `Nobody could be counted, so there are no slots to list. The reasons are listed below.` — and no day lines |

  The zone fallback in the first three, and the second "nobody counted" sentence, were added in
  review round 1 (section 9). This is how unit 1 meets FR13's "names the members the default was
  applied to": the default applies to every counted member, and the sentence says so. Unit 2
  names them one by one, once some members have hours of their own.
- **Day lines**: the day in bold, then its slots joined by ` · `, each `<t:start:t>–<t:end:t>` — a
  Discord timestamp, shown in the reader's local time — or `nothing free`. At most six slots, then
  `+N more` (FR6). A slot of 24 hours or more ends with `<t:end:f>`, which carries the date (R7).
  A day with no slot of its own that begins inside one listed under an earlier day reads
  `free until <t:end:t> (started Thu)` instead of `nothing free`, with the same rule for the
  end's style (review round 1). A day with slots of its own lists only those.
- **Closing line**, only when no day has a slot and the minimum is above the smallest allowed:
  ``Nothing {n} min or longer is free in this range — a shorter `duration` may find something.``
  (review round 1).
- **Field "Not counted"**, only when someone is not: one line per reason that has members, in this
  order — `⚪ No calendar linked`, ``🔄 Needs to reconnect with `/calendar connect` ``,
  `⚠️ Couldn't check right now` — then ` — ` and the members as mentions. The emoji match the
  existing grid's. When the field's 1,024 characters hold every line in full, everyone is named.
  When they do not, the lines share the field: shortest first, each keeps its full length while
  that is within an equal share of the room left, and a line that is cut ends with `+N more`
  (review round 1 — the shares were equal before, which cut a long list while a short one left
  most of the field empty).
- **Footer**: `Days are in {zone id} · times show in your local time`.
- **Colour**: the existing availability embed's.
- **Budget**: fourteen days at six slots each with `+N more`, the longest range line and sentence
  come to about 3,600 characters of description against Discord's 4,096; with the longest title,
  a full field and the footer the embed is about 4,750 against 6,000. A test builds that case, and
  Discord.Net itself refuses to build an embed over either limit.

### 4.8 Docs copy

| Where | Text |
|---|---|
| `README.md` Features, the bullet at line 42 | ``- Google Calendar, free/busy scope only (`/availability`): grids for a role or event, free slots for a member or role.`` — the section goes from 394 to 398 words by `awk '/^## Features/{f=1;next} /^## Commands/{f=0} f' README.md \| wc -w`, cap 400. |
| `README.md` command table, a row after `/availability event` | ``/availability slots who [when days duration]`` — `Free slots for a member or a role over 1–14 days (default 7, from today): times every counted member is free, 8 AM–11 PM in each member's timezone. Only you see the reply` |
| `Docs.razor`, the "Google Calendar availability" entry | After "Scheduling-Assistant style.": `To find a time instead of checking one, /availability slots who:@dnd (Discord only; any member can run it, and only they see the reply) lists when a member — or everyone holding a role — is free over the next 1 to 14 days: when picks the first day, days how many (7 by default) and duration the shortest slot worth listing (60 minutes by default). A slot is time with nothing on the linked calendar, between 8 AM and 11 PM in each member's own timezone; members who haven't linked a calendar aren't counted, and the reply names them.` The entry's last sentence ("Read-only…") stays last. |
| `Docs.razor` command table, line 341 | ``/availability role\|event\|slots`` — `Free/busy grid for a role or an event's Going list · free slots for a member or role over 1–14 days` |
| `PRIVACY_POLICY.md:48`, `Privacy.razor:70` | "render availability grids" → "render availability grids and free-slot lists"; both "Last updated" dates → the day the PR opens. |
| `CONTEXT.md` | Three terms: **Free Slot** (a stretch in which every Counted Member is inside their Available Hours with nothing on a linked calendar; computed live, never stored), **Counted Member** (a member whose availability shaped a free-slots answer; everyone else asked about is named with the reason), **Available Hours** (the weekly wall-clock hours, read in the member's own zone, inside which a Free Slot may fall; 8 AM–11 PM every day by default). |
| `live-e2e-script.md` §12 | New items: the command appears with the member-or-role picker; a linked member's slots match their Google calendar inside 8 AM–11 PM; `when:today` late in the day works and the first slot starts on the quarter hour; a role with a linked and an unlinked member lists the unlinked one under Not counted; a member with no calendar → "Nobody could be counted"; `days:14` on a real Google account returns slots rather than "couldn't check"; `when:yesterday` and `when:banana` refuse politely; a user id from outside the server is refused; the reply reads at phone width; `role` and `event` still behave as before. |

Changed for D11 (2026-11-12), with the rows above left as approved: both README lines and the Docs
command row say "yourself or a role" where they said "a member or (a) role"; the Docs entry reads
"lists when everyone holding a role is free over 1 to 14 days; pick yourself as `who` for your
own."; and in the live script the single-member items are run as yourself, the item with a caller
many hours away goes through a role held by that one member, and a new item checks that naming
another member is refused.

Left as they are: the worked examples and the interest-roles paragraph (they use `/availability
role`, still correct); `/help`'s tagline; the Terms; the web app's feature list (slots have no web
view).

## 5. What is added, file by file

| File | Change |
|---|---|
| `src/CalCrony.Contracts/Calendar.cs` | `FreeSlotLimits`; `AvailableHoursLimits` with `DefaultStartMinute` 480 and `DefaultEndMinute` 1380 (R4); `FreeSlotsRequest`; `FreeSlotMemberState` (all six values); `FreeSlotMemberDto`; `FreeSlotDto` with `StartUnix`/`EndUnix` as on `BusyBlockDto`; `FreeSlotDayDto`; `FreeSlotsResponse` — signatures as in design §3, XML docs in the file's style. |
| `src/CalCrony.Api/Services/NaturalDateTimeParser.cs` | `TryResolveDay` and two private helpers (4.1). |
| `src/CalCrony.Api/Services/WeeklyHours.cs` (new) | The week: constructor (R6), `Default`, `RangeFor(IsoDayOfWeek)`. |
| `src/CalCrony.Api/Services/MemberZone.cs` (new) | `Resolve` (4.3). |
| `src/CalCrony.Api/Services/FreeSlotCalculator.cs` (new) | `FreeSlotQuery`, `MemberAvailability`, `FreeSlotDay`, `QueryWindow`, `Compute` (4.5) — signatures as in design §4.2. |
| `src/CalCrony.Api/Services/FreeSlotsService.cs` (new) | `GetAsync` and `FreeSlotsOutcome` (4.2, 4.4). |
| `src/CalCrony.Api/Endpoints/CalendarEndpoints.cs` | The route and its handler with the three checks. |
| `src/CalCrony.Api/Program.cs` | `AddScoped<FreeSlotsService>()`. |
| `src/CalCrony.Bot/Api/CalCronyApiClient.cs` | `GetFreeSlotsAsync`. |
| `src/CalCrony.Bot/TimeOfDayText.cs` (new) | `Format(minutes)`: `8 AM`, `6:30 PM`, `12 PM`, `12 AM`; 1440 is `midnight` (R5). |
| `src/CalCrony.Bot/FreeSlotsEmbedBuilder.cs` (new) | `Build(subject, minimumMinutes, response)` (4.7). |
| `src/CalCrony.Bot/AvailabilityEmbedBuilder.cs` | `AvailabilityColor` becomes `internal` so the new builder shares it. Nothing else. |
| `src/CalCrony.Bot/Modules/AvailabilityModule.cs` | `SlotsAsync` (4.6); the class summary names slots. |
| Docs | The seven sites of 4.8. |
| Tests | Section 7, steps 2.x–5.x. |

## 6. Verified code facts (`master` 4911a1f, 2026-11-11)

- `Calendar.cs` is 60 lines and ends with `AvailabilityResponse`; `BusyBlockDto` (38–42) is the
  precedent for computed Unix helpers on a wire record. Contracts has no package references, and
  no `DateOnly` on the wire yet — System.Text.Json writes it natively as `2026-11-12`.
- `Program.cs`: JSON options add the NodaTime converters only (20–21), so enums cross as numbers on
  both sides; `IClock` 29; the parser is a singleton 31; `AddScoped<CalendarAvailabilityService>()`
  45; the `BotOnly` policy 93; `MapCalendarEndpoints()` 178.
- `CalendarEndpoints.cs`: `MaxUsersPerQuery` 17; routes 21–30, the bot-only route and its comment
  26–28; `CheckAvailability` 159–181 — de-duplicates first (166), the id guard and its wording
  167–170.
- `CalendarAvailabilityService.CheckAsync` (35–71) returns one row per requested id in request
  order (66–70), saves refreshed tokens itself (60–63), and classifies in `CheckOneAsync` (80–120):
  refresh rejected → `ReconnectRequired`, refresh or query failure → `Error`.
  `GoogleCalendarProvider.GetFreeBusyAsync` (112–155) turns any non-success from Google into that
  `Error`, so a range Google refused would show as "couldn't check", logged with status and body.
- `NaturalDateTimeParser.cs`: `sealed partial class … (IClock clock)` 17; `LocalPattern` 19–20;
  `model` 52–53; `TryResolve` 70–125 (reference 87–89, recognizer loop 92–105, past filter 115–121);
  `ResolveValue` 132–169 (range start 141, bare date at 09:00 154–158, bare time 161–168). The
  note at 55–60 — never name a method `TryParse` here, minimal APIs would bind the type — is why
  the new one is `TryResolveDay`.
- `EventEndpoints.cs`: `ParseDateTime` 1757–1811, its zone branch 1773–1800, and the response
  carries the zone id (1808–1810) — what the D9 pin test compares; `ResolveZoneAsync` 1858–1863,
  called from `EventEndpoints.cs:414`, `PollEndpoints.cs:97`, `DeliveryEndpoints.cs:153`.
  `Mapping.FindZone` is `Mapping.cs:139`.
- `Entities.cs`: `Guild.TimeZone` is non-null, default `"UTC"` (10); `UserProfile` 87–121, key
  `Id` 89, `TimeZone` 90.
- `AvailabilityModule.cs`: module attributes 10–13 (the module-context pin test covers the new
  subcommand through them); `RoleAsync` 22–64 — defer 28, member download 32–36, the two refusals
  38–50, the plain-name subject 61–63.
- `AvailabilityEmbedBuilder.cs`: the colour 9 (private today); the status emoji 33–40.
- `CalCronyApiClient.cs`: `CheckAvailabilityAsync` 488–489; `SendAsync` 524–563 surfaces
  `ErrorResponse.Error` as the display text.
- Option precedents: `IMentionable` on `NotifyModule.cs:26`; `MinValue`/`MaxValue` on
  `EventModule.cs:69`. The bot formats dates as `ddd MMM d` in `PollEmbedBuilder.cs:87` and
  `EventNameAutocompleteHandler.cs:68`.
- Tests: `ApiFixture` runs the API on the real clock against a Postgres container, so route tests
  ask for a concrete day a few days ahead rather than faking time; `CalendarApiFixture` swaps in
  `FakeCalendarProvider`, whose `GetFreeBusyAsync` (51–55) ignores its window — it gains a record
  of calls; `CalendarAvailabilityTests.SeedConnectionAsync` (116–132) is the seeding pattern;
  `NaturalDateTimeParserTests` uses a `FakeClock` at Wednesday 2026-07-15 10:00 Chicago (10);
  `WebAuthorizationTests.Generic_availability_is_bot_only` (92–101) is the bot-only pattern;
  `DocsPageTests.Entry` (83–84) scopes an assertion to one feature entry; `ComponentTests`
  (105–116) renders the privacy page. The bot tests have no mocking library — logic worth testing
  lives in pure helpers, as here.
- Docs: `README.md` 42 (bullet), 88–89 (rows); `Docs.razor` 259–265 (entry), 341 (row);
  `PRIVACY_POLICY.md` 3 and 48–49; `Privacy.razor` 8 and 70; `live-e2e-script.md` 203–216 (§12).
- Checked by running, not by reading: the recognizer's answers in 4.1; that
  `new InteractionService(new DiscordSocketClient())` plus `AddModuleAsync(typeof(AvailabilityModule), …)`
  builds the command tree offline and exposes each option's type, bounds and description; the README
  word count.

## 7. Execution plan

Layers run top to bottom. Each layer builds and its tests pass before the next starts. A box is
ticked in the turn its step completes.

**0. Baseline**
- [x] 0.1 Branch `feature/availability-free-slots` from `master`; `dotnet build CalCrony.slnx` and
      `dotnet test CalCrony.slnx` green before the first change; record the count. — 810 green
      (510 API, 171 bot, 129 web), the number the audit trail records for v0.36.3; the build has 22
      warnings, all in existing test code.

**1. Contracts**
- [x] 1.1 `Calendar.cs`: the types of section 5, documented. `FreeSlotMemberState`'s summary says
      which values this release returns.

**2. API — pure pieces, each with its tests**
- [x] 2.1 `TryResolveDay` (4.1). `NaturalDateTimeParserTests`: the table of 4.1 at the fixture's
      Wednesday — `today` at 10:00 and at 23:30, `wednesday`, `saturday`, `tomorrow`, `this week`,
      `this month`, `next week`, a date, a date with a time, a bare time already past, `in 3 days`,
      a zone abbreviation ignored, `yesterday` and an explicit past date refused, a bare past date
      rolling to next year, nonsense refused; and `TryResolve` still refusing `today` after 9 AM.
- [x] 2.2 `WeeklyHours` (R6). `WeeklyHoursTests`: the default is 8 AM–11 PM on all seven days and
      comes from `AvailableHoursLimits`; a day without a range answers null; the constructor refuses
      a negative start, an end past 1440 and a start not before its end.
- [x] 2.3 `MemberZone` (4.3). `MemberZoneTests`: the member's zone wins; an unknown member id falls
      to the server's; neither → UTC.
- [x] 2.4 `FreeSlotCalculator` (4.5). `FreeSlotCalculatorTests`:
      one member, no busy blocks → 8 AM–11 PM on every day, in their zone;
      a block splits a day; overlapping, touching, unsorted and out-of-window blocks; an empty or
      inverted block ignored;
      the minimum: a shorter gap dropped, an exact one kept;
      `now` at 10:07 → the first slot starts 10:15; at 10:15 → 10:15; a remainder shorter than the
      minimum dropped; `now` after hours → today listed with nothing; 23:50 with one day asked;
      two members → the overlap; three members in Chicago, Berlin and Tokyo;
      days are the caller's: a Los Angeles member seen from New York has slots crossing midnight,
      listed under the day they start on, and the last day's slot keeps its real end (D5) while one
      starting after the last midnight is not listed;
      the first day's midnight cuts a stretch already under way and lists what is left;
      daylight saving in America/Chicago (2026-03-08 and 2026-11-01): 8 AM–11 PM stays fifteen
      hours, and a midnight–6 AM range is five and seven hours long;
      custom weeks: a day off; hours to midnight meeting hours from midnight are one slot;
      no members → every day empty;
      `QueryWindow` for today and for a future first day.

**3. API — service and route**
- [x] 3.1 `FreeSlotsService` and `FreeSlotsOutcome` (4.2, 4.4); registered in `Program.cs`.
- [x] 3.2 The route in `CalendarEndpoints` with the three checks, beside the existing bot-only route
      and with the same comment on why.
- [x] 3.3 `FakeCalendarProvider` records each free/busy call (token, start, end).
      `FreeSlotsApiTests` on `CalendarApiFixture`, each asking for a day three days ahead in the
      zone under test:
      a linked member with two blocks → three slots at the expected instants; the day list, zone,
      first and last day and counted number are right;
      linked, unlinked, reconnect-required and failed-refresh members together → the four states,
      slots shaped by the linked one alone;
      nobody counted → no days, every member listed;
      two linked members with different timezones → the overlap;
      zone pin (D9): a caller with a timezone, a caller without one in a server with one, neither —
      the zone returned equals `POST /tools/parse-datetime`'s for the same caller and server;
      `when` omitted and `when:"today"` → the first day is today in the caller's zone;
      refusals: `yesterday`, nonsense, days 0 and 15, minutes 14 and 1441, no ids, 51 ids;
      duplicate ids answered once;
      exactly one provider call per linked member, from the first midnight to the midnight after
      the extra day (FR7, D5).
      `WebAuthorizationTests`: a signed-in web member gets 403 from the route.

**4. Bot**
- [x] 4.1 `CalCronyApiClient.GetFreeSlotsAsync`.
- [x] 4.2 `TimeOfDayText.Format` (R5). `TimeOfDayTextTests`: 480, 1380, 1110, 0, 720, 1440.
- [x] 4.3 `FreeSlotsEmbedBuilder` (4.7); `AvailabilityColor` internal. `FreeSlotsEmbedBuilderTests`:
      title; range line for one day, several days and across New Year; the four sentences, with the
      hours taken from `AvailableHoursLimits`; slots in order as timestamps; `nothing free`; seven
      slots → six and `+1 more`; a 30-hour slot ends with the dated form; the field's three lines
      and their order; no field when everyone is counted; nobody counted → no day lines; fifty
      members not counted stay inside 1,024 characters with `+N more`; the worst case of 4.7 builds,
      under 4,096 and 6,000; the footer names the zone.
- [x] 4.4 `AvailabilityModule.SlotsAsync` (4.6) and the class summary.
      `AvailabilityCommandShapeTests` (R8): the module builds; `slots` has `who` as a required
      mentionable, `when` optional text, `days` 1–14 and `duration` 15–1440 from `FreeSlotLimits`;
      `role` and `event` keep their options; every description in the group is at most 100
      characters.

**5. Docs**
- [x] 5.1 `README.md`: the bullet and the row (4.8); the word count re-run and at most 400.
- [x] 5.2 `Docs.razor`: the entry and the row (`@@dnd` in Razor, as the page writes it today).
      `DocsPageTests`: the "Google Calendar availability" entry names `/availability slots`,
      "Discord only" and the 8 AM–11 PM default.
- [x] 5.3 `PRIVACY_POLICY.md` and `Privacy.razor`: the Limited Use sentence and the date, the same
      in both. `ComponentTests`: the privacy page says "free-slot lists".
- [x] 5.4 `CONTEXT.md`: the three terms.
- [x] 5.5 `live-e2e-script.md` §12: the slots items.

**6. Verification**
- [x] 6.1 `dotnet build CalCrony.slnx -c Release` without warnings from the new code;
      `dotnet test CalCrony.slnx` green; counts per project recorded against 0.1.
- [x] 6.2 Read the whole diff against section 4, rule by rule, and against FR1–FR9, FR13, FR16.
      — done, and followed by an independent review; its findings and the fixes are in section 9.
- [x] 6.3 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 8. Delivery

Approving this plan covers creating the branch and committing to it locally as layers complete.
Everything that leaves this machine waits for its own go-ahead, as the workflow plan says.

- [x] 8.1 First commit on the branch: `docs: free slots and available hours — requirements,
      workflow plan, design and the unit 1 plan` (the four documents, `aidlc-state.md`, `audit.md`).
      Code commits follow by layer.
- [x] 8.2 *Go-ahead:* the tracking issue, one box per unit. — #171, filed 2026-11-11 on Joshua's
      "Continue" at the completion gate.
- [x] 8.3 *Go-ahead:* push and open the PR. Body: what it does; the shape of the change; behaviour
      for existing servers (none — a new subcommand); what a caller can now see (up to two weeks of
      a member's free time in one ephemeral reply — the same data `/availability role` gives window
      by window — and only for members of the server); no migration; test counts. — PR #172, opened
      2026-11-11 at head `cd30f84` with `Refs #171`; the body also lists the known limits.
- [ ] 8.4 Copilot rounds until an approval with no open threads. *Go-ahead:* squash merge. The test
      stack picks up `:main`; the new §12 items are run there. — Blocked 2026-11-11: checks green,
      but Copilot's review did not start on opening or on a push, and requesting it by hand
      registered nothing. Joshua's decision the same day: two review agents in Copilot's place —
      one on security, one on code quality — reviewing each commit in a loop until a commit comes
      back clean from both. That review was complete on `9f13116` after five rounds (section 9).
      Joshua then answered the two decisions it had left open (2026-11-12, section 9): D11 is
      implemented in `61d6eda`, and the agents' rounds on it and on the records that answer
      them are in section 9, ending with no findings from either on `c22539c` (round 10); the
      separate piece of work outside this change goes first, in its own session. That work
      reached `master` on 2026-11-12 and `master` is merged into this branch (section 9). Still
      ahead: the agents' round on that merge, then his go-ahead to merge the PR.
- [ ] 8.5 `aidlc-state.md` marks unit 1 merged; unit 2's plan follows. The release waits for unit 2.

**One question before the branch is pushed** (not before code generation starts):

Q8 — `audit.md` quotes the community member's Discord message word for word, as the workflow
requires of raw input. It carries no username, and the repository is public.

- A) Keep it word for word. **(Recommended — the audit rule is "raw input, never summarized", the
  message was posted in a public server, and it names nobody.)**
- B) Replace the quote with a one-line description and a pointer to where it was posted.
- C) Other (describe).

[Answer]: A — taken as the recommended option: Joshua continued on 2026-11-11 without answering, which the presentation said would be read this way. It can still be changed before the branch is pushed.

## 9. Notes from generation

Where the code as built differs from sections 4–7 as approved:

- **`src/CalCrony.Bot/FreeSlotTarget.cs`** is a file the plan did not list. The rules of 4.6 for
  resolving `who` — bots left out of a role, the 50-member limit, a bot or a user from outside the
  server refused — live there as pure functions, and `SlotsAsync` only hands over what Discord
  resolved. The bot tests have no mocking library, so in the module those rules, R3's privacy guard
  among them, could only have been checked on the live pass; `FreeSlotTargetTests` pins them.
- **A seeded cross-check** was added to `FreeSlotCalculatorTests`: 250 random lookups (one to
  three members, random weeks and busy blocks, zones with whole-, half- and quarter-hour offsets)
  are compared with a model that walks the window minute by minute. It covers zones without
  daylight saving only; the two daylight-saving cases keep their own tests. A deliberately broken
  intersection is caught by this test and by no other.
- **`Docs.razor` wording** (4.8) reads "over 1 to 14 days: `when` picks the first day (today by
  default)…" and "…in each member's own timezone (the server's, if they haven't set one)" — the
  default for `when` and the zone fallback were missing from the proposed copy.
- **Privacy dates** are set to 2026-11-11 in both copies; if the PR opens on a later day they move
  with it.
- **A second calculator test of invariants** covers the nights the clocks change, where the
  minute model does not apply: 250 random lookups in zones with a one-hour change, a half-hour
  change (Lord Howe) and offsets on the half and three-quarter hour, each checked for order, no
  overlap or touching, the minimum, the window, the right day, and no busy time inside a slot.

Step 6.2 was done twice: my own read of the diff against section 4, then an independent review of
the uncommitted change by a second agent that had not written it. It found no crash on ordinary
input and no authorization or privacy gap, and it confirmed by running the real code: the
calculator against its own minute model over 1,500 lookups in 14 zones around clock changes, the
resolver over 20,000 random phrases (no day before today, no exception), and the reply builder over
30,000 random answers (no line over its share, no limit exceeded). What it did find, and what was
done:

| Finding | Change |
|---|---|
| A today that is only implied outranked a day the text names: `friday at night`, `tomorrow, after 6pm` and `friday, this week` all resolved to today. | Rules 2–4 of 4.1 rewritten: phrases are read one at a time and a named day outranks an implied today. Twelve new parser tests. |
| A spelled-out range was judged over on its own last day: on Nov 13, `nov 11 to nov 13` rolled to 2027 and `monday to friday` was refused. | The end of a spelled-out range is its last day; only calendar units end exclusively (4.1 rule 2). |
| No upper bound on the first day: `year 5000` queried every linked calendar for that year, and a day in the last weeks of 9999 overflowed into a 500. | `FreeSlotLimits.MaxDaysAhead` (366) and a 400 with its reason (4.2 step 3). Three new route tests. |
| `README.md` said only `/availability role` needs the Server Members intent. | It names `/availability slots` with a role as well. |
| `CONTEXT.md` and the Docs entry said every member left out is named; past about 40 under one reason the rest are a count. | Both reworded. |
| `who:@everyone` titled the reply `@@everyone`. | The subject keeps a name that already starts with the sign. `/availability role` has the same wart and is left alone, as FR8 says. |
| A test comment claimed the recognizer knows no weekday abbreviations; it reads `mon` to `fri`. | Comment corrected; 4.1 records the limitation. |

Noted by the review and not changed here, for unit 2's privacy edit: beyond the DM-reminder wording
the requirements already schedule, the two privacy copies differ in two older places — "fetched on
demand when someone runs an availability check" is only on the web copy, and the repository copy
has no Public calendar paragraph — while both now carry the same date.

Counts after the fixes: 963 tests green — 617 API, 216 bot, 130 web (810 before: 510, 171, 129).
The Release build has the same 22 warnings as the baseline, all in existing test code and the test
project's package references; none from new code. The reply's worst case, measured on the real
builder: 3,556 of 4,096 description characters and 4,710 of 6,000 for the whole embed.

### Review round 1 — two review agents in Copilot's place (2026-11-11)

Copilot's review did not start on PR #172 (step 8.4), so on Joshua's instruction two review agents
read the whole branch at `7e6ba41`, one for security and privacy and one for correctness and code
quality. Neither had seen the code before, and neither changed anything; every finding was
confirmed here before it was acted on, by reading or by running the real code. Their findings are
numbered S1–S5 and Q1–Q10.

Fixed in this round:

| Finding | Change |
|---|---|
| S2, and noted by both reviewers — `when` had no length limit on either layer. | `FreeSlotLimits.MaxWhenLength` (100): Discord enforces it on the option, and the route refuses longer text before anything reads it (4.2, 4.6). A route test and the command-shape test pin both. Since `master` was merged on 2026-11-12 the limit is `DateTextLimits.MaxLength` (below). |
| Q1 — a route test asked for `in 416018 weeks` to land in the last days of year 9999. From 2026-11-14 that text is past the end of the calendar and is no longer read as a date, so the test would have begun failing on its own. | The number of weeks is counted from the day the test runs. |
| Q4 — a day with no slot of its own read `nothing free` even when it began inside a slot listed under the day before. A caller in Tokyo looking at a member on Chicago time sees the member's day as 11 PM–2 PM: with the member busy for all of their Friday, the caller's Friday was free until 2 PM and said `nothing free`. | `FreeSlotDay.OpenUntil` and `FreeSlotDayDto.OpenUntilUtc` (4.5 step 5); the day line reads `free until … (started Thu)` (4.7). Calculator, route and reply tests, and both seeded tests now check it. Not done here, for unit 2's plan: with hours that run from midnight to midnight a slot can reach the end of the window, where its real end is not known. |
| Q2 — the "Not counted" field gave each reason an equal share, so 25 members under one reason and 1 under another were cut at 20 names with over 400 of the field's 1,024 characters unused. | Everyone is named when the field holds them; otherwise a short line keeps its length and the long ones split the rest (4.7). |
| Q3 — the sentence said "in their timezone" for a member who has set none, whose hours are read in the server's zone (UTC by default). | The sentence states the fallback (4.7). Naming the members it applied to, with the zone, is left for unit 2, where the reply names members one by one anyway. |
| Q8 — "A member is counted once they link a calendar" was said to a member whose linked calendar needs reconnecting; a role held only by bots was "nobody has the role"; a display name starting with `@` was titled `@@name`; a minimum longer than anyone's hours gave a run of `nothing free` with no hint. | The second "nobody counted" sentence, the bots-only refusal, one at sign, and the closing line (4.6, 4.7). |
| Q9 — `last quarter` on the first day of a quarter read as today: the recognizer writes a quarter with a timex in parentheses, which was taken for a spelled-out range ending that day. | A spelled-out range is one whose length is counted in days (4.1 rule 2). `past 3 days` still reads as today, as 4.1 records. |
| Q7 — `MinutesPerDay` existed twice, the lower bound for days was a literal on both sides, and no test tied the numbers in the option descriptions to the limits. | `AvailableHoursLimits.MinutesPerDay` and `FreeSlotLimits.MinDays` in Contracts; a test reads the descriptions against the limits. |
| Q5, Q6 — the new route was missing from the list of calendar routes that need a credential; a comment gave the wrong reason for an empty answer; one assertion could not fail; one refusal was asserted by status alone; two tests shared a member id; no test pinned a month by name; the clock-change test had no zone that changes at midnight. | Each corrected or added; America/Santiago joins the clock-change test. |
| S5, and step 1 of S1 — the privacy sentence said "members of your Discord server", and the places where a calendar is linked said less than what linking allows. | Both privacy copies say "members of the Discord servers you share with CalCrony"; the web's calendar card and the `/calendar connect` reply say that those members can check when you are free or busy. |

Confirmed and not changed in this round:

- **S1** (with S3) — `who:<member>` lets any member of a server list any linked member of it, up
  to a year ahead, without the target knowing; before, a linked member could be looked up only
  through a role of 50 or fewer or an event they had answered. The requirements (section 7 there)
  and step 8.3 above call the exposure the same as `/availability role`'s: that holds for the kind
  of data, not for who can be reached. A list of one member also shows where their day starts and
  ends, and so roughly their timezone. Whether to gate lookups about another member, add a
  per-member switch, or keep the design with the clearer wording is Joshua's decision.
- **S4** — a point about what repeated lookups can cost. It applies to the availability commands
  that already exist as much as to this one, so it is raised separately rather than changed here.
- **Q10** — the audit entry stamped 18:04:33Z says "committed locally in four commits"; the commits
  are stamped 18:05:23Z. The entry was written just before they were made. It is left as written.
- Further points from the security review are outside this change and are followed up
  separately.

Counts after round 1: 985 tests green — 625 API, 230 bot, 130 web. The Release build has the same
22 warnings. The reply's worst case on the real builder: 3,601 of 4,096 description characters and
4,749 of 6,000 for the whole embed.

### Review round 2 — the commit that answered round 1 (2026-11-11)

Both agents reviewed `b1452ba` on its own. Both found round 1's fixes right and complete: the
security reviewer found no way around the limit on `when`, and the quality reviewer no wrong
answer in the new code or its tests. Their new findings are S6–S7 and Q11–Q14.

| Finding | Change |
|---|---|
| S6 — wording about code outside this change. | Made more general: here, in the audit log, in the state file and in two code comments. |
| S7 (low) — the new sentence about who can check a linked member's free/busy was shown only to members who had not linked a calendar yet. | The web's calendar card says it after linking as well, and so does `/calendar status`. A web test covers both sides of the card. |
| Q11 — the Docs page said that a day beginning inside a slot says until when it is free; the reply says so only for a day with no slot of its own. | The Docs page says "a day with none of its own". |
| Q12 — two of the new live-script items could not be observed as written. | The member's zone is named (America/Chicago) with one whole day blocked, on the second day of the range or later; the wording check covers `/calendar status` and the card before and after linking. |
| Q13 — `aidlc-state.md` put both reports on the security scale ("nothing critical or high"), where the quality report rated the date-dependent test major. | Reworded. |
| Q14 — no test for a slot that runs through two day starts, or for a day that begins inside a slot the day before folded into its "+N more". | One calculator test and two reply tests. The first shape needs saved hours, so it cannot occur before unit 2. |
| Noted by the quality reviewer — `last 2 weeks` is now refused as passed, which was not written down. | Pinned in the parser tests. It follows from 4.1 rule 2 as narrowed: its length is counted in weeks. |

Also done on the security reviewer's suggestion: the limit on `when` was checked against the real
parser with generated text, and it held.

Counts after round 2: 991 tests green — 627 API, 232 bot, 132 web. The Release build has the same
22 warnings.

### Review round 3 — the commit that answered round 2 (2026-11-11)

Both agents reviewed `c8ce2c9` on its own. The quality reviewer had no findings: each fix right,
each new test proving what its name says, the documents agreeing with the code and with each
other. The security reviewer found round 2's fixes right and nothing new in the code, and had two
low findings about wording in these review records.

| Finding | Change |
|---|---|
| S8, S9 (both low) — wording in the review records: this section, the audit log and the state file. | Reworded. No code, test or user-facing text changes. |

Counts are unchanged: 991 tests green.

### Review round 4 — the records commit (2026-11-11)

Both agents reviewed `ae58578`, which changed these records and nothing else. The security
reviewer found nothing left in the tree to reword. Both found that the records had stopped
agreeing on what is open with Joshua.

| Finding | Change |
|---|---|
| S10 (low) and Q15 (minor), the same point — in rewording, `aidlc-state.md` had stopped listing the second of the two decisions put to Joshua, while the audit log still showed two. A later session resuming from the state file would have seen one, and the merge next. | The state file lists both again, to be settled before the merge, and the audit log's next entry says the same. |
| Q16 (nit) — the round 3 audit entry called every change to the earlier entries a rewording; some passages were shortened. | The round 4 entry says so. The earlier entries are left as they are. |
| Q17 (nit) — the note on checking the limit on `when` did not say how the check came out. | "And it held", above and in the round 4 audit entry. |

Counts are unchanged: 991 tests green.

### Review round 5 — the second records commit (2026-11-11)

Both agents reviewed `9f13116` and had no findings. The loop ends there: it is the head of
PR #172, and every check on it is green. Five rounds in all — the whole branch, then each commit
that answered the round before — with ten findings on security and privacy (S1–S10) and seventeen
on correctness and quality (Q1–Q17). The code and tests last changed in `c8ce2c9`, which both
agents passed in round 3; rounds 4 and 5 were about these records alone.

### Joshua's decisions on what the review left open (2026-11-12)

Both were put to him after round 1 and again after round 5. His answer: "I agree with your
recommendations".

- **S1, who may list a single member's free slots — option A, recorded as FR19 and D11.** Only
  once that member has saved their own available hours, which unit 2 brings; until then `who:`
  takes the caller or a role. The requirements, the application design and the workflow plan are
  amended, and sections 1, 2, 4.6 and 4.8 above are changed in place.
- **The separate piece of work outside this change goes first**, in its own session, started on
  his answer. This PR's merge waits for it.

What changed for D11:

| Where | Change |
|---|---|
| `FreeSlotTarget.ForUser` | Takes the caller's id. A member other than the caller is refused — after the checks for a bot and for a user from outside the server, which keep their own reasons — and no member is handed on, so the module has nothing to ask the API about them. |
| `AvailabilityModule.SlotsAsync` | Passes the caller's id. The subcommand reads `Find free time slots for yourself or a role over one or more days` and `who` reads `Yourself or a role`. |
| Tests | `FreeSlotTargetTests`: the caller alone; another member refused with nobody asked about; the bot and outside-user refusals ahead of it. `AvailabilityCommandShapeTests` pins the two descriptions, so they move with the rule in unit 2. `DocsPageTests` checks the entry's new sentence. |
| Docs | Both README lines, the Docs entry and its command row, and the live script (4.8). The README's Features section stays at 398 words. |

What D11 does not change, so that it is not read as more than it is:

- **A role is as it was.** A role lookup that ends up with one counted member — a role one member
  holds, or one whose other holders have no readable calendar — lists that member's slots. That
  is almost the reach `/availability role` has, where every member gets a line of their own.
  The one difference (S11, round 6): bots are left out here before the 50-member limit and
  counted toward it there, so a role of more than 50 holders with 50 or fewer people is accepted
  here and refused by the grid — `@everyone` in a server of 46 people and 6 bots, for one. Who
  holds a role is in the hands of whoever manages roles in the server. S3's point, that a list
  resting on one member shows roughly where their day starts and ends, still applies on this
  path.
- **What a role lookup gives is as it was, too** (S11). When the caller is one of two counted
  members, the answer is the other member's slots cut to the caller's own hours, and a caller
  with an empty calendar can move those hours with `/settings timezone`. A lookup still lists up
  to 14 days in one reply, starting up to a year ahead. The members it covers are not told, and
  nothing records who asked.
- **The route.** `POST /calendar/free-slots` stays bot-only and takes the ids the bot hands it.
  Like R3's membership check, the rule for `who` is the bot's. Where the saved-hours check sits
  is for unit 2's plan.
- **The reply.** A list about the caller still reads "their linked calendar … their timezone".
- **The PR's title**, `feat: free slots for a member or role (/availability slots)`, as the
  workflow plan set it. It becomes a line of the 0.37.0 release notes, by when unit 2 has made
  "a member" true of anyone who has saved hours.

Counts after D11: 993 tests green — 627 API, 234 bot, 132 web. The Release build has the same 22
warnings.

### Review round 6 — the commit that implemented D11 (2026-11-12)

Both agents reviewed `9f13116..61d6eda`: the round 5 records and the D11 change. Neither had a
finding in the code or the tests. The security reviewer found the rule enforced — no way in this
unit to get another member's slots by naming them, the three refusals decided before the API is
called and so giving nothing away — and nothing in the range saying more than it should. The
quality reviewer found the order of the checks right, each test proving what its name says, and
the user-facing wording accurate. Their findings are on these records.

| Finding | Change |
|---|---|
| S11 (low) — the records said a role lookup has "the reach `/availability role` already has". Not quite: bots are left out before the limit here and counted there. And the list of what D11 does not change left out three things about a role lookup: the caller as the other counted member, how much one reply lists, and that the members covered are not told. | The role bullet above is exact and a bullet is added; FR19 and D11 say the same. The audit entry stamped 00:40:36Z is corrected by the entry after it, not in place. |
| Q18 (nit) — three slips in this plan's amended sections: 4.6 said two refusals had ended "pick a member or a role" as approved, where one did; it said the rows are checked in the table's order, where the three refusals are; and section 1 did not keep its approved wording. | Each corrected. |
| Q19 (nit) — the design's docs-and-privacy table (its section 7) did not carry what FR19 and D11 ask of the Docs page and the privacy copies in unit 2. | Added there, with the README wording that returns in unit 2, and the amendment note names section 7. |

Noted by both and left as it is: the one line with no automated test is the module handing
`ForUser` the caller's id — the bot tests have no way to run a command. The live script's item
for naming another member is its check, and is marked there as one that must pass.

For unit 2's plan, from the security reviewer: S3 comes back for members who save hours, since
a list about one of them shows roughly the hours they keep, so the wording where hours are saved
should say so (FR10 now asks for it); and a role lookup that rests on one member needs no
opt-in under D11 — if that were ever wanted, only the API could enforce it, because only it
knows who ended up counted.

Not changed, and offered to Joshua: counting bots toward the limit in `ForRole`, which would
make the reach identical to the grid's and refuse the role in S11's example.

Counts are unchanged: 993 tests green.

### Review round 7 — the records commit that answered round 6 (2026-11-12)

Both agents reviewed `51acb11`, six documents and nothing else. The security reviewer had no
findings: S11 answered accurately in every place it touches, the new bullet right against the
code, and nothing in the commit saying more than it should. The quality reviewer found Q18 and
Q19 answered as meant and every statement the commit adds about the code true, and had two nits
on gaps the corrections left.

| Finding | Change |
|---|---|
| Q20 (nit) — FR10's new clause, that a list about one member shows roughly the hours they keep, had not reached the design: D11 and its section 7 stopped at listing one's slots by name. | D11 and both section 7 cells carry it. |
| Q21 (nit) — the live script's must-pass item sits in a section a tester may skip when the Google prerequisites are missing, and it needs none of them. | The item says so, and to run it even when the rest of the section is skipped. |

Noted by the security reviewer, not as a finding: the must-pass item can run after the merge,
on the test stack, and has to pass before the release.

Counts are unchanged: 993 tests green.

### Review round 8 — the records commit that answered round 7 (2026-11-12)

Both agents reviewed `ef81f5f`, five documents and nothing else. The quality reviewer had no
findings: Q20 and Q21 answered as meant, its round 7 report recorded accurately, the documents
agreeing with each other and with the code. The security reviewer found nothing saying more than
it should and its round 7 report recorded accurately, and had one finding at information level.

| Finding | Change |
|---|---|
| S12 (info) — the live script says in three places that section 12 can be skipped without blocking the release, and none of them named the must-pass item as the exception. A tester without the Google prerequisites would skip the section at its header and never reach the item. | Each of the three names the exception: the opening paragraph, the section's header and rule 2 of Part C. |

Also recorded, at the security reviewer's remark: in round 7 it had itself noted the gap that
became Q20, as one that needed no change.

Counts are unchanged: 993 tests green.

### Review round 9 — the records commit that answered round 8 (2026-11-12)

Both agents reviewed `d9388af`, four documents and nothing else. The security reviewer had no
findings: S12 answered in all three places, with nothing left in the script that contradicts
it. The quality reviewer found the three statements agreeing with the item and with each other
and its round 8 report recorded accurately, and had one nit.

| Finding | Change |
|---|---|
| Q22 (nit) — `build-and-test-summary.md`, where the results of the live pass are recorded, states the gate in its own words — "all non-conditional sections pass" — with no exception for the must-pass item. This branch had not touched that file. | Its gate line names the exception, and so does its results row for section 12. A search of the tracked files finds no other statement of the rule. |

Counts are unchanged: 993 tests green.

### Review round 10 — the records commit that answered round 9 (2026-11-12)

Both agents reviewed `c22539c` and had no findings. The loop on D11 ends there: it is the head of
PR #172, and every check on it is green. Five rounds on this change — `61d6eda` with the round 5
records, then each commit that answered the round before — with two findings on security and
privacy (S11, S12) and five on correctness and quality (Q18–Q22), none of them in the code or the
tests, which last changed in `61d6eda`. The security reviewer also corrected its own round 9
report, which had said the tracked files held no other statement of the skip rule; Q22 was one.

Over all ten rounds: twelve findings on security and privacy (S1–S12) and twenty-two on
correctness and quality (Q1–Q22).

### `master` merged into the branch (2026-11-12)

The separate piece of work reached `master` on 2026-11-12 as `29a0d15` (PR #173, squash-merged at
20:34:34Z): one length limit for date and time text typed in words, `DateTextLimits.MaxLength`
(100 characters, in Contracts), checked at the top of `TryResolve` and declared on the bot's date
options. PR #172 then conflicted with `master`, so `master` was merged into this branch. Only
`aidlc-docs/audit.md` and `aidlc-docs/aidlc-state.md` conflicted, both sides having appended at
the same place: each keeps both sides whole, the audit entries in the order of their timestamps
and the state file's new line ahead of this feature's section. No code conflicted.

What this unit changed to follow that limit, in the merge commit itself:

| Where | Change |
|---|---|
| `NaturalDateTimeParser.TryResolveDay` | Refuses text over the limit first, with `TryResolve`'s refusal, and checks its text for null as `TryResolve` does (4.1, rule 6). The class summary `master` brought says text over the limit is refused before anything reads it; with this that is true of both methods. |
| One constant | `FreeSlotLimits.MaxWhenLength` is gone. The `when` option of `/availability slots` and the route's own check read `DateTextLimits.MaxLength`, so the first day cannot drift from the other date options. The route's check and its message, `when must be at most 100 characters.`, stay as they were (4.2): over-long text is still refused there, before the service runs. |
| Tests | `NaturalDateTimeParserTests`: the day reader reads text at the limit and refuses one character more. `DateOptionLimitTests`, which came with `master`, gains a row for the `when` of `/availability slots` — its twelfth. The route test and the command-shape test read the shared constant, and the route test fills its text out with dots, as `master`'s length tests do. |
| Wording | The comment above the route's check, the route test's comment and two lines of this plan — 4.2's table and round 1's row for S2 — are reworded or shortened to say what the limit is and where it is checked. |

Each check was taken out in turn, and the test meant to pin it failed: without the day reader's
own check the over-limit text is read as tomorrow; without the route's check the refusal is the
parser's and not the route's `when must be at most 100 characters.`; with the option's limit moved
off the shared constant, the new `DateOptionLimitTests` row and the command-shape test both fail.

Counts after the merge: 1,017 tests green — 639 API, 246 bot, 132 web (993 on this branch before,
831 on `master`). The Release build has no errors and the same 22 warnings.

Both review agents read the merge commit before it is pushed.

## 10. Plan checklist

- [x] Requirements, workflow plan and design re-read; the code this unit plugs into read at the
      baseline (section 6)
- [x] Rules written and the uncertain ones settled by running the real code (4.1, R8, the word count)
- [x] Refinements to the approved design listed with their reasons (section 3)
- [x] Steps drafted in layer order, tests beside each layer
- [x] Approved by Joshua — 2026-11-11, as written ("Continue")
