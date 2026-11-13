# Available hours — unit 2 (`available-hours`): functional design and code-generation plan

**Feature**: free slots and available hours (asked for in the community Discord on 2026-11-10;
tracked in #171). **Inputs**: the approved requirements
(`inception/requirements/availability-free-slots-requirements.md`, FR1–FR19), workflow plan and
application design (`inception/application-design/availability-free-slots-design.md`, D1–D11), and
unit 1 as merged (`construction/plans/free-slots-code-generation-plan.md`).
**Baseline**: `master` a079441 (unit 1, PR #172; 1,017 tests green there — 639 API, 246 bot, 132
web — re-run as step 0.1). **Branch**: `feature/available-hours` → PR
`feat: per-member available hours for free slots`.
**Status**: approved by Joshua on 2026-11-13 (UTC), as written ("Approve the plan"); code
generation under way (section 9 records what differs from the plan).

As with unit 1, this file is the unit's functional design (sections 1–5) followed by its
code-generation plan (sections 6–8), approved together.

## 1. What this unit ships

Each member can save the hours they are usually available — one range per weekday — from Discord
(`/settings available-hours set`, `off`, `reset`) and from a card on the web's My settings page.
`/availability slots` then cuts each counted member's slots from their own hours instead of the
default, counts a member who has saved hours and linked no calendar on those hours alone ("hours
only"), and lets another member be named in `who:` once that member has saved hours (D11).

One additive migration: a nullable column on `UserProfile`. No configuration, no new Google scope,
Discord permission or intent. Nothing from Google is stored (FR16); a member's hours are the only
new stored data. `/availability role` and `/availability event` are not touched (FR8).

Requirements covered: FR10–FR12, FR14, FR15; the "hours only" part of FR3; FR13 as it reads once
some members have hours of their own; the hours half of FR17 and FR18; the rest of FR19.

## 2. Carried in (locked)

| From | Decision |
|---|---|
| Q3, Q6, Q7 | Each member stores their own hours; one range per weekday, each day on or off; a member with hours and no calendar is counted, marked "hours only". |
| D1, D2 | Hours are one nullable JSON column on `UserProfile`, and travel on routes of their own — `GET`, `PUT`, `DELETE /users/{userId}/available-hours` — never on `UserSettingsDto`. |
| D3 | A time of day is minutes from midnight in 30-minute steps, 0–1440; the end may be 1440. A weekday is the BCL `DayOfWeek`. |
| D4 | The calculation is final since unit 1. This unit changes only where a member's week comes from. |
| D6 | `/settings available-hours` is a subcommand group — `set`, `off`, `reset` — nested in `SettingsModule`. |
| D7 | "Available on no day" cannot be stored: the API rejects an empty week, and the bot and the web card refuse it and point at reset. |
| D8 | Limits live once, in Contracts. |
| D10 | Saving hours writes no activity-log entry. |
| D11, FR19 | A member other than the caller can be named in `who:` only once they have saved their own hours; every place where hours are saved or shown says that saving them allows it, and that such a list shows roughly the hours one keeps (FR10). Reset takes it back. A role is as it was. |
| Design 5.1 | `set` and `off` change the days named and leave the others as they were; a first `set` starts from an empty week, so days not named are unavailable. Every reply spells out the whole week and the zone it is read in. |
| FR12 | Hours are wall-clock times in the member's zone: their `/settings timezone`, else the zone of the server the command runs in, else UTC. |

## 3. Refinements made here

Details the design left to this stage, plus the places this plan moves something. Each can be
overruled. The numbering continues unit 1's R1–R10.

| # | Refinement | Why |
|---|---|---|
| R11 | D11's check sits in the API, inside the lookup. `FreeSlotsRequest` gains `ByName` — set when `who` named a member rather than a role — and the service refuses a named member other than the requester who has no saved hours, before any calendar is read. | D11 left open how the bot learns that hours are saved, and noted that the hours `GET` route could answer it. Asking first and looking up second leaves a gap between the two calls, and has the bot read another member's hours. In one request the rule is decided where the hours are stored, and a route test can show that no calendar was asked about. |
| R12 | The answer says whether it was checked by name (`FreeSlotsResponse.ByName`), and the bot shows a list about someone else only when it was. | Then the rule does not depend on the API being updated before the bot: an API that does not know `ByName` ignores it, and the bot refuses instead of listing. |
| R13 | An `off` with nothing saved yet is refused and points at `set`. The alternative was to start from the default week and save what is left. | Saving hours is what lets other members list one's slots by name. A command called `off` should not be what turns that on. |
| R14 | The reply names members in two fields — **Counted with less to go on** (hours only; default hours assumed) and **Not counted** — where the design (5.3) had one field for all five groups. | Fifty mentions do not fit in one field's 1,024 characters, and FR3 wants each of these members named. The two fields also keep "counted" and "not counted" apart, which is the first thing a reader needs. |
| R15 | Members on the default hours are named one by one only when some counted member has saved hours. When every counted member is on the default, the sentence says so for all of them, as in unit 1. | Otherwise every role lookup would list its whole membership under a note until members start saving hours. FR13 is met either way: the reply says whom the default was applied to. |
| R16 | The stored value is `[{"day":1,"start":480,"end":1380},…]` — ISO weekday numbers, Monday 1 — in a column of at most 512 characters, where D1 said "text". A stored value that cannot be read counts as not saved, and reading never throws. | The longest week is 239 characters, and the profile's timezone, username and avatar hash are bounded the same way (its theme is not). The stored names are fixed here so that renaming a DTO property cannot change what is on disk. |
| R17 | A typed time is at most 20 characters and is read by a small reader of its own: `6pm`, `6:30 PM`, `18:30`, `noon`, `midnight`. A bare hour is read on the 24-hour clock (`7` is 7 AM). `midnight`, `12am` and `0:00` mean the start of the day in `from` and the end of the day in `to`; `24:00` is accepted in `to` only. | The design named `6:30pm` and `18:30`; the rest are what people type. The options declare the length, so Discord turns longer text down before the command runs. |
| R18 | A suggestion shows the 12-hour label and carries the 24-hour form as its value (`6:30 PM` → `18:30`, `midnight` → `24:00`). | The value a picked suggestion sends is read by the same reader as typed text, and is unambiguous. |
| R19 | Every hours command reads what it needs before it writes — the caller's hours, their settings, and the server's settings when they have set no timezone — and writes nothing if a read fails. | The reply has to say which zone applies (FR12). A reply that saved the hours and then could not say so would leave the member guessing. |
| R20 | The two privacy copies are brought level in two older places as well: "fetched on demand when someone runs an availability check" and the Public calendar paragraph, which only the web copy has. The retention section gains a line for hours. | Unit 1's review noted both differences and left them for this unit's privacy edit. The requirements already schedule the DM-reminder wording. |
| R21 | `who` reads `Yourself, a role, or a member who has saved their available hours`, and the subcommand reads `Find free time slots for a member or role over one or more days` again. | The design returns "a member or role" to the descriptions (D11). The option's own description is where the condition fits in under 100 characters. |
| R22 | `AvailableHoursCard` loads and saves through the hours routes itself; the page hands it the member's saved timezone and shows it only once the settings have loaded. | The card then never states a zone the page has not read, and the page's own saves never carry hours (FR14). |

## 4. Rules

### 4.1 A week of hours

A week holds at most one range per weekday and at least one day. A range is a start and an end in
minutes from midnight, on the 30-minute step (`AvailableHoursLimits.StepMinutes`), with the start
before the end; the end may be 1440, the midnight that closes the day. A weekday without a range is
not available.

`WeeklyHours.TryFromDto` checks a submitted week in this order and stops at the first failure:
the two checks on the whole list first, then the entries one at a time in the order sent, each
through its five checks — so a fault in the first entry is reported ahead of any in the second.
`{Day}` is the weekday's English name.

| Check | Message |
|---|---|
| no days, or none sent | `Pick at least one day, or reset to go back to the default hours.` |
| more than seven entries | `A week has seven days.` |
| an entry's day is not a weekday | `{value} is not a day of the week.` |
| a weekday appears twice | `{Day} is listed twice.` |
| a time outside 0–1440 | `{Day}: a time is 0 to 1440 minutes after midnight.` |
| a time off the 30-minute step | `{Day}: times are in 30-minute steps.` |
| the start is not before the end | `{Day}: the start has to be before the end.` |

Storage (R16): `Serialize` writes the days in order, Monday first, as
`[{"day":1,"start":480,"end":1380},…]`. `Parse` returns null for null or blank text and for
anything it cannot read as a week — text that is not that JSON, an unknown weekday, a weekday
twice, a range outside one day or not start-before-end, or no day at all. It does not apply the
30-minute step: that is a rule for what may be entered, and a stored week must stay readable if
the step ever changes. Saving is another matter: `set`, `off` and the web card send the whole
week back, and every day in it is then held to the step in force — so a change of step needs
the stored weeks rewritten with it. `ToDto` lists the available days, Monday first.

### 4.2 The hours routes

In `SettingsEndpoints`, beside the settings routes and with their guard: the bot may act for any
member, and a web caller only for their own id — another id gets the existing 403,
`You can only do that for your own account.`

| Route | Answer |
|---|---|
| `GET /users/{userId}/available-hours` | `AvailableHoursDto`. With nothing saved — no profile row, an empty column, or a stored value that cannot be read — `IsSet` is false and `Days` is the default week, so both surfaces can show what applies. Otherwise `IsSet` is true and `Days` is the saved week. It creates no row. |
| `PUT /users/{userId}/available-hours` | `SetAvailableHoursRequest` → the saved week as `AvailableHoursDto` with `IsSet` true, or 400 with a message from 4.1. The profile row is created when the member has none, with the defaults a missing row already reads as. |
| `DELETE /users/{userId}/available-hours` | 204. The column is cleared when there is a row; no row is created; a second call is the same 204. |

Nothing else changes on the profile: these routes touch that one column, and
`PUT /users/{id}/settings` never carries it (FR14). No activity-log entry is written (D10).

The row is found or added the way `PutUserSettings` does it. Two first-ever writes for one member
at the same instant can still collide, as two settings writes can today; that is left as it is.

### 4.3 Slots with stored hours

`FreeSlotsService` loads each member's stored week with the timezone it already loads — one query,
read-only. A member's state follows the design's table (4.5 there):

| Availability check says | Hours saved | State | Counted | Hours used |
|---|---|---|---|---|
| `Free` or `Busy` | yes | `Counted` | yes | their own |
| `Free` or `Busy` | no | `DefaultHours` | yes | 8 AM–11 PM every day |
| `NotConnected` | yes | `HoursOnly` | yes | their own; no busy blocks |
| `NotConnected` | no | `NotCounted` | no | — |
| `ReconnectRequired` | either | `ReconnectRequired` | no | — |
| `Error` | either | `Error` | no | — |

A member whose calendar is linked and cannot be read is not counted on their hours alone: the
calendar exists, and a list that ignored it would say more than is known.

Hours are read in the member's own zone by the rule unit 1 already applies to the default hours
(`MemberZone.Resolve`). The calculation, the window, the one availability check per lookup and the
shape of the answer are unchanged. A member counted on hours alone costs no provider query.

### 4.4 Naming another member (D11)

- The bot sets `ByName` when `who` is a member, and leaves it unset for a role — including a role
  one member holds, which is a role lookup as before (FR19).
- The route refuses a by-name request that does not carry exactly one member:
  `A lookup by name takes exactly one member.`
- The service, when the request is by name and the member is not the requester: with no saved
  hours it refuses, before the availability check, so no calendar is read. The text is the same
  whether or not that member has linked a calendar, or has ever used CalCrony:

  ``That member hasn't saved their available hours, so their free slots can't be listed by name. They can save them with `/settings available-hours set` or under My settings on the web. To see when a group is free, pick a role.``

- The requester by name is always allowed, with or without saved hours.
- The answer carries `ByName` when the request did (R12). In the bot, a target that is a member
  other than the caller is shown only when the answer says so; otherwise the reply is
  `That member's free slots can't be listed by name right now.`
- The bot's other checks on a single target stay as they are and stay first: a bot
  (`Bots don't keep a calendar — pick a member or a role.`) and a user who is not in this server
  (`That user isn't a member of this server.`). The route stays bot-only.

What the refusal tells the caller is that the named member has not saved hours — the same fact a
list tells in the other direction, and the member's own choice either way. The notices of 4.10
say that saving hours makes one's slots listable by name.

### 4.5 The commands

`/settings available-hours` — `The hours you're usually available; free slots are cut from them`.
Every hours command acts on the member who runs it; none takes a user option. All replies are
ephemeral.

| Subcommand | Options | Description in Discord |
|---|---|---|
| `set` | `days` (required) — one of ten choices: `every day`, `weekdays`, `weekends`, `Monday` … `Sunday` (as approved, two of them carried the days in parentheses; section 9) · `from`, `to` (required) — text with suggestions, at most 20 characters | `Save your hours for the days you pick; other members can then list your free slots by name` (review round 1; as approved: `Save the hours you're available on the days you pick`) · days: `The days these hours apply to` · from: `Start time, e.g. 6 PM or 18:00` · to: `End time, e.g. 11 PM or 23:00; midnight ends the day` |
| `off` | `days` (required) — the same ten choices | `Mark days as not available` · days: `The days to mark as not available` |
| `reset` | none | `Go back to the default hours, 8 AM–11 PM every day` — a test holds the two times against `AvailableHoursLimits` |

`set`, in order:

1. Read both times (4.6). One that cannot be read: `I couldn't read the {from|to} time. Try 6 PM, 6:30pm or 18:30.`
   One off the step: `Times are in 30-minute steps — 6 PM or 6:30 PM, for example.`
   A start not before its end: `The start has to be before the end. Hours can't run past midnight; for a late night, end at midnight.`
2. Read the caller's hours and settings, and the server's settings when the caller has no timezone
   (R19). A failed read: `❌ Couldn't load your settings: {error}`, and nothing is written.
3. Start from the saved week, or from an empty one when nothing is saved; give the named days the
   range; save. A refusal from the API prints as `❌ {error}`. Which week a save starts from, and
   the two refusals of `off`, are decided in `AvailableHoursText` (`Set`, `TryOff`), where tests
   reach them (review round 1).
4. Reply with the saved week, the zone and the notice (4.10).

`off`: the same reads; with nothing saved it is refused (R13):
``You haven't saved any hours yet, so there is nothing to switch off. Start with `/settings available-hours set`.``
Otherwise the named days lose their range. If no day would be left (D7):
``That would leave no day available. To go back to the default hours, use `/settings available-hours reset`.``
Otherwise it saves and replies as `set` does.

`reset`: clears the saved hours and replies (4.10). It needs no reads.

`/settings view` gains one line after the DM-reminder line, from one more read under the rule
`view` already has (a failed read is an error, never a guess):

- saved: `**Your available hours:** {week} (saved — members of servers you share with CalCrony can list your free slots by name)`
- not saved: `**Your available hours:** not saved — 8 AM–11 PM every day is assumed`

### 4.6 Typed times and suggestions

`TimeOfDayText.TryParse(text, asEnd, out minutes)` reads, ignoring case and outer spaces:

| Typed | Read as |
|---|---|
| `6pm`, `6 pm`, `6:30pm`, `6:30 PM`, `12am`, `12 pm` | the 12-hour clock; hours 1–12, minutes two digits |
| `18:30`, `18`, `7`, `0:00`, `00:00` | the 24-hour clock; hours 0–23 |
| `noon` | 12 PM |
| `midnight`, `12am`, `0:00`, `00:00` | 0 in `from`; 1440 in `to` |
| `24:00`, and a bare `24` | 1440, in `to` only |

Anything else is not a time. The reader takes at most 20 characters
(`TimeOfDayText.MaxInputLength`, which both options declare) and does not check the 30-minute
step — the command does, so it can say what is wrong.

`TimeOfDayAutocompleteHandler.BuildSuggestions(input, asEnd)` is pure and makes no API call. The
candidates are the times on the step: 12 AM to 11:30 PM for `from`, 12:30 AM to midnight for `to`.
With nothing typed it offers the whole hours (24 choices). With something typed it offers the
candidates whose label or 24-hour form starts with it, spaces and case aside — `6` gives 6 AM,
6:30 AM, 6 PM and 6:30 PM; `18` gives 6 PM and 6:30 PM — at most 25, with a time the text names
exactly placed first (R18).

### 4.7 The week in words

`AvailableHoursText.Describe` puts a week on one line. Days that share a range form a group;
groups come in the order of their first day, Monday first. Three or more days in a row are written
`Mon–Fri`, others are joined with commas; then the range in the bot's 12-hour style, with
`midnight` for an end of 1440. Days with no range come last as `… off`. A week with the same range
on all seven days is `Every day 8 AM–11 PM`.

```text
Mon–Fri 6 PM–11 PM · Sat, Sun 10 AM–midnight
Mon, Wed, Fri 6 PM–10 PM · Sat 9 AM–1 PM · Tue, Thu, Sun off
```

`AvailableHoursText.Apply(days, choice, range)` gives the named days the range, or removes them
when the range is null, and returns the new week.

### 4.8 The slots reply

Title, range line, the closing line and the footer are unit 1's. The basis sentence and the
fields change, and a day line gains one thing (review round 1): an end that is only where the
lookup stopped looking is followed by `or later`. A lookup reads one day past the last day
listed; hours that run to midnight and resume at midnight can carry a slot all the way there,
where it is cut. The answer names that instant (`FreeSlotsResponse.LookedUntilUtc`), and a slot
or a "free until" that ends there reads `…–<t:end:f> or later`.

**Basis sentence.** "Default" below means every counted member is on the default hours.

| Case | Sentence |
|---|---|
| nobody counted, none of them with hours or a calendar | ``Nobody could be counted, so there are no slots to list. A member is counted once they link a calendar with `/calendar connect` or save their available hours with `/settings available-hours set`.`` |
| nobody counted, at least one for another reason | unit 1's, unchanged |
| default — one asked about; one counted of several; several counted | unit 1's three sentences, unchanged |
| one asked about, on their calendar and saved hours | `Free on their linked calendar, inside the available hours they saved, read in their timezone (the server's if they haven't set one).` |
| one asked about, on saved hours alone | `Inside the available hours they saved, read in their timezone (the server's if they haven't set one). No calendar is linked, so anything else they have on isn't taken into account.` |
| several asked about, one counted, on their calendar and saved hours | `When the one counted member is free on their linked calendar, inside the available hours they saved, read in their timezone (the server's if they haven't set one).` |
| several asked about, one counted, on saved hours alone | `Inside the one counted member's saved available hours, read in their timezone (the server's if they haven't set one). They have no calendar linked, so anything else they have on isn't taken into account.` |
| two or more counted, at least one with saved hours | `When all {n} counted members are free: each inside their available hours{default}, read in their own timezone (the server's for anyone who hasn't set one), with nothing on a linked calendar.` — `{default}` is ` (8 AM–11 PM for anyone who hasn't saved theirs)` when a counted member is on the default hours, and empty otherwise |

A list about the caller still says "their", as in unit 1.

**Fields** (R14), each only when it has something to say:

- **Counted with less to go on** — shown when two or more members are counted and at least one of
  them has saved hours. Its lines, in this order:
  `🕒 Saved hours only, no calendar linked — {members}` and
  `ℹ️ No hours saved, 8 AM–11 PM assumed — {members}` (R15). With one member counted, the
  sentence already says which of these applies.
- **Not counted** — as in unit 1, with the first reason reworded:
  `⚪ No hours saved and no calendar linked`, then the reconnect and couldn't-check lines as they are.

Each field shares its own room by unit 1's rule (shortest line first, an equal share of what is
left, `+N more` where a line is cut). **Not counted** keeps the full 1,024 characters;
**Counted with less to go on** takes what the embed has left under Discord's 6,000, at most 1,024.
It is shown first.

**Budget.** The longest new sentence is 69 characters longer than unit 1's longest (228 against
159), so the description's worst case goes from 3,601 to about 3,670 of 4,096. With both fields
full the embed comes to about 5,870 of 6,000 — unit 1's 4,749, those 69, a second field of 1,024
and its name. With the limits as they are, the room left for the first field is never under a
field's 1,024, so the rule that it takes what is left is a guard against the description growing,
and no test can make it bite (section 9). A test builds the worst case, and Discord.Net refuses
to build an embed over either limit.

