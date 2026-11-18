# Free slots count CalCrony events — requirements analysis

**Request**: a member's report in the community Discord on 2026-11-13, brought by Joshua on
2026-11-16. **Depth**: standard — one additive change to the free-slots lookup in the API, the
reply's wording in the bot, and the user-facing texts; no migration is expected.
**Date**: 2026-11-16. **Baseline**: `master` 56b61ab, the code of v0.37.0, which prod runs; the
0.37.0 records have 1,292 tests green at `b5b90d6`, the last code change before it — not re-run
for this analysis.
**Status**: approved by Joshua 2026-11-16 ("Take recommended approach", read as continue — the
audit entry of 2026-11-16T22:46:53Z has the reading). Q1–Q5 answered 2026-11-16 — A, A, A, A, A
("Yes, as recommended"); the assumptions in section 5 were approved with the rest.
**Builds on**: `availability-free-slots-requirements.md` (FR1–FR19). This document adds
FR20–FR31 and says in section 3 which of the earlier requirements it changes.

## 1. Intent

The free-slots feature was asked for by a game master whose players pick a session time from the
times the GM is free. The loop is: look up the GM's free slots, agree on one, book the session as
a CalCrony event. After that the booked time is no longer free, but `/availability slots` still
lists it. The member's report, shortly after 0.37.0 went live: it seems to be working, but the
command does not seem to read CalCrony events, so it shows them as free for a slot that is
already booked.

The report is right. The lookup knows two things about a member: the hours they say they are
available, and the busy blocks of their linked Google calendar. It does not know the events
scheduled in CalCrony itself. A member can work around that only by also putting the session on
their own Google calendar, and a member counted on saved hours alone cannot block a date at all,
since hours are one range per weekday.

This was not a decision, and it is not a defect in the code. The earlier requirements name the
linked calendar as the only source of busy time (FR2), and CalCrony's own events were raised
nowhere in the requirements, the design or the plans; the out-of-scope list does not name them
either. The code does what was approved. This change closes that gap in the requirements.

## 2. What exists today (verified against the code at 56b61ab)