### 4.9 The web card

`AvailableHoursCard`, on My settings under the two existing cards:

- Seven rows, Monday first: an **Available** tick, **From** (12 AM to 11:30 PM) and **To**
  (12:30 AM to midnight), in 30-minute steps. A row that is not ticked keeps its times and greys
  them out. A day that is not in the saved week starts at 8 AM–11 PM.
- With nothing saved it shows the default week, ticked, under
  `You haven't saved hours, so the default applies: 8 AM–11 PM every day.`
- **Save** is disabled while the hours have not loaded, while no day is ticked (D7:
  `Tick at least one day, or reset to the default.`) or while a ticked row's start is not before
  its end (`The start has to be before the end.` beside that row). A refusal from the API is shown
  as it is.
- **Reset to default** clears the saved hours and shows the default week again; it is disabled
  while nothing is saved.
- The zone, from the page's loaded settings: `Times are read in your timezone, {zone}.` — or,
  with none set, `You haven't set a timezone, so these times are read in the timezone of the server someone looks you up from. Set yours above to make them mean the same everywhere.`
- The notice of 4.10, always visible.
- If the hours cannot be loaded the card says so and offers nothing to save: it never writes rows
  it did not read.

### 4.10 What saving hours says (FR10, D11)

One wording, used wherever hours are saved or shown:

| Where | Text |
|---|---|
| reply to `set` and `off` | `🕒 Your available hours: **{week}**` / the zone line / ``Saved hours can be looked up by name: members of the servers you share with CalCrony can list your free slots with `/availability slots`, which shows roughly the hours you keep and, if your calendar is linked, when you're busy inside them. `/settings available-hours reset` takes that back.`` (the clause on a linked calendar: review round 1) |
| zone line, timezone set | `Read in your timezone, **{zone}**.` |
| zone line, none set | ``You haven't set a timezone, so these hours are read in each server's own — **{server zone}** here. `/settings timezone` makes them mean the same everywhere.`` |
| reply to `reset` | `🕒 Your available hours are back to the default, **8 AM–11 PM every day**. Other members can no longer list your free slots by name; a role you hold still counts you while your calendar is linked.` |
| `/settings view` | the two lines of 4.5 |
| the description of `set` in Discord | `…; other members can then list your free slots by name` (4.5) — the one place a member reads before saving (review round 1) |
| web card | `Saved hours can be looked up by name: members of the servers you share with CalCrony can list your free slots with /availability slots, which shows roughly the hours you keep and, if your calendar is linked, when you're busy inside them. Reset to default takes that back.` |
| Docs page, both privacy copies | 4.11 |

### 4.11 Docs copy

| Where | Text |
|---|---|
| `README.md` Features, line 42 | ``- Google Calendar, free/busy scope only (`/availability`): role or event grids, free slots for a member or role within their available hours.`` — 22 words where the line has 20, so the section goes from 398 to 400, the cap. |
| `README.md` command table, the `/settings` row | adds ``· `/settings available-hours set\|off\|reset` `` and `· your available hours` |
| `README.md` command table, the slots row | `Free slots for a member or a role over 1–14 days (default 7, from today): times every counted member is free, inside each member's available hours (8 AM–11 PM until they save their own). You can always pick yourself; another member once they have saved hours. Only you see the reply` |
| `Docs.razor`, a new entry after "Google Calendar availability" | **Available hours** (per member; only you can set yours) — `/settings available-hours set days:weekdays from:6pm to:11pm`, or the card under My settings here: the hours you're usually available, one range per weekday, read in your timezone (the server's if you haven't set one). Free slots are cut from them, and until you save any, 8 AM to 11 PM every day is assumed. The first `set` leaves the days you don't name off; after that, `set` and `off` change only the days you name, and `reset` goes back to the default. With saved hours you are counted in free-slot lists even without a linked calendar, marked as hours only. Saving hours also lets members of the servers you share with CalCrony list your free slots by name, which shows roughly the hours you keep and, if your calendar is linked, when you're busy inside them; `reset` takes that back. (The sentence on a first `set` and the clause on a linked calendar: review round 1.) |
| `Docs.razor`, the "Google Calendar availability" entry | the slots sentence reads "lists when a member — or everyone holding a role — is free over 1 to 14 days: you can always pick yourself, and another member once they have saved their available hours"; "A slot is time inside each member's available hours (8 AM to 11 PM until they save their own) with nothing on a linked calendar"; and "Members with neither saved hours nor a linked calendar aren't counted". |
| `Docs.razor` command table, the `/settings` row | adds `available-hours set\|off\|reset` |
| `PRIVACY_POLICY.md`, `Privacy.razor` — Preferences | the same sentence in both: per-user and per-server timezone, DM-confirmation and DM-reminder settings (reminders by DM are off unless you turn them on yourself — no server or event creator can), your available hours if you save them (the weekly hours you say you are usually available; saving them lets members of the servers you share with CalCrony list your free slots by name, which shows roughly the hours you keep and, if your calendar is linked, when you are busy inside them, and resetting them takes that back), interface theme, server settings. |
| both privacy copies — levelled (R20) | "fetched on demand when someone runs an availability check and not stored" in both; the Public calendar paragraph in both, in the web copy's words. |
| both privacy copies — Retention & deletion | **Available hours**: kept until you reset them (`/settings available-hours reset` or the web app), which deletes them at once. |
| both privacy copies — the date | the day the PR opens |
| `CONTEXT.md` | **Available Hours** says they are the member's own once saved, 8 AM–11 PM every day until then, and that saving them lets other members list that member's Free Slots by name. **Counted Member** says a member can be counted on a calendar, on saved hours, or on both. |
| `live-e2e-script.md` §12 | New items: the nested group registers, with ten `days` choices and time suggestions; `set`, `off` and `reset` reply with the week, the zone and the notice, and `/settings view` and the web card agree with them; an `off` with nothing saved and an `off` that would leave no day are refused; slots follow saved hours; a member with hours and no calendar is listed as hours only; a role of members in different states shows both fields; the migration is in the test stack's history. The item for naming another member is replaced — **must pass**: a member who has not saved hours is refused with the text of 4.4, linked or not; one who has is listed; and an account from outside the server is refused as that even when it has saved hours (review round 1, S1). |