| Site | Today | For this change |
|---|---|---|
| `FreeSlotsService.GetAsync` (`src/CalCrony.Api/Services/FreeSlotsService.cs:47`) | Reads the server's zone and each member's zone and saved hours (`:54`–`:63`). Refuses a lookup by name of a member without saved hours before anything else about them is read (`:65`–`:73`). Takes busy blocks from one call, `CalendarAvailabilityService.CheckAsync` (`:101`), and hands them to the calculation (`:116`–`:120`). Reads no event, RSVP or series. | Gains a second source of busy time for the members it counts, read after the by-name refusal. |
| `CalendarAvailabilityService.CheckAsync`, `GoogleCalendarProvider` (`src/CalCrony.Api/Services/GoogleCalendarProvider.cs:120`, `:133`) | One free/busy query per linked member, about the `primary` calendar only. A member with no linked calendar gets no busy blocks. | Unchanged. |
| `FreeSlotCalculator` (`src/CalCrony.Api/Services/FreeSlotCalculator.cs:19`, `:64`) | Pure: takes each counted member's zone, hours and busy intervals, which may come in any order, overlap, touch or reach outside the window. | Unchanged; event times join the busy intervals. |
| `FreeSlotsResponse` (`src/CalCrony.Contracts/Calendar.cs:244`) | Carries `ByName` and `LookedUntilUtc` as defaulted fields, so a bot and an API of different versions still understand each other. | One more defaulted field: whether CalCrony events were counted. |
| `FreeSlotsEmbedBuilder.BasisSentence` (`src/CalCrony.Bot/FreeSlotsEmbedBuilder.cs:142`–`:202`) | One sentence on what the slots rest on: available hours and linked calendars. For a member on hours alone: "No calendar is linked, so anything else they have on isn't taken into account." | Reworded in every form (FR27). |
| `Event` (`src/CalCrony.Api/Data/Entities.cs:130`) | `GuildId`, `CreatorId`, `StartsAt`, an optional `DurationMinutes`, `Status`, an optional `SeriesId`. Where an end is needed and no length is set, 60 minutes is used (`FeedEndpoints.cs:136`, `CalendarEndpoints.cs:281`). A length may be up to four weeks (`FieldLimits.MaxMinutes`, 40320). | Read, not changed. |
| `EventStatus` (`src/CalCrony.Contracts/Events.cs:6`) | Scheduled, Started, Ended, Cancelled. Skipping an occurrence cancels it; deleting an event removes the row. | Only scheduled and started events count. |
| Creating an event (`src/CalCrony.Api/Endpoints/EventEndpoints.cs:1557`) | The only place an RSVP row is created is `PutRsvp`: creating an event adds no RSVP for its creator. | So a game master is tied to their own session only as its creator. |
| `RsvpOption.IsAttending`, `Rsvp.Waitlisted` (`Entities.cs:325`, `:353`) | Exactly one option per event is the attending one: "Going" in the default set of Going, Not going, Maybe (`EventEndpoints.cs:112`). A waitlisted RSVP holds no seat. An event that allows several RSVPs can hold several rows for one member. | "Holds a seat on the attending option" is the test for an attendee. |
| `EventSeries` (`Entities.cs:192`) | A running series has exactly one live occurrence as a row; the next is created when that one ends. Later occurrences are computed where they are shown, by `RecurrenceCalculator.NextOccurrence` (`src/CalCrony.Api/Services/RecurrenceCalculator.cs:96`), from the anchor date, the day set, the until date and the count. Each occurrence takes the series' creator (`SeriesMaterializer.cs:48`) and starts with no RSVPs. | Later occurrences are computed for the lookup's days and tied to the creator. |
| Indexes (`src/CalCrony.Api/Data/CalCronyDbContext.cs:95`, `:126`, `:166`) | Events by server and start; series by server; RSVPs by event, member and option. | No new index is expected; decided at application design. |
| `/availability role`, `/availability event`, the web's event grid | All rest on the same availability check and count no CalCrony event. | Unchanged (Q5 A). |
| Who can already see events (`EventEndpoints.cs:21`, `:245`) | The web lists a server's events, with their RSVPs, to every member of that server; in Discord the event's message shows them to whoever can see its channel. | The privacy side of this change starts from here (section 7). |
| Texts | The reply's sentences; `Docs.razor:260`–`:291`; `README.md:42`, `:90`; `PRIVACY_POLICY.md:30`–`:36` and `Privacy.razor`; the saved-hours notice (`src/CalCrony.Bot/AvailableHoursText.cs:49`) and the web card; `CONTEXT.md:36`, `:40`; section 12 of the live E2E script. | Each says what slots rest on, or what saving hours allows, and changes with FR27 and FR30. |
| Tests | `FreeSlotsApiTests` has 32 tests and none creates an event; `FreeSlotsEmbedBuilderTests` pins the reply's sentences and `DocsPageTests` the Docs wording. | New cases (FR31); pinned sentences are updated on purpose. |

## 3. Functional requirements

**Busy time from CalCrony events**

- **FR20** For every member a free-slots lookup counts, the time of CalCrony events in the server
  the command runs in counts as busy, beside any busy block from a linked calendar (Q1 A, Q4 A).
- **FR21** An event counts for a member when the member created it, or holds a seat on its
  attending option — an RSVP that is not waitlisted. An RSVP on any other option does not count,
  and does not release a creator (Q2 A). Several RSVPs of one member on one event count once.
- **FR22** An event is busy from its start to its end. One with no length set counts as 60
  minutes, as everywhere else. Only live events count: scheduled or started. A cancelled, deleted
  or ended event blocks nothing.
- **FR23** For a running recurring series, the occurrences after the one that exists count as
  busy for the series' creator, each at the series' start time and length, on the days the series
  repeats on, for as long as its until date or count allows, inside the days looked at (Q3 A).
  Nobody else is tied to an occurrence that does not exist yet, because RSVPs start fresh on each
  occurrence.
- **FR24** Events never change who is counted. FR3 stands as it is: a member with neither saved
  hours nor a readable calendar stays not counted, whatever events they have. A member counted
  on hours alone is now cut by their events too.
- **FR25** The by-name rule is untouched (FR19, D11): a member named without saved hours is
  refused before anything about them is read, events included.