Left as they are: the Limited Use sentence (a list resting on hours alone uses nothing from
Google); `/help`'s tagline; the Terms; the worked examples.

## 5. What changes, file by file

| File | Change |
|---|---|
| `src/CalCrony.Contracts/Calendar.cs` | `AvailableHoursLimits.StepMinutes` (30); `AvailableHoursDayDto`, `AvailableHoursDto`, `SetAvailableHoursRequest` as in design §3; `FreeSlotsRequest.ByName` and `FreeSlotsResponse.ByName` (R11, R12) — last in each record, read as false when absent on the wire; `FreeSlotsResponse.LookedUntilUtc` (review round 1), absent the same way; `TimeOfDay.Format` (section 9); the two enum summaries that say "not returned until members can save hours" corrected. |
| `src/CalCrony.Api/Services/WeeklyHours.cs` | `TryFromDto`, `Parse`, `Serialize`, `ToDto` (4.1). |
| `src/CalCrony.Api/Data/Entities.cs`, `CalCronyDbContext.cs` | `UserProfile.AvailableHoursJson`, `HasMaxLength(512)`. |
| `src/CalCrony.Api/Data/Migrations/` | `AddAvailableHours`: one nullable column; `Down` drops it. Snapshot updated. |
| `src/CalCrony.Api/Endpoints/SettingsEndpoints.cs` | The three hours routes (4.2). |
| `src/CalCrony.Api/Endpoints/CalendarEndpoints.cs` | The by-name check on the request (4.4). |
| `src/CalCrony.Api/Services/FreeSlotsService.cs` | Stored hours, the two new states, the by-name rule, `ByName` on the answer (4.3, 4.4). |
| `src/CalCrony.Bot/Api/CalCronyApiClient.cs` | `GetAvailableHoursAsync`, `PutAvailableHoursAsync`, `ResetAvailableHoursAsync`. |
| `src/CalCrony.Bot/TimeOfDayText.cs` | `TryParse`, `MaxInputLength`, the 24-hour form (4.6). |
| `src/CalCrony.Bot/AvailableHoursText.cs` (new) | `Apply`, `Describe`, and the reply texts of 4.5 and 4.10 as pure functions. |
| `src/CalCrony.Bot/Api/TimeOfDayAutocompleteHandler.cs` (new) | Suggestions (4.6). |
| `src/CalCrony.Bot/Modules/SettingsModule.cs` | The nested `AvailableHoursGroup` with the module's three context attributes; the `view` line. |
| `src/CalCrony.Bot/FreeSlotTarget.cs` | `ForUser` allows another member and marks the target as by name; a pure check that an answer about someone else was checked by name (R12); the two refusal texts. |
| `src/CalCrony.Bot/Modules/AvailabilityModule.cs` | The two descriptions (R21); `ByName` handed on; the R12 check before the reply. |
| `src/CalCrony.Bot/FreeSlotsEmbedBuilder.cs` | The sentences and the two fields (4.8). |
| `src/CalCrony.Web/Api/CalCronyWebApiClient.cs` | The three client methods. |
| `src/CalCrony.Web/Components/AvailableHoursCard.razor` (new), `Pages/App/UserSettings.razor` | The card and its place on the page (4.9). |
| Docs | The sites of 4.11. |
| Tests | Section 7. |

## 6. Verified code facts (`master` a079441, 2026-11-12)

- `Calendar.cs`: `FreeSlotLimits` 64–90; `AvailableHoursLimits` 95–106 with `MinutesPerDay` and the
  two defaults (unit 1's R4); `FreeSlotsRequest` 116–117; `FreeSlotMemberState` 120–143, all six
  values defined, with "Not returned until members can save hours" on `Counted` and `HoursOnly`;
  `FreeSlotsResponse` 175–181. A body without a property deserializes it to its default
  (`CalendarEndpoints.cs` 201–203 relies on that for `UserIds`).
- `Program.cs`: JSON options add the NodaTime converters only (20–21), so `DayOfWeek` crosses as a
  number; `AddScoped<FreeSlotsService>()` 46; `MapSettingsEndpoints()` 168.
- `SettingsEndpoints.cs`: routes 16–19; the self-only guard 104–107 and 139–142
  (`GuildAccessService.SelfOnly`, `Auth/GuildAccessService.cs` 62–63); `PutUserSettings` 136–196
  finds or adds the row (159–164) and assigns named properties only, so a column it does not name
  is left alone.
- `Entities.cs`: `UserProfile` 87–121. `CalCronyDbContext.cs`: its mapping 78–84 bounds the
  timezone, the username and the avatar hash at 64; the theme is not mapped and is `text` (as
  first written this said every string was bounded — corrected in review round 1); the
  JSON-column precedent `RsvpOptionsJson` is `HasMaxLength(10240)` (124).
- Migrations live in `src/CalCrony.Api/Data/Migrations/`; the latest is
  `20260902152200_AllowMultipleRsvps`, and the test stack's history has 24 rows. `dotnet ef`
  10.0.11 is installed as a global tool, and the repository's `dotnet-tools.json` pins the same
  version (as first written this said there was no tool manifest — corrected in review round 1). The API applies
  pending migrations at start, and the API tests run on a migrated Postgres container.
- `WeeklyHours.cs`: constructor 20–40 (inside one day, start before end, a real weekday),
  `Default` 44–47, `RangeFor` 52–53. `MemberZone.Resolve` is `MemberZone.cs` 18–19.
- `FreeSlotsService.cs`: the read-only profile query 47–51 selects id and timezone; the single
  availability check 78–79; the state loop 83–97 counts `DefaultHours` only and hands
  `WeeklyHours.Default` to the calculation; `StateOf` 121–127.
- `CalendarEndpoints.cs`: `GetFreeSlots` 196–232, its four checks in order and the service call
  228–231.
- `SettingsModule.cs`: the context attributes 10–13; `view` 17–40 with its rule that a failed read
  is an error (25–30); read-modify-write on `dm-reminders` 45–68; `timezone` with autocomplete
  80–95; an enum of choices with `ChoiceDisplay` 193–203. No module in the bot nests a group yet
  (seven `[Group]` attributes, all on top-level modules).
- `ModuleContextTests` takes every type that implements `IInteractionModuleBase` and has a slash
  command (19–23), nested ones included, and requires `CommandContextType`, `IntegrationType` and
  `RequireBotInGuild` of each server module (37–57).
- `FreeSlotTarget.cs`: `ForRole` 20–39; `ForUser` 53–71 with the three refusals in order, the
  third being the caller rule this unit replaces. `AvailabilityModule.SlotsAsync` 118–179: the two
  descriptions 118 and 120, the request built 164–170.
- `FreeSlotsEmbedBuilder.cs`: the three reasons 20–25; `Build` 32–63; `BasisSentence` 93–111;
  `WhyNobodyWasCounted` 118–121; `NotCountedField` 210–242 and `FormatGroup` 250–269 already take
  groups and a budget.
- `TimeOfDayText.Format` gives `8 AM`, `6:30 PM`, `12 AM` and `midnight` for 1440.
  `TimeZoneAutocompleteHandler` (`Api/`) is the handler pattern: a pure `BuildSuggestions`, at
  most 25.
- `CalCronyApiClient.cs`: `GetUserSettingsAsync` 281, `PutUserSettingsAsync` 314,
  `GetFreeSlotsAsync` 496. `CalCronyWebApiClient.cs`: 97, 211; `Unit` results for deletes 118–119.
- `UserSettings.razor`: the two cards 20–79; `ApplySettings` 177–184 is where the loaded timezone
  lands; a save that never loaded is refused (137–143).
- Tests: `AvailabilityCommandShapeTests` builds a module's command tree offline (21–28);
  `ThemeComponentTests` renders `UserSettings` against a capturing HTTP handler (`UseApi`, 311);
  `UserSettingsApiTests` has the web self-only pattern; `FakeCalendarProvider` records each
  free/busy call.
- Docs: `README.md` 42, 83, 90; the Features section is 398 words. `Docs.razor` 232–240 (DM
  reminders), 259–276 (availability), 358 (the `/settings` row). `PRIVACY_POLICY.md` 3, 30–31, 39,
  48–49, 68–95; `Privacy.razor` 8, 39–41, 50–51, 58–62, 70, 94–119. `CONTEXT.md` 35–45.
- Read back from Discord on 2026-11-12, for the test guild: `/availability slots` is registered
  with `who` as a mentionable, `when` at most 100 characters, `days` 1–14 and `duration` 15–1440.

## 7. Execution plan

Layers run top to bottom. Each layer builds and its tests pass before the next starts. A box is
ticked in the turn its step completes.

**0. Baseline**
- [x] 0.1 On `feature/available-hours`: `dotnet build CalCrony.slnx` and `dotnet test CalCrony.slnx`
      green before the first change; record the count against 1,017. — 1,017 green (639 API, 246
      bot, 132 web) on 2026-11-13; the Release build has no errors and the baseline's 22 warnings.

**1. Contracts**
- [x] 1.1 `Calendar.cs`: the changes of section 5, documented in the file's style.

**2. API — the week and its storage**
- [x] 2.1 `WeeklyHours` (4.1). `WeeklyHoursTests`: each check of the table with its message and
      their order; a week round-trips through `Serialize` and `Parse`; the stored text is exactly
      the form of R16; `Parse` gives null for null, blank, text that is not JSON, an unknown
      weekday, a weekday twice, a bad range and an empty list, and reads a time off the step;
      `ToDto` lists Monday first and leaves out days without a range.
- [x] 2.2 The column, its mapping and the migration `AddAvailableHours`, generated with
      `dotnet ef migrations add`; the generated files read through — one nullable column added,
      dropped by `Down`, nothing else in the snapshot's diff. — `20261113002636_AddAvailableHours`;
      the snapshot's one other line is the tool's version stamp (section 9).

**3. API — routes and the lookup**
- [x] 3.1 The hours routes (4.2). `AvailableHoursApiTests`: nothing saved reads as not set with
      the default week, and no row is created; a week round-trips; each refusal of 4.1 through the
      route; `DELETE` clears, is a 204 twice over and creates no row; a stored value that cannot
      be read answers as not set; a web member reads, saves and clears their own and gets 403 on
      each route for another id; a settings write leaves the hours as they were, and an hours
      write leaves timezone, theme and both DM switches as they were (FR14).
- [x] 3.2 `FreeSlotsService` and the route's by-name check (4.3, 4.4). `FreeSlotsApiTests` gains:
      a linked member with saved hours is `Counted` and their slots lie inside those hours;
      saved hours and no calendar is `HoursOnly`, counted, with no provider call;
      one member on saved hours and one on the default → the overlap, each in their own zone;
      a weekday without a range has nothing free;
      hours from midnight to midnight with no calendar → one slot across the range;
      a linked calendar that needs reconnecting is not counted, hours or not;
      by name: another member with saved hours is listed and the answer says it was by name;
      another member without them is refused with the text of 4.4 and the provider is not asked —
      the same for a linked member, an unlinked one and an id CalCrony has never seen;
      the requester by name is listed without saved hours; the same single id without `ByName`
      is answered as before; `ByName` with two ids is refused.

**4. Bot**
- [x] 4.1 `CalCronyApiClient`: the three methods.
- [x] 4.2 `TimeOfDayText.TryParse` (4.6). `TimeOfDayTextTests`: every row of the table, both
      positions for the midnight forms, `24:00` refused in `from`; refused: empty, `25:00`,
      `6:60`, `13pm`, `0pm`, letters, text of 21 characters; a time off the step is read as it is.
- [x] 4.3 `AvailableHoursText` (4.7, 4.10). `AvailableHoursTextTests`: the two example lines; all
      seven days alike; one day; days off last; `Apply` for each of the ten choices, on an empty
      week and over a saved one, and with a null range; each reply and each zone line, with the
      two default times taken from `AvailableHoursLimits`.
- [x] 4.4 `TimeOfDayAutocompleteHandler` (4.6). `TimeOfDayAutocompleteTests`: nothing typed, for
      `from` and for `to`; `6`, `18`, `6:3`, `6p`, `mid`; never more than 25; every value reads
      back through `TryParse` to the time its label shows.
- [x] 4.5 `SettingsModule.AvailableHoursGroup` and the `view` line (4.5). First thing in this
      step: the module with its nested group is built offline, as `AvailabilityCommandShapeTests`
      does it, to see that Discord.Net accepts the shape. `SettingsCommandShapeTests`: `settings`
      keeps its seven subcommands and gains the group with `set`, `off` and `reset`; the option
      types, the ten choices, autocomplete and the length limit on `from` and `to`; every
      description at most 100 characters; `reset`'s description against the limits.
      `ModuleContextTests` passes with the nested type in its list.
- [x] 4.6 `FreeSlotTarget` and `AvailabilityModule` (4.4, R21). `FreeSlotTargetTests`: the caller
      and another member are both handed on, marked by name, a role is not; a bot and a user from
      outside the server are refused first; an answer about someone else is shown only when it
      says it was by name, and one about the caller or a role either way.
      `AvailabilityCommandShapeTests`: the two descriptions.
- [x] 4.7 `FreeSlotsEmbedBuilder` (4.8). `FreeSlotsEmbedBuilderTests`: each sentence of the table;
      unit 1's sentences unchanged when everyone counted is on the default; the first field's two
      lines, their order, and when each is left out; the reworded reason; both fields full stay
      inside 1,024 each and the embed inside 6,000; the worst case builds.

**5. Web**
- [x] 5.1 `CalCronyWebApiClient`: the three methods.
- [x] 5.2 `AvailableHoursCard` and the page (4.9). `AvailableHoursComponentTests`: nothing saved →
      the default week ticked and its note; a saved week → its rows; Save sends the ticked rows
      and nothing else; no day ticked, and a row out of order, each disable Save with their text;
      Reset sends the delete and shows the default; a failed load offers nothing to save; both
      zone sentences; the notice; the page shows the card only once settings have loaded, and its
      own saves still send no hours.

**6. Docs**
- [x] 6.1 `README.md`: the bullet and the two rows (4.11); the word count re-run and at most 400.
- [x] 6.2 `Docs.razor`: the new entry, the reworded sentences and the row. `DocsPageTests`: the
      entry names both surfaces, "only you", the default and the by-name rule; the availability
      entry says a member can be picked once they have saved hours.
- [x] 6.3 Both privacy copies (4.11), read side by side afterwards for the passages they share.
      `ComponentTests`: the privacy page names available hours and what saving them allows.
- [x] 6.4 `CONTEXT.md`: the two terms.
- [x] 6.5 `live-e2e-script.md` §12: the hours items, and the must-pass item replaced.

**7. Verification**
- [x] 7.1 `dotnet build CalCrony.slnx -c Release` without warnings from the new code;
      `dotnet test CalCrony.slnx` green; counts per project recorded against 0.1. — 1,272 green
      on 2026-11-13: 719 API, 400 bot, 153 web (1,017 before: 639, 246, 132). The Release build
      has no errors and no warning from new code.
- [ ] 7.2 The whole diff read against section 4, rule by rule, and against FR3, FR10–FR15 and
      FR17–FR19; then both review agents read the local commits before anything is pushed.
- [ ] 7.3 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 8. Delivery

Approving this plan covers committing to `feature/available-hours` locally as layers complete.
Everything that leaves this machine waits for its own go-ahead, as the workflow plan says.

- [x] 8.1 First commit on the branch: `docs: available hours — the unit 2 plan` (this file,
      `aidlc-state.md`, `audit.md`). The branch starts from `master` a079441 with unit 1's three
      local record commits on top, so they go out with it. Code commits follow by layer.
      — Committed locally on 2026-11-12, with the plan still awaiting approval.
- [ ] 8.2 *Go-ahead:* push and open the PR. Body: what it does; the shape of the change; what
      saving hours allows and where a member is told; the migration and rolling back; behaviour
      for existing servers and members (nothing changes until a member saves hours); test counts.
      `Closes #171`.
- [ ] 8.3 Review: Copilot's rounds if its review starts — the events list is checked two minutes
      after opening; otherwise the two review agents on each commit, as on unit 1.
- [ ] 8.4 *Go-ahead:* squash merge. The test stack picks up `:main` and applies the migration;
      the §12 items are run there, unit 1's still-open ones with them.
- [ ] 8.5 `aidlc-state.md` marks unit 2 merged; build and test follows, then the release.
- [ ] 8.6 *Go-ahead:* release 0.37.0 (PR #175). *Go-ahead:* prod — backup first, then the pinned
      tag; the migration is additive, so rolling the image back stays safe.

For the live pass, because no test can cover them: Discord accepting the nested group at
registration; the suggestions as they appear while typing; the card at phone width; and the lines
in the two modules that no test reaches, the bot's tests having no way to stand in for Discord:

- in `AvailabilityModule.SlotsAsync`: the line that tells a member of this server from anyone
  else Discord can resolve by id — since this unit the one thing that keeps a lookup by name
  inside a server the two share, as the API holds no membership (review round 1, S1); the line
  that hands `ByName` on; and the line that checks the answer before replying. The must-pass
  item of 4.11 covers all three.
- in `SettingsModule`: that `view` appends the hours line, and that `set` and `off` read before
  they write and write nothing when a read fails (R19). The rules themselves — which week a save
  starts from, the two refusals of `off`, every reply — are pure functions with tests.

## 9. Notes from generation

Where the code as built differs from sections 4–7 as approved:

- **`TimeOfDay.Format` in Contracts** is not in the file list. The 12-hour label for a time of day
  moved there from the bot's `TimeOfDayText`, whose `Format` now calls it. The web card writes the
  same labels, and one wording serves both surfaces.
- **The `days` choices** are named `every day`, `weekdays`, `weekends` and the seven weekdays —
  without the parentheses 4.5 gave two of them — so the Docs example `days:weekdays` is what
  Discord shows.
- **A null entry in a submitted week** is refused as `null is not a day of the week.`; the table of
  4.1 did not list the case.
- **The reply to a time that cannot be read** puts the option's name in code style:
  ``I couldn't read the `from` time. Try 6 PM, 6:30pm or 18:30.``
- **The by-name rule is checked before the first day is read** — an order 4.4 did not state. A
  `when` that cannot be read then changes nothing about what the caller is told of the member.
  The check covers every id in the request, though the route lets a by-name request carry one.
- **Suggestions** offer nothing for text longer than a typed time can be, and find noon and
  midnight by those words.
- **The web card**: a row that is unticked when the week is saved keeps the times it showed, so
  ticking it again restores them; after a reset the default week is shown from the limits in
  Contracts, without another read.
- **`MembersField` leaves a field out** when its room cannot hold each line's reason and a count.
  With the limits as they are that cannot happen; it is a guard, not a rule.
- **The snapshot's diff** has one line besides the column: the tool's version stamp, 10.0.11 to
  10.0.15, the EF Core version the project already references.
- **The new web test class** derives from `BunitContext`, the name bUnit now uses, where the older
  classes derive from `TestContext`; the build gains no warning.
- **Privacy dates** are set to 2026-11-13 in both copies; if the PR opens on a later day they move
  with it.

The reply's worst case, measured on the real builder with both fields in use: 3,670 of 4,096
description characters and 5,887 of 6,000 for the whole embed (the first field 1,007, the second
981). Even there the room left for the first field is 1,120, so it gets a field's full 1,024; the
rule that it takes what is left guards against the description growing.

Counts: 1,272 tests green — 719 API, 400 bot, 153 web (1,017 before: 639, 246, 132). The Release
build has no errors and no warning from new code.

Three more differences, found in review round 1 and true since generation:

- **The week's checks run entry by entry**: a fault in the first entry is reported ahead of any
  in the second. 4.1 now says so.
- **A bare `24`** is read as the end of the day in `to`, as `24:00` is. 4.6's table now lists it.
- **The API's refusal for a named member** reaches the member as `❌ That member hasn't saved…`,
  as every refusal from the API does; the bot's own refusals carry no mark.

### Mutation checks (2026-11-13)

Before the review, on an exported copy of `5c7392c` — never the repository — fourteen rules were
taken out one at a time, and each time the tests meant to pin the rule failed: the by-name
refusal; that refusal moved to after the calendars are read; a linked calendar that cannot be
read falling back on saved hours; the answer not saying it was by name; saved hours ignored by
the lookup; a by-name request carrying several members; the self-only guard on each of the three
hours routes; the step on a submitted week; an empty week being saved; the bot showing an
unchecked answer about someone else; a named member not marked as by name; and the bot accepting
a start that is not before its end.

### Review round 1 — two review agents, on `5c7392c` (2026-11-13)

As on unit 1, two review agents read the commit before anything was pushed, one on security and
privacy and one on correctness and quality. Each built it and ran the suite in a copy of its own:
1,272 green and the baseline's 22 warnings, as recorded above. Neither found a wrong rule or a
wrong user-facing text, and the security reviewer found no way around the by-name rule: for a
linked member with an expired token, a refused lookup by name made no refresh call and no
free/busy call; twenty-nine stored values that are not a week broke no lookup; and release 0.36.4
ran against a schema with the new column, as rolling the image back would have it.

| Finding | What was done |
|---|---|
| S1 (low) — The check that a named user is a member of this server is, since this unit, the one thing that keeps a lookup by name inside a server the two share. No automated test reaches the module line that makes it, and its live item sat in a section that may be skipped. | The must-pass live item now includes it: an account from outside the server that has saved hours is refused as that, with no list. Section 8 names the line. The reviewer ran Discord.Net's own option reader on interaction payloads: a resolved member is handed on, anyone else is refused. |
| S2 (low) — In Discord a member learned that saving hours makes them listable by name only from the reply, after the save. | `set`'s description says it (4.5), and a test holds the text. |
| S3 (info) — The notice said what a list by name shows of the hours, not that a linked calendar's busy times are cut out of them. | One clause added to the one wording, wherever it is used: both replies, the web card, the Docs entry and both privacy copies (4.10, 4.11). |
| S4 (info) — Another member's display name reaches the reply's title as typed, markdown characters included. The reply is ephemeral, and nothing in an embed pings. | Not changed: escaping a name without seeing how Discord shows a title could put backslashes into ordinary names. A live item looks at it. |
| Q1 (major) — Three rules of 4.5 were decided inside the command group, where no test reaches: which week a first `set` starts from, and the two refusals of `off`. Taking any of them out passed every test, and the first two would then have saved the default week. | Moved into `AvailableHoursText.Set` and `TryOff`, with tests. The live item for a first `set` now says to run it on an account with nothing saved. |
| Q2 (minor) — A slot still open where the lookup stops looking was shown as ending there. Default hours could not produce one; hours that meet at midnight can. | `FreeSlotsResponse.LookedUntilUtc`, and the reply writes `or later` (4.8). A comment in the calculator said such a slot always shows its real end; corrected. |
| Q3 (minor) — "A linked calendar that cannot be read is not made up for by saved hours" was pinned for a refused refresh only. | A second case, for a provider that could not be reached. |
| Q4 (minor) — Which of the two time options is the end rested on a name that nothing kept in step with the command. | The handler's two option names are constants the command declares its options with, and a test compares them. The live item checks what each empty box offers. |
| Q5 (minor) — The page's test could not tell the saved timezone from one being typed. | The test types a timezone without saving it, and the card still names the saved one. |
| Q6 (nit) — The order of the checks on the two typed times was not pinned. | A test with two faults at a time. |
| Q7 (nit) — 4.8 read as if a test exercised the rule that the first field takes what the embed has left. | 4.8 reworded: with the limits as they are it is a guard, as this section already said. |
| Q8 (nit) — A stored week off the step can be read but not saved again in part. | Not reachable while the step stays as it is; 4.1 says what a change of step would need. |
| Q9 (nit) — Two facts in section 6 were untrue — the tool manifest, and "every string bounded", which R16's reason repeated — and three small differences were missing from this section. | Corrected in place; the three are listed above. |
| Q10 (nit) — A test helper named `Counted` built a member on the default hours; a test of eight stored days could not fail for the reason it named; the route's summary left the by-name refusal out. | The helper is `OnDefaultHours`; the count check in `Parse`, which an eighth entry can never get past the other checks to need, is gone and the test names the reason that is true; the summary lists the refusal. |
| Q11 (nit) — The Docs example sets weekdays; on a first `set` that leaves the weekend off, and the entry did not say. | One sentence in the entry (4.11). |

Noted by the security reviewer and not changed here, being older than this change: the repository
copy of the privacy policy mentions the community Discord server in its contact section, with a
caution about posting personal details there, and the web copy does not.

Counts after round 1: 1,284 tests green — 721 API, 410 bot, 153 web. The Release build has no
errors and the baseline's 22 warnings.

## 10. Plan checklist

- [x] Requirements (FR1–FR19), design (D1–D11), unit 1's plan and section 9, and the code this
      unit touches read at `master` a079441
- [x] Rules written for the week, the routes, the lookup, naming a member, the commands, typed
      times, the reply, the web card and the notices (section 4)
- [x] Refinements recorded with the alternative and the reason (section 3)
- [x] File list, verified facts, steps and delivery gates written (sections 5–8)
- [x] Approved by Joshua — 2026-11-13 (UTC), as written ("Approve the plan")