- **FR26** Events of other servers are never read (Q4 A). Nothing about an event except its time
  enters the answer: the reply shows no title, channel or attendee, only the slots that remain.
- **FR27** The reply says what the slots rest on, now including this server's CalCrony events, in
  every form of its basis sentence. The sentence for a member on hours alone no longer says that
  nothing else is taken into account. The reply stays truthful when the bot and the API are of
  different versions: it says events were counted only when the API says so.
- **FR28** `/availability role`, `/availability event` and the web's availability grid behave
  exactly as they do today, and the Docs say that only free slots count CalCrony events (Q5 A).
- **FR29** No new stored data, and no new Google scope, Discord permission or intent; ADR 0003
  holds. No migration is expected; if application design finds an index is needed, it is
  additive and rolling the image back stays safe. The lookup costs a small, fixed number of extra
  database reads per command, not multiplied by days or by members, and no extra Google call.
- **FR30** Docs: the README bullet (inside the 400-word cap) and the command row; the Docs
  page's availability and available-hours entries; both privacy copies and their date; the
  saved-hours notice in the bot and on the web card; the glossary terms; the live E2E items.
  Every place that says what saving hours allows (FR10) now also says that a list by name shows
  when the member is busy with that server's CalCrony events.
- **FR31** Tests:
  - a creator's event cuts a slot, with and without an RSVP of their own; a seat on the attending
    option cuts;
  - a waitlisted RSVP, an RSVP on another option, and a cancelled, ended, deleted or other-server
    event do not;
  - no length is 60 minutes; several RSVPs count once; an event that began before the first day
    and runs into it; clipping at the lookup's edges;
  - a member on hours alone is cut; a member with neither hours nor a calendar stays not counted;
    the by-name refusal reads no event;
  - a series with a day set, with an until date, with a count, and one that is stopped;
  - a role lookup whose members are blocked by different events;
  - the reply's sentences in each form, and when the API does not say events were counted; the
    Docs wording.

**Earlier requirements this changes**

- **FR2** — a slot is a stretch during which every counted member is inside their own available
  hours, has no busy block on a linked calendar *and no CalCrony event of the server that counts
  for them (FR20–FR23)*, at least the minimum length long.
- **FR6** — the reply states the range, the minimum length and whose hours, calendars *and
  events* it rests on.
- **FR10** — what saving hours allows is restated as in FR30.
- **FR17**, **FR18** — extended by FR30 and FR31.
- FR3, FR7, FR8, FR16 and FR19 hold as written: who is counted, one Google query per linked
  member, the other commands unchanged, nothing from Google stored, and the by-name rule.

## 4. Design questions (answered)

Put to Joshua on 2026-11-16 with the finding; answered the same day with "Yes, as recommended".

**Q1 — Handle it?**

- A) Count CalCrony events in free slots. **(Recommended.)**
- B) State the limit in the reply and the Docs only.
- C) Leave it as it is.

[Answer]: A — the recommended option; Joshua, 2026-11-16.

**Q2 — Whose events block a member's slots?**

- A) Events they created, and events where they hold a seat on the attending option ("Going" in
  the default set). **(Recommended.)**
- B) Created events only: the reported case and the smallest change.
- C) As A, plus a seat on any other option. This covers sets like Tank/Healer/DPS, but "Maybe"
  and "Not going" would block too.
- D) As A, but a creator whose own RSVP is on another option, such as "Not going", is not
  blocked.

[Answer]: A — the recommended option; Joshua, 2026-11-16.

**Q3 — Recurring sessions?**

- A) Count a running series' later occurrences for its creator. **(Recommended.)**
- B) Count only the occurrence that already exists. The session after the next one would still
  show as free.

[Answer]: A — the recommended option; Joshua, 2026-11-16.

**Q4 — Other servers?**

- A) This server's events only. **(Recommended.)**
- B) Every server on the instance. A list in one server would then show busy time that comes
  from another.

[Answer]: A — the recommended option; Joshua, 2026-11-16.

**Q5 — The other availability commands?**

- A) Leave `/availability role` and `/availability event` as they are, and say in the Docs that
  only `slots` counts CalCrony events. **(Recommended.)**
- B) Change `/availability role` in the same work.

[Answer]: A — the recommended option; Joshua, 2026-11-16.

## 5. Assumptions (accepted unless Joshua says otherwise)

- **A started event** blocks until its end, although its start is past.
- **Long events**: an event may be up to four weeks long and blocks every slot inside it, so the
  lookup also finds events that began before the first day.
- **Who the creator is**: whoever created the event, including one made for somebody else and a
  time poll turned into an event, where it is whoever converted it. There is no host field.
- **A creator who is not going stays blocked** (Q2 A). This change gives them no way out. The
  Docs say so, and section 6 names what would.
- **Custom option sets**: only the option marked as attending counts, so a member seated on
  another role of a Tank/Healer/DPS set is not blocked. The Docs say so.
- **Every lookup is live**: an event that is moved, cancelled, deleted or ended shows in the next
  lookup; nothing is cached.
- **Later occurrences of a series** are computed with the calculation the ICS feed and the
  materializer use, day set included, and take the series' own length. A change made to one
  occurrence only does not move the later ones. A series that is stopped, or whose count is used
  up, has none.
- **Role lookups** follow the same rule for each counted member, and the events of all of them
  are read together.
- **Wording** is drafted in the code-generation plan and approved with it. The direction: "Free
  on their linked calendar and clear of this server's CalCrony events, inside the available hours
  they saved"; for a member on hours alone, that no calendar is linked, so nothing outside
  CalCrony is taken into account.
- **The API route and the command stay as they are**: same options, same limits, same bot-only
  route. No web view of slots.
- **Delivery**: one unit and one pull request, titled as a `feat:` because members get new
  behaviour; release 0.38.0; rollout by the standing backup-then-upgrade procedure, with a
  release summary. A tracking issue is proposed at workflow planning. Nothing is pushed, opened,
  merged or released without a go-ahead.
- **Size** (an estimate, not a measurement): one unit, smaller than either unit of #171.

## 6. Out of scope (candidates for follow-up issues)

- `/availability role`, `/availability event` and the web grid counting CalCrony events (Q5 B).
- Events of other servers (Q4 B).
- A setting per RSVP option for "counts as attending", so that every role of a custom set
  blocks; a way for a creator to say they are not attending.
- Tying attendees to occurrences of a series that do not exist yet.
- Saying in the reply why a time is not free.
- The public calendar's view of series that repeat on several weekdays, which is being checked
  as a separate piece of work.
- Writing CalCrony events into a member's Google calendar (ADR 0003 rules it out).

## 7. Risks

- **A creator who does not attend stays blocked**, for example a moderator who schedules for
  others. Stated in the Docs; the remedy is the per-option setting of section 6.
- **Custom option sets are covered in part**: only the attending option counts.
- **What a list newly shows.** A list of someone's free slots now has gaps where they host or
  attend an event of that server. Those events and their RSVPs are already open to the server's
  members on the web, so it is nothing new in kind, but a member who cannot see an event's
  channel in Discord would still see the gap. A list by name is still limited to members who
  saved hours (FR19), the notice they see when saving is reworded (FR30), and both privacy
  copies say it.
- **A role reaches members who saved no hours** *(added in the review, 2026-11-17)*. A role
  lookup leaves out each counted member's events too, also for a member counted on a linked
  calendar without saved hours; a role one member holds then shows that member's event times.
  D11 let a role through because it reaches what the availability grid reaches; it now reaches a
  little further, to information every member of the server can already read on the web.
- **Members who saved hours before this change** *(added in the review, 2026-11-17)* agreed to a
  notice that did not mention events. The 0.38.0 release summary, which goes to the community
  Discord, tells them, and how to stop lists by name.
- **The two kinds of lookup can disagree**: `/availability role` may show a member free at a
  time `slots` leaves out. The Docs say which command counts what.
- **Series far ahead.** A lookup may start up to a year ahead, where everything recurring is
  computed and nothing exists as a row. The work per series is bounded at application design.
- **Bot and API of different versions**, for the seconds between their restarts: the defaulted
  field keeps the reply from claiming more than was counted (FR27).
- **Other work in flight** changes the same records, and two fixes change the date parser. This
  change does not touch the parser; it takes `master` in before its pull request.
