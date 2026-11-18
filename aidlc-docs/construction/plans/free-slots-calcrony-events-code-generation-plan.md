# Free slots count CalCrony events (`free-slots-calcrony-events`): design, rules and code-generation plan

**Change**: free slots count this server's CalCrony events as busy time (a member's report in the
community Discord, 2026-11-13). **Inputs**: the approved requirements
(`inception/requirements/free-slots-calcrony-events-requirements.md`, FR20–FR31; Q1–Q5: A, A, A,
A, A), the approved workflow plan (`inception/plans/free-slots-calcrony-events-workflow-plan.md`),
and the design and plans of free slots and available hours
(`inception/application-design/availability-free-slots-design.md`, D1–D11;
`construction/plans/free-slots-code-generation-plan.md` and
`available-hours-code-generation-plan.md`, R1–R22).
**Baseline**: `master` 4af1305 — the code of v0.37.0, which prod runs. The suite is re-run as
step 0.2 and its counts are recorded in section 9.
**Branch**: `feature/free-slots-calcrony-events` → PR `feat: free slots count this server's
CalCrony events`.
**Status**: approved by Joshua on 2026-11-17, as written ("Continue"); generated on 2026-11-17 and
reviewed in four rounds (section 9); the completion gate was answered "Continue" on 2026-11-18,
and the branch was rebuilt as one commit on `master` before the first push (section 9).

By the workflow plan, this file is the unit's application design (section 3), its functional
design (section 4) and its code-generation plan (sections 5–8), approved together.

## 1. What this unit ships

`/availability slots` leaves out the time a counted member is busy with a CalCrony event of the
server the command runs in: an event they created, an event where they hold a seat on the
attending option, and — for the member who created a repeating event — its later dates. The
reply's sentence on what the slots rest on says so, and so does every text that describes a
slot or says what a list by name shows.

No migration, no index, no configuration, no new stored data, no new Google scope, Discord
permission or intent (FR29). `/availability role`, `/availability event` and the web's
availability grid are not touched (FR28). The route, the command and its options stay as they
are.

Requirements covered: FR20–FR31, with FR2, FR6 and FR10 as changed and FR17 and FR18 as extended.

## 2. Carried in (locked)

| From | Decision |
|---|---|
| Q1, Q4 | This server's CalCrony events count as busy for every member a lookup counts; other servers' events are never read. |
| Q2 | An event counts for the member who created it and for a member holding a seat on its attending option. A waitlisted RSVP and an RSVP on another option do not count, and an RSVP on another option does not release a creator. |
| Q3 | A running series' later occurrences count for the series' creator, and for nobody else. |
| Q5 | `/availability role`, `/availability event` and the web grid stay as they are; the Docs say that only `slots` counts CalCrony events. |
| FR22 | Busy from start to end; 60 minutes when no length is set; only scheduled and started events. |
| FR24 | Events never change who is counted. |
| FR25, D11 | A member named without saved hours is refused before anything about them is read, events included. |
| FR26 | Nothing but an event's time enters the answer. |
| FR27 | The reply is truthful when the bot and the API are of different versions. |
| D4 | The slot calculation takes each member's zone, week of hours and busy intervals, and does not change. |

## 3. Design decisions made here

Each is a choice the requirements left to design, numbered on from the design document's list.
The rejected alternative is named so that it can be overruled. On approval they are added to
the design document as an amendment, the way D11 was (step 0.3).

| # | Decision | Instead of | Why |
|---|---|---|---|
| D12 | A new service in the API, `EventBusyTimes`, gives the busy intervals of CalCrony events per member. `FreeSlotsService` asks it once per lookup, for the members it counts, and adds the intervals to each member's calendar blocks before the calculation. | Reading events inside `CalendarAvailabilityService`, or inside the calculation | That service also answers `/availability role`, `/availability event` and the web grid, which stay as they are (Q5 A); the calculation is pure and already takes busy intervals in any order, overlapping or reaching outside the window. |
| D13 | At most five database reads are added to a lookup, none of them per member, per day or per event: the server's live events that can reach into the days looked at; the counted members' seats on those events; the options of the events that have such a seat; the running series created by a counted member; and those series' live occurrences. Only ids, times, lengths, statuses and schedule fields are selected — no title, description, location or channel. | One joined query, or reading every live event of the server | A joined query multiplies rows (events × options × seats). Every live event would be a year of events for a lookup that starts a year ahead. |
| D14 | No new index and no migration. The reads use the indexes that exist: events by server and start, RSVPs by event, options by event, series by server, and the live occurrence of a series. | An index on a series' or an event's creator | The reads are already bounded by the server and the range; the creator is checked on the rows that come back. |
| D15 | An event can reach into the days looked at only if it starts before they end and no earlier than four weeks before they begin — the longest length an event can be given (`FieldLimits.MaxMinutes`, checked on every length since 2026-07-19). | Computing each event's end in SQL | The start has an index, the end does not; four weeks back is a bounded range. |
| D16 | Who an event counts for is decided on the rows read: its creator, and a member with a non-waitlisted RSVP on the option `RsvpPolicy.AttendingOption` names — the flagged option, else the first by sort order. | Filtering on the option's flag in SQL | `AttendingOption` is the one rule every other "who is going" use follows; a second rule in SQL could drift from it. |
| D17 | A running series' later occurrences come from a new pure function, `SeriesProjection.LaterOccurrences`. It steps the schedule with `RecurrenceCalculator.NextOccurrence`, day set included, from the series' slot cursor: the first occurrence after the end of the live one, each later one after the end of the one before it — what the scheduler does when an occurrence ends. It honours the until date and the count that is left, takes the series' own length, and stops at the end of the days looked at, or after 1,000 steps. | Reusing or changing `PublicCalendarBuilder.ProjectOccurrences` | That function steps from the present only, leaves out the day set, yields starts without ends, and is being changed by other work in flight. One projection for the feed, the public calendar and free slots is a candidate for a follow-up. |
| D18 | The answer carries one more defaulted field, `EventsCounted`. This API sets it on every answer; an answer without it reads as false. The bot's sentences name CalCrony events only when it is set, and are today's sentences, word for word, when it is not. | A version check, or always naming events | The same pattern as `ByName` and `LookedUntilUtc`: a bot and an API of different versions understand each other, and the reply claims only what the API says it did (FR27). |
| D19 | Nothing is cached and nothing is stored: every lookup reads the events as they are (FR29). | A cache of busy time per member | An event that is moved, cancelled or deleted must show in the next lookup. |

The bound in D17: a lookup ends at most 366 days and 15 days from today, and a schedule's
smallest step is a day, so a series has fewer than 400 occurrences between now and the end of
the days looked at. The stop at 1,000 steps cannot be reached by a lookup the route accepts; it
is there so that the loop is bounded on its own, as the public calendar's is.

## 4. Rules

### 4.1 The order of a lookup

1. The by-name rule, as today and first: a member named without saved hours is refused from the
   profile row alone (`FreeSlotsService.cs:69`). Nothing else has been read by then.
2. The first day, the window and the calendars, as today. The window is the one the calendars
   are asked for (`FreeSlotCalculator.QueryWindow`).
3. Who is counted, as today (`StateOf`). Events have no part in it (FR24).
4. **New**: if anyone is counted, `EventBusyTimes` is asked for the counted members only. If
   nobody is counted, no event, RSVP or series is read.
5. The calculation, unchanged, on each member's calendar blocks and event intervals together.
6. The answer, with `EventsCounted` set.

### 4.2 Which events are read

- Only events of the server in the request (`GuildId`), never another server's (FR26).
- Only live events: scheduled or started. Ended and cancelled events are not read, and a deleted
  event has no row (FR22).
- Only events that start before the window ends and later than four weeks before it starts
  (D15). Of those, an event matters when it ends after the window starts.
- An event is busy from its start to its start plus its length, 60 minutes when none is set —
  the scheduler's own default, `DeliveryScheduler.DefaultEventLengthMinutes`, made internal and
  used here rather than written a second time.
- A started event counts until its end. An interval may begin before the window or end after
  it; the calculation clips it.

### 4.3 Who an event counts for

| Member's tie to the event | Counts |
|---|---|
| Created it (`Event.CreatorId`), with or without an RSVP of their own | yes |
| Created it and RSVP'd on another option, such as "Not going" | yes |
| Holds a seat on the attending option — an RSVP there that is not waitlisted | yes |
| Waitlisted on the attending option | no |
| RSVP on any other option | no |
| Several RSVPs on one event (an event that allows them) | once |
| Both created it and holds a seat | once |
| No tie | no |

Only the counted members' RSVPs are read. A member who is not counted stays not counted
whatever events they have (FR24).

### 4.4 Later occurrences of a series

For each running series of the server whose creator is a counted member:

- Nothing when the series is stopped (`Ended`, not read at all) or its count is used up
  (`OccurrenceCount` has reached `MaxOccurrences`).
- The occurrence that exists is an event row and is counted under 4.2 and 4.3 like any other
  event — for its creator and for whoever holds a seat in it. It is never repeated here.
- The first later occurrence is the first slot of the schedule after the series' cursor
  (`CurrentOccurrenceDate`) that starts after the live occurrence ends, or after now when there
  is no live occurrence or it has already ended. Each one after that is the first slot that
  starts after the one before it ends. This is what the scheduler will do, since it creates the
  next occurrence when the live one ends.
- Each occurrence starts at the series' start time in the series' zone, on the days the series
  repeats on, and lasts the series' length, 60 minutes when none is set.
- None after the until date. With a count, only as many as are left, and the ones that fall
  before the days looked at use up the count as well.
- Those that end after the window starts and start before it ends count as busy for the
  series' creator, and for nobody else (FR23).

Two cases that follow from the third point. A weekly session whose current occurrence was moved
by hand to a date past next week's slot: next week's slot will never exist, so it does not
block. A change made to one occurrence only does not move the later ones.

### 4.5 What the API answers

`FreeSlotsResponse` gains `EventsCounted` (D18), true on every answer of this API. Nothing else
in the answer changes: no field says which events were counted, or why a time is not free
(FR26).

### 4.6 The reply's sentences

The first column is today's sentence, kept word for word for an answer that does not say events
were counted. The second is the sentence when it does. "Their" events, not "this server's": an
event blocks the members it counts for, not everyone, and the requirements' draft wording could
be read as the second (refinement R23).

| Form | Today, and when `EventsCounted` is not set | When `EventsCounted` is set |
|---|---|---|
| One member asked about, default hours | Free on their linked calendar, between 8 AM and 11 PM in their timezone (the server's if they haven't set one). | Free on their linked calendar and clear of their CalCrony events in this server, between 8 AM and 11 PM in their timezone (the server's if they haven't set one). |
| Several asked about, one counted, default hours | When the one counted member is free on their linked calendar, between 8 AM and 11 PM in their timezone (the server's if they haven't set one). | When the one counted member is free on their linked calendar and clear of their CalCrony events in this server, between 8 AM and 11 PM in their timezone (the server's if they haven't set one). |
| Several counted, all on default hours | When all 3 counted members are free on their linked calendars, each between 8 AM and 11 PM in their own timezone (the server's for anyone who hasn't set one). | When all 3 counted members are free on their linked calendars and clear of their own CalCrony events in this server, each between 8 AM and 11 PM in their own timezone (the server's for anyone who hasn't set one). |
| One asked about, calendar and saved hours | Free on their linked calendar, inside the available hours they saved, read in their timezone (the server's if they haven't set one). | Free on their linked calendar and clear of their CalCrony events in this server, inside the available hours they saved, read in their timezone (the server's if they haven't set one). |
| One asked about, saved hours alone | Inside the available hours they saved, read in their timezone (the server's if they haven't set one). No calendar is linked, so anything else they have on isn't taken into account. | Inside the available hours they saved, read in their timezone (the server's if they haven't set one), and clear of their CalCrony events in this server. No calendar is linked, so those events are all that can show them as busy. |
| Several asked about, one counted, calendar and saved hours | When the one counted member is free on their linked calendar, inside the available hours they saved, read in their timezone (the server's if they haven't set one). | When the one counted member is free on their linked calendar and clear of their CalCrony events in this server, inside the available hours they saved, read in their timezone (the server's if they haven't set one). |
| Several asked about, one counted, saved hours alone | Inside the one counted member's saved available hours, read in their timezone (the server's if they haven't set one). They have no calendar linked, so anything else they have on isn't taken into account. | Inside the one counted member's saved available hours, read in their timezone (the server's if they haven't set one), and clear of their CalCrony events in this server. They have no calendar linked, so those events are all that can show them as busy. |
| Several counted, some with saved hours | When all 3 counted members are free: each inside their available hours (8 AM–11 PM for anyone who hasn't saved theirs), read in their own timezone (the server's for anyone who hasn't set one), with nothing on a linked calendar. | When all 3 counted members are free: each inside their available hours (8 AM–11 PM for anyone who hasn't saved theirs), read in their own timezone (the server's for anyone who hasn't set one), with nothing on a linked calendar and no CalCrony event of their own in this server. |
| Nobody counted | Nobody could be counted, so there are no slots to list. … | The same: events never change who is counted. |

The parenthesis in the last "several counted" row appears only when a counted member is on the
default hours, as today. The fields **Counted with less to go on** and **Not counted**, the day
lines, the footer and the hint for a shorter minimum do not change.

### 4.7 What a list by name shows

One wording, in the bot and on the web card, for what saving hours allows. Today it says that a
list by name shows roughly the hours you keep "and, if your calendar is linked, when you're
busy inside them". From this change:

> Saved hours can be looked up by name: members of the servers you share with CalCrony can list
> your free slots with `/availability slots`, which shows roughly the hours you keep and when
> you're busy inside them: with your CalCrony events in that server and, if your calendar is
> linked, with what's on your calendar. `/settings available-hours reset` takes that back.

The web card ends "Reset to default takes that back." instead, as today.

The short mentions stay as they are, because they say that a list by name is possible and not
what it shows: the line in `/settings view`, the reply to `reset`, and the description of
`/settings available-hours set`, which is 90 of the 100 characters Discord allows (refinement
R25).

### 4.8 Docs copy

**Docs page, the availability entry.** The sentence that says what a slot is ends "with nothing
on a linked calendar." It becomes:

> … with nothing on a linked calendar and none of their CalCrony events in this server. An event
> counts for the member who created it and for members holding a seat on its attending option
> (Going by default): a waitlisted RSVP or one on another option doesn't count, and whoever
> created the event stays busy even if they RSVP elsewhere. A repeating event's later dates
> count for the member who created it. Only `slots` counts CalCrony events:
> `/availability role`, `/availability event` and the grid show linked calendars alone.

**Docs page, the available-hours entry.** Its last sentence becomes: "Saving hours also lets
members of the servers you share with CalCrony list your free slots by name, which shows roughly
the hours you keep and when you're busy inside them: with your CalCrony events in that server
and, if your calendar is linked, with what's on your calendar; `reset` takes that back."

**Privacy, both copies, and their date.** In "Preferences", the passage on available hours:
"… saving them lets members of the servers you share with CalCrony list your free slots by name,
which shows roughly the hours you keep and when you are busy inside them — with the CalCrony
events of that server that you created or hold a seat in and, if your calendar is linked, with
what is on your calendar — and resetting them takes that back". In "Content you create", one
sentence is added: "In a server's free-slot lists, the times of that server's events you created
or hold a seat in count as busy time for you; a list shows no title, channel or attendee."
(Refinement R26.)

**README.** The Features section is at exactly 400 words, its cap, so the bullet is reworded
inside its own length: "Google Calendar, free/busy scope only (`/availability`): role or event
grids; member or role free slots within available hours, around CalCrony events." The command
row gains "and outside this server's CalCrony events they created or hold a seat in" after the
available hours.

**Glossary (`CONTEXT.md`).** *Free Slot*: "… inside their Available Hours with nothing on a
linked calendar and no CalCrony event of the server that counts for them — one they created, or
one they hold a seat in on its attending option." *Counted Member* gains: "CalCrony events cut a
Counted Member's time; they never make a member counted."

**Live items**, a new block in section 12 of `live-e2e-script.md`, "Free slots count CalCrony
events (v0.38.0)":

- As a member with saved hours, create an event inside those hours; `/availability slots
  who:<yourself>` no longer lists that time, and the sentence names CalCrony events.
- A second account with saved hours RSVPs "Going": listed by name, its slots have the gap. On
  "Maybe" or "Not going" the time is back. Waitlisted on a full event, the time stays free.
- The creator RSVPs "Not going" to their own event: the time stays out of their list.
- Cancel the event, or delete it: the time is back on the next lookup.
- A weekly repeating event: the creator's list for the week after next (`when:` a date) has the
  gap; the second account's list does not, until it RSVPs to that occurrence.
- An event in another server that shares the bot and the member does not cut the list here.
- `/availability role` and `/availability event` are as before: the grid shows calendars alone.
- The item marked **Must pass** still passes: a member named without saved hours is refused.

### 4.9 The release summary

`docs/release-notes/0.38.0.md`, checked with the repository's tool (step 5.4):

```markdown
### What's new

- **Free slots count CalCrony events.** `/availability slots` no longer lists a time when a member is busy with a CalCrony event in this server: one they created, or one they are going to.

### How to use it

1. Run `/availability slots who:@GM` as before. A session the GM has scheduled in CalCrony is now left out of their free time.
2. An event counts for whoever created it and for members holding a seat on its attending option ("Going" by default). A waitlisted RSVP or any other answer does not block.
3. A repeating event also blocks its later dates for the member who created it.
```

### 4.10 Refinements made here

| # | Refinement | Why |
|---|---|---|
| R23 | The reply says "their CalCrony events in this server", where the requirements' draft said "this server's CalCrony events". | An event blocks only the members it counts for. |
| R24 | For a member on saved hours alone the reply ends "those events are all that can show them as busy", where the draft said that nothing outside CalCrony is taken into account. | CalCrony events of other servers are not counted either (Q4 A), so "outside CalCrony" would say too much. |
| R25 | FR30's "every place that says what saving hours allows" is applied to the places that say what a list by name shows: the notice in the bot, the web card, the Docs entry and both privacy copies. The one-line mentions stay. | They do not say what a list shows today either, and the command description has ten characters left. |
| R26 | Both privacy copies gain one sentence under "Content you create" on this use of event times and seats. | The policy names what is stored there; this is a new use of it that members can see the effect of. |
| R27 | "The by-name refusal reads no event" and "the reads do not grow with days or members" (FR29, FR31) are tested by recording the database commands a lookup sends, with a recorder added to the calendar test fixture through EF Core's `ConfigureDbContext`. | Neither can be seen in the answer; without the recorder they would rest on reading the code. |
| R28 | The later occurrences of a series are the ones the scheduler will create (D17), which is narrower than "every slot of the schedule" where the live occurrence was moved past later slots, or an occurrence outlasts the gap to the next. | A slot that will never become an event should not block. |

## 5. What changes, file by file

| Layer | File | Change |
|---|---|---|
| Contracts | `src/CalCrony.Contracts/Calendar.cs` | `FreeSlotsResponse` gains `EventsCounted` (defaulted), documented in the file's style. |
| API | `src/CalCrony.Api/Services/SeriesProjection.cs` (new) | The pure function of D17 and the small record of schedule fields it takes. |
| API | `src/CalCrony.Api/Services/EventBusyTimes.cs` (new) | The reads of D13 and the rule of 4.3. |
| API | `src/CalCrony.Api/Services/FreeSlotsService.cs` | Takes `EventBusyTimes`; the order of 4.1; `EventsCounted`; its summary. |
| API | `src/CalCrony.Api/Services/FreeSlotCalculator.cs` | The comment on a member's busy intervals only. |
| API | `src/CalCrony.Api/Services/DeliveryScheduler.cs` | `DefaultEventLengthMinutes` from private to internal. |
| API | `src/CalCrony.Api/Program.cs` | Registers `EventBusyTimes`. |
| Bot | `src/CalCrony.Bot/FreeSlotsEmbedBuilder.cs` | The sentences of 4.6. |
| Bot | `src/CalCrony.Bot/AvailableHoursText.cs` | `SavedNotice` (4.7). |
| Bot | `src/CalCrony.Bot/Modules/AvailabilityModule.cs` | The comment on `SlotsAsync` only. |
| Web | `src/CalCrony.Web/Components/AvailableHoursCard.razor` | The notice (4.7). |
| Web | `src/CalCrony.Web/Pages/Docs.razor`, `Privacy.razor` | 4.8. |
| Docs | `README.md`, `PRIVACY_POLICY.md`, `CONTEXT.md`, `docs/release-notes/0.38.0.md` (new), `aidlc-docs/construction/build-and-test/live-e2e-script.md` | 4.8, 4.9. |
| Tests | `tests/CalCrony.Api.Tests/SeriesProjectionTests.cs` (new), `DbCommandRecorder.cs` (new), `CalendarApiFixture.cs`, `FreeSlotsApiTests.cs` | Section 7. |
| Tests | `tests/CalCrony.Bot.Tests/FreeSlotsEmbedBuilderTests.cs`, `AvailableHoursTextTests.cs` | Section 7. |
| Tests | `tests/CalCrony.Web.Tests/AvailableHoursComponentTests.cs`, `DocsPageTests.cs` | Section 7. |

Not changed: `CalendarAvailabilityService`, `GoogleCalendarProvider`, the route and its checks
(`CalendarEndpoints.cs`), `FreeSlotTarget`, the command's options, `NaturalDateTimeParser`,
`PublicCalendarBuilder`, `FeedEndpoints`, `SeriesMaterializer`, `RecurrenceCalculator`, and the
database.

## 6. Verified code facts (`master` 4af1305, 2026-11-16)

- `FreeSlotsService.GetAsync` (`FreeSlotsService.cs:47`): profiles read `:54`–`:63`; the by-name
  refusal `:69`–`:73`; the window `:100`; the calendars asked `:101`; the counted members and
  their busy blocks `:103`–`:121`; the answer `:129`–`:139`.
- `MemberAvailability.Busy` (`FreeSlotCalculator.cs:19`) takes intervals in any order, which may
  overlap, touch or reach outside the window; `Normalize` (`:191`) clips, sorts and merges them.
- `FreeSlotsResponse` (`Calendar.cs:244`) has two defaulted fields already, `ByName` and
  `LookedUntilUtc`. `FreeSlotLimits` (`Calendar.cs:64`): 50 members, 14 days, 366 days ahead.
- `Event` (`Entities.cs:130`): `CreatorId`, `StartsAt`, `DurationMinutes`, `Status`, `SeriesId`.
  `EventStatus` (`Events.cs:6`): Scheduled 0, Started 1, Ended 2, Cancelled 3.
- `EventSeries` (`Entities.cs:192`): `DaysOfWeek`, `UntilDate`, `MaxOccurrences`,
  `CurrentOccurrenceDate` (the slot date of the last occurrence created), `OccurrenceCount`,
  `Ended`, `DurationMinutes`, `TimeZone`, `CreatorId`.
- `RsvpOption.IsAttending` (`Entities.cs:325`), `Rsvp.Waitlisted` (`:353`);
  `RsvpPolicy.AttendingOption` (`RsvpPolicy.cs:30`) returns the flagged option, else the lowest
  sort order.
- `RecurrenceCalculator.NextOccurrence` (`RecurrenceCalculator.cs:96`): the first slot whose
  date is after the cursor and whose instant is after the given instant; the until date is
  inclusive; a cursor that lags is moved up first, so one call does not walk missed slots.
- `SeriesMaterializer.MaterializeNext` (`SeriesMaterializer.cs:21`): nothing for an ended series
  or a used-up count (`:28`); the next occurrence from `CurrentOccurrenceDate`, with the day set
  (`:35`–`:37`); it takes the series' creator (`:48`) and length (`:53`), and the count goes up
  by one (`:79`).
- `DeliveryScheduler`: an event starts once its start has passed (`:74`–`:79`) and ends once
  its length, 60 minutes when none is set (`DefaultEventLengthMinutes`, `:20`), has elapsed
  (`:105`–`:108`); the next occurrence of a running series is created when it has no live one
  (`:130` onwards).
- `FeedEndpoints.cs:150`–`:178`: the feed takes the next occurrence from the end of the live
  one, with the day set, and skips a series whose count is used up (`:168`).
- `PublicCalendarBuilder.ProjectOccurrences` (`:120`–`:143`) steps from the present, without the
  day set (`:128`–`:130`), yields starts, and stops after 1,000 steps (`:16`).
- Indexes (`CalCronyDbContext.cs`): events by server and start (`:95`); a series' live
  occurrence (`:107`); series by server (`:126`); RSVPs by event, member and option (`:166`);
  options by event (the model snapshot, `:881`).
- `FieldLimits.MaxMinutes` is 40320 (`FieldLimits.cs:30`); `Validation.BadDuration`
  (`Validation.cs:23`) checks a length against it, since `fc2a17f` (2026-07-19).
- The bot: `BasisSentence` (`FreeSlotsEmbedBuilder.cs:142`–`:179`), `DefaultHoursSentence`
  (`:185`–`:202`); `SavedNotice` (`AvailableHoursText.cs:49`–`:52`), `ViewLine` (`:271`); the
  `set` description (`SettingsModule.cs:268`, 90 characters).
- The web: the card's notice (`AvailableHoursCard.razor:74`–`:78`); the Docs entries
  (`Docs.razor:259`–`:278` and `:279`–`:292`); the privacy page's date (`Privacy.razor:8`) and
  passages (`:37`–`:45`).
- Docs: `PRIVACY_POLICY.md:3` (date), `:28`–`:36`; `README.md:42` (the Features section counts
  400 words) and `:90`; `CONTEXT.md:36`, `:40`; section 12 of the live script (`:204`).
- Tests: `FreeSlotsApiTests` runs on `CalendarApiFixture` with the real clock, asks for a day
  three days ahead (`:651`) and seeds rows directly (`:661`–`:707`). The scheduler and retention
  are off in tests (`ApiFixture.cs:33`, `Program.cs:117`), so seeded events and series stay as
  seeded. Pinned texts: `FreeSlotsEmbedBuilderTests.cs:57`–`:177`,
  `AvailableHoursTextTests.cs:327`, `AvailableHoursComponentTests.cs:274`,
  `DocsPageTests.cs:81` and `:106`.
- EF Core is 10.0.5 here, which has `ConfigureDbContext<TContext>` on the service collection.
- The release summary's rules: `docs/release-notes/README.md` and `CONTRIBUTING.md`, "Release
  summary"; `build-test` checks every file in that folder.

## 7. Execution plan

Tests are written with each step. A step is ticked in the same turn its work is done.

**0. Before any code**

- [x] 0.1 `master` taken into the branch: `audit.md` keeps both sides in the order of their time
      stamps and the state file keeps both sides. If the public calendar's series fix has
      merged, D17 and 4.4 are read again against what it leaves.
- [x] 0.2 `dotnet build CalCrony.slnx -c Release` and `dotnet test CalCrony.slnx` on the branch:
      green, with the counts and the warning count recorded in section 9.
- [x] 0.3 The design document amended with D12–D19 as approved.

**1. Contracts**

- [x] 1.1 `Calendar.cs`: `EventsCounted` on `FreeSlotsResponse`.

**2. API**

- [x] 2.1 `SeriesProjection` (D17, 4.4). `SeriesProjectionTests`, with the clock as an argument:
      a weekly series with no live occurrence; a day set of two weekdays gives both; nothing
      after the until date; with a count, only what is left, and the ones before the window use
      it up; a used-up count gives none; the first one waits for the live occurrence's end, so a
      live occurrence moved past later slots hides them; an occurrence that outlasts the gap to
      the next slot; one that starts before the window and reaches into it; the series' length
      and the 60-minute default; the wall-clock start kept across a daylight-saving change; a
      daily series looked up a year ahead stays inside the step bound.
- [x] 2.2 `DefaultEventLengthMinutes` made internal; `EventBusyTimes` (D13–D16, 4.2, 4.3),
      registered in `Program.cs`.
- [x] 2.3 `FreeSlotsService`: the order of 4.1, the intervals joined, `EventsCounted`; the
      comments that say what busy time is.
- [x] 2.4 `DbCommandRecorder` and `CalendarApiFixture` (R27).
- [x] 2.5 `FreeSlotsApiTests` gains, each on the route:
      a creator's event cuts their slot, with no RSVP of their own and with one on another
      option; a seat on the attending option cuts; a waitlisted RSVP and an RSVP on another
      option do not; a cancelled and an ended event do not, and a deleted one gives the time
      back; another server's event does not; no length is 60 minutes; several RSVPs of one
      member count once; an event that began before the first day and runs into it; an event on
      the day after the last one cuts a slot that runs to it; a member on hours alone is cut,
      stays "hours only" and no calendar is asked; a member with neither hours nor a calendar
      stays not counted, and no event is read when nobody is counted; the by-name refusal is the
      same text and reads no event, RSVP or series; a role lookup whose members are blocked by
      different events; a series' later date cuts its creator's slots and not an attendee's;
      the answer says events were counted; the event reads number at most five, and the same
      for one member over one day as for several over fourteen.

**3. Bot**

- [x] 3.1 `FreeSlotsEmbedBuilder` (4.6). `FreeSlotsEmbedBuilderTests`: each sentence of the
      second column; the first column stays pinned by the tests that exist, now as the case of
      an answer that does not say; the worst-case tests run with the longer sentences.
- [x] 3.2 `AvailableHoursText.SavedNotice` (4.7) and the two tests that quote it; the comment on
      `SlotsAsync`.

**4. Web**

- [x] 4.1 `AvailableHoursCard.razor` and its test.
- [x] 4.2 `Docs.razor` (4.8). `DocsPageTests`: the new sentences of both entries, and that only
      `slots` is said to count CalCrony events.
- [x] 4.3 `Privacy.razor` and `PRIVACY_POLICY.md` (4.8), read side by side afterwards; both
      dates.

**5. Docs**

- [x] 5.1 `README.md`: the bullet and the row; the Features word count re-run, at most 400.
- [x] 5.2 `CONTEXT.md`: the two terms.
- [x] 5.3 `live-e2e-script.md` §12: the new block.
- [x] 5.4 `docs/release-notes/0.38.0.md`; `dotnet run --project tools/ReleaseNotes -- check` and
      `-- preview --version 0.38.0`. If a release PR for a 0.37.x patch is open with a summary
      file of its own and this change is to merge before that release, that file is renamed to
      `0.38.0.md` and added to instead, before this change merges, and cut to the 1,800
      characters a summary may have, with Joshua's approval of the wording (section 9).

**6. Whole change**

- [x] 6.1 `dotnet build CalCrony.slnx -c Release` with no warning from the new code;
      `dotnet test CalCrony.slnx` green; the counts recorded in section 9.
- [x] 6.2 Mutation checks, each rule taken out in turn and a test meant to pin it seen to fail:
      the creator; the seat; the waitlist; the other option; the status filter; the server
      filter; the 60-minute default; the four weeks back; counted members only; the order of
      the refusal; the day set; the until date; the count; the wait for the live occurrence's
      end; the sentences' switch on `EventsCounted`.
- [x] 6.3 The whole diff read against section 4, rule by rule, and against FR20–FR31; new files
      scanned for invisible characters.
- [x] 6.4 Two review agents, one on security and one on code quality, on each local commit until
      neither has a finding; section 9 records each round and names the commit each agent read.
- [x] 6.5 The state file and the audit trail brought up to date; the completion gate put to
      Joshua.

## 8. Delivery

- **Commits**: local, layer by layer, on `feature/free-slots-calcrony-events`. Nothing is pushed
  before the completion gate.
- **At the completion gate**, on Joshua's go-ahead: the tracking issue is filed, the branch is
  pushed and the pull request is opened at a commit both review agents have passed, with
  `Closes` naming the issue. Its title is `feat: free slots count this server's CalCrony
  events`.
- **Checks**: `build-test` (the suite, and the release summary's rules), `pr-title`, CodeQL.
- **Merge**: a squash merge at the reviewed commit, on his go-ahead.
- **After the merge**: the test stack on `:main`, app services only; the live items of 4.8, run
  from his account; the build-and-test summary updated; his approval before the release.
- **Release 0.38.0**: the release PR's "Release summary" comment is read first; merging it is
  the release. The `release-summary` job's run is read afterwards and never re-run.
- **Prod**: backup first, then the pinned tag, app services only. There is no migration, so
  rolling back is the tag set back and the same restart. For the seconds between the API's
  restart and the bot's, an older bot shows today's sentences over a list that already leaves
  events out.
- **Each of these steps waits for a go-ahead of its own.**

## 9. Notes from generation

### Before any code (2026-11-17)

- **0.1** `master` was at `ab32e96` (pull requests #179 and #181) and is merged into the branch
  as `4bdebae`. Only the audit trail and the state file conflicted. `master`'s own entries sit
  between earlier ones, so the audit trail was merged as a set: the part all three versions
  share, then every other entry once, in the order of their time stamps — 43 entries after the
  shared part, 18 of them this branch's and 17 `master`'s. The state file keeps both sections.
  The public calendar's series fix was not on `master`, so D17 stands as written.
- **0.2** On `4bdebae`: the Release build has no error and 22 warnings, the number the 0.37.0
  records give for the baseline. 1,636 tests pass and none fails — 721 API, 418 bot, 153 web,
  344 for the release-summary tool — and 17 of the tool's tests are skipped on Windows, as they
  are for every run here.
- **0.3** The design document carries D12–D19 in its section 2 and a new section 11.

### Generated (2026-11-17)

Layer by layer, in local commits: the API (`0d0c9cb`), the bot (`e09e48d`), the web
(`c481de2`) and the documents (`00153ba`). `master` had moved while they were written — pull
requests #180 (`sat` and `sun` in date text) and #183 (a linter for the workflow files) — and
was taken in a second time, as `f537ed1`, the same way as in step 0.1: 48 entries after the part
the three versions of the audit trail share, and the state file with the sections of both
sides.

Where the code differs from the plan as approved:

- **`RsvpPolicy.cs` is changed**, which section 5 did not list. D13 has the lookup select only
  an option's id, flag and sort order, and D16 has it use `RsvpPolicy.AttendingOption`, which
  takes whole options. The rule is now a method that takes any row carrying a flag and a sort
  order (`AttendingOf`), and `AttendingOption` calls it, so there is still one rule and its
  result for whole options is what it was.
- **`ComponentTests.cs` in the web tests is changed**, which section 5 did not list: it quotes
  the privacy page's passage on available hours, and now quotes the new one and the new
  sentence under "Content you create".
- **The Docs entry says one thing more than 4.8**: members with neither saved hours nor a
  linked calendar "aren't counted, whatever events they have" (FR24). The sentence that
  followed the old one began "It is listed under"; after the new sentences "it" would have been
  an event, so it reads "A slot is listed under".
- **Tests beyond the list in section 7**: a custom option set whose attending option is not the
  first by sort order; a series in another server; a series' occurrence moved past the next
  slot, on the route as well as in the pure tests; and, for the projection, a zone that is not
  known, an occurrence that starts as the window ends, and a live occurrence that is already
  over.
- **5.4, the summary's file**: `master` now carries `docs/release-notes/0.37.1.md`, written
  with pull request #180, and release pull request #185 for 0.37.1 is open. This change's
  summary is `0.38.0.md`, a file of its own, and `0.37.1.md` is left as it is. Which is right
  depends on the order of two things that are both Joshua's to decide: if 0.37.1 is released
  first, the two files are as they should be; if this change is merged first, the next release
  becomes 0.38.0 and `0.37.1.md` has to be folded into `0.38.0.md` in the same pull request.
  That is checked again when the pull request is opened and before it is merged (section 8).

On `f537ed1`: the Release build has no error and the same 22 warnings, none of them in a file
this change adds or edits. 1,781 tests pass and none fails — 855 API, 427 bot, 153 web, 346 for
the release-summary tool, 17 of the tool's skipped on Windows. Two of the tests named above came
after that run, and a third was renamed and given a series as well, which brings the API's to
857; the whole suite is run again on the commit the review agents read.

**`master` taken in a third time**, as `1a8380b`, before the review: pull requests #182, the
public calendar's fix for weekly day sets, and #186, records. By step 0.1, D17 and 4.4 were read
again against what that fix leaves. Its projection now passes the day set; it still steps from
the present, not from the end of the live occurrence, and it still yields starts without ends.
So D17 stands as approved: free slots need the ends, and the wait for the live occurrence and
for each occurrence before the next. The two projections now agree on which days a series
has. They can still differ where an occurrence was moved past later slots: the calendar shows
such a slot, and free slots leave it free, which is what the scheduler will do. One projection
for both, and for the feed, stays a candidate for a follow-up.

On `013c0b7` the Release build had no error and its 22 warnings, and 1,797 tests passed — 871
API, 427 bot, 153 web, 346 for the release-summary tool. **`master` taken in a fourth time**, as
`4ada04a`, after pull request #184 (date text that is missing or cannot be read) had merged; its
change to `RsvpPolicy.cs` merged with this one's without a conflict. On `104c587`, the head the
review read: no error, the 22 warnings, 1,835 tests passing — 909 API with #184's own, 427 bot,
153 web, 346 for the tool.

### Mutation checks (2026-11-17)

Each rule was taken out of the code in turn, the tests meant to pin it were run, and the file
was put back. Every one of the 28 changes built and failed at least one test; afterwards the
sources were as committed.

| Rule taken out | Test that failed |
|---|---|
| The creator is busy | `An_event_cuts_its_creators_slots_whatever_rsvp_they_gave_themself` (3 of 4 rows) |
| A seat on the attending option counts | `Only_a_seat_on_the_attending_option_ties_a_member_to_an_event_they_did_not_create` |
| A waitlisted RSVP holds no seat | the same test, its waitlisted row |
| An RSVP on another option does not count | the same test, its two other-option rows |
| The marked option is the attending one, whatever its sort order | `With_a_custom_option_set_only_the_option_marked_as_attending_counts` |
| Only scheduled and started events | `Only_a_scheduled_or_started_event_counts` (its ended and cancelled rows) |
| Only this server's events | `Events_and_series_count_only_in_the_server_they_belong_to` |
| Only this server's series | the same test |
| No length is 60 minutes, for an event | `An_event_with_no_length_set_counts_sixty_minutes` |
| Four weeks back | `An_event_of_the_longest_length_that_began_four_weeks_earlier_still_counts` |
| A stopped series has no later dates | `A_series_later_date_cuts_its_creators_slots_and_not_an_attendees` |
| The live occurrence's end reaches the projection | `A_series_occurrence_moved_past_the_next_slot_leaves_that_slot_free` |
| Events are read for counted members only | `Events_never_make_a_member_counted_and_with_nobody_counted_none_is_read` |
| The by-name refusal comes before any event is read | `The_by_name_refusal_is_the_same_text_and_reads_no_event_rsvp_or_series` |
| Event intervals join the busy time | `A_role_lookup_cuts_each_member_by_their_own_events` |
| The answer says events were counted | `The_answer_says_that_events_were_counted_even_when_there_are_none` |
| The day set | `A_series_with_a_day_set_gives_every_day_of_the_set` |
| The until date | `Nothing_falls_after_the_until_date_and_the_date_itself_still_counts` |
| The count that is left | `A_count_that_is_used_up_gives_none` (both rows) |
| Each occurrence uses up the count | `The_occurrences_before_the_window_use_up_the_count_as_well` |
| The first one waits for the live occurrence's end | `A_live_occurrence_moved_past_later_slots_hides_the_slots_it_passed` |
| Each later one waits for the one before | `An_occurrence_that_outlasts_the_gap_to_the_next_slot_takes_that_slot_with_it` |
| No length is 60 minutes, for a series | `A_series_with_no_length_set_counts_sixty_minutes` |
| One that starts as the window ends is not given | `One_that_starts_as_the_window_ends_is_not_given` |
| One that reaches into the window is given | `One_that_starts_before_the_window_and_reaches_into_it_is_given_and_one_that_ends_as_it_starts_is_not` |
| The sentences name events only when the answer says so — always naming them | six tests, among them `An_answer_that_does_not_say_events_were_counted_never_names_them` |
| The same — never naming them | five of the `With_events_counted_…` tests |
| The same, in the default-hours sentences | four tests, among them `One_member_counted_reads_as_their_own_free_time` |

Not checked this way, because the answer cannot show it: that only the counted members' RSVPs
are read, and that an event which has ended before the window is dropped before its seats are
read. Both are for the review to read in `EventBusyTimes.cs`.

### Review round 1 — two review agents on `104c587` (2026-11-17)

The first two agents, started on `013c0b7`, stopped on a usage limit before they reported
anything. Two new ones, on another model, read `104c587`. Security and privacy: no critical,
high or medium finding; two low, four for information. Code quality: no major finding; seven
minor, eight nits. All are answered in the round's commit, as follows.

Security and privacy:

- **S1 (low).** A series that cannot reach the days looked at is now passed over before its
  rule is stepped, and the live occurrence reaches the projection as a start and a length
  (R29). Round 2 put the check in a function of its own, `CanReachWindow`, pinned by tests with
  dates next to the window.
- **S2 (low).** Members who saved hours before this change agreed to a notice that did not
  mention events. The 0.38.0 summary, which goes to the community Discord, now tells them, and
  how to stop lists by name (R30).
- **S3 (information).** A role lookup now leaves out each counted member's events, also for a
  member who saved no hours, so D11's reason for letting a role through — it reaches what the
  grid reaches — holds in kind but no longer in reach. Recorded among the requirements' risks
  and in the design document's section 11; no code change.
- **S4 (information), also Q4.** "Events you created or hold a seat in" said more than the rule,
  since a seat on any other option does not count. Both privacy copies, the README row and the
  contract's comment now say "hold a seat in on their attending option" (R31). This changes the
  wording of 4.8 as approved, which Joshua sees at the completion gate.
- **S5 (information).** The first read selects the creator of every live event in range, not
  only of the counted members' events: D13 as approved, and nothing of it leaves the method.
  Left as it is.
- **S6 (information).** For an event that breaks the rule of one marked option, which option
  counts depends on the order rows come back in, as everywhere else that rule is used. Left as
  it is.
- **Could not determine, now settled.** Each list of ids goes to the database as one array
  parameter (`= ANY (@ids)`), checked in the commands a lookup sends. Rows with a length or an
  interval outside today's checks were not counted on prod; today's routes cannot store either.

Code quality:

- **Q1 (minor).** The five reads were separate statements, so a series the scheduler moved on
  between two of them could be missed for that one lookup. They now share one snapshot of the
  database, a repeatable-read transaction (R32); a test makes the scheduler's step land before
  each read in turn.
- **Q2 (minor).** A test would have failed on the Thursday before each clock change in Chicago.
  Its event is now counted back from the noon it ends at.
- **Q3 (minor).** No test cut a member with a linked calendar by an event; one now does, with a
  calendar block beside it.
- **Q4 (minor).** As S4.
- **Q5 (minor).** Records out of date at that commit: the state file's "No code is written", the
  fourth merge and its counts, and "three tests" where two were added and one extended.
  Corrected here and in the state file.
- **Q6 (minor).** The design document's row for members counted on hours alone (4.5), D16 and
  section 11's table did not match the code; corrected, and section 11 also records S3.
- **Q7 (nit).** The projection follows the scheduler as far as it can be foreseen; the scheduler
  acts on an end at its next sweep, which can matter only for an occurrence that ends less than a
  sweep before a slot. The comments say so instead of "what the scheduler will create".
- **Q8 (nit).** The five-reads test had one seated event and one series; it now has two of each.
- **Q9 (nit).** The schedule was built from twelve positional arguments inside the query, where
  two of them could swap unseen; the series is now read as plain columns and the schedule built
  with named arguments, and a test checks that every field reaches the projection as stored.
- **Q10 (nit).** A comment said "no earlier than" where the code says "later than"; corrected.
  D15 keeps its words: the two differ only for an event that ends as the window starts, which is
  not in the window either way.
- **Q11 (nit).** The work is bounded per series. Not changed; one projection shared by the feed,
  the public calendar and free slots is a candidate for a follow-up.
- **Q12 (nit).** A test that passed whatever the live occurrence's end was now checks that the
  live occurrence is not given again; a check of a constant is gone; the slots before a delete are
  asserted exactly; the list of table names sits with the class's other fields.
- **Q13 (nit).** The fallback to the first option by sort order, for a set with no option marked,
  is now tested through the lookup.
- **Q14 (nit).** The summary now says that whoever created an event stays blocked whatever they
  answer. Its file name stands: 0.37.1 was released on 2026-11-17 at 23:27Z, so the next minor
  release is 0.38.0.
- **Q15 (nit).** A comment said the busy list is empty only for a member with neither a calendar
  nor an event; corrected.

Refinements made in this round:

| # | Refinement | Why |
|---|---|---|
| R29 | A series that cannot reach the window is passed over before its rule is stepped; the live occurrence reaches the projection as a start and a length. | S1 |
| R30 | The release summary tells members who saved hours that lists by name now leave out their events, and that the creator of an event stays blocked whatever they answer. | S2, Q14 |
| R31 | "Hold a seat in on their attending option" in both privacy copies, the README row and the contract's comment. | S4, Q4 |
| R32 | The lookup's reads of events, seats, options and series share one snapshot. | Q1 |

The mutation checks were run again over the changed code: the 28 of the first run, with the text
they change brought up to date, and ten for this round's rules. Each of the 38 built; 37 failed
a committed test and one was checked by hand, which round 2 replaced with a committed test. Two
of the new ones were not caught at first because their tests were not strict enough — the
schedule test gave two fields of one type the same value, and the fallback test relied on the
order rows come back in — so both tests were tightened until each check failed them.

**`master` taken in a fifth time**, as `1a62379`: release 0.37.1 (#185), the workflow linter's
records (#188) and a code owners file (#187). Only the audit trail conflicted, merged as before.
On `1a62379`: no error, the 22 warnings, 1,846 tests passing — 917 API, 427 bot, 153 web, 349 for
the release-summary tool.

### Review round 2 — the same two agents on `ed0daa9` and `1a62379` (2026-11-18)

Both read the answers of round 1 and the merge. Security and privacy: S1 is closed on every path
of the lookup; one low finding, on public text, and three for information. Code quality: every
round-1 finding answered; one minor finding and six nits. Answered in the round's commit:

- **Wording (low).** Round 1's notes are reworded above.
- **The snapshot test (minor; information from security).** Before the scheduler's step, that
  test's lookup made three reads, not five, so the step never landed before the seats or options
  read. A seat in the window now makes it read all five, and each round asserts that the step
  landed.
- **The snapshot stays open while series are stepped (nit; information from security).** The
  reads now return the running series, the transaction is committed, and the series are stepped
  after it. The method's comment says it opens a transaction of its own and is called outside
  one.
- **The first-date check (information from security).** The checks that pass a series over are
  now one function, `SeriesProjection.CanReachWindow`, tested with dates next to the window: a
  first date one day past the date the window ends on can still reach it and two days cannot, a
  last created date on that date can and the day after cannot, and a live occurrence that starts
  a minute before the window ends can and one that starts as it ends cannot. One test still shows
  that the check comes before the rule is read.
- **More than one live occurrence (nit).** The comment described a rule the scheduler does not
  follow; it now says what holds: the index on live occurrences allows one per series.
- **The 60-minute default twice (nit).** One internal helper, `DeliveryScheduler.EventLength`,
  beside the constant, used by both new files. The scheduler's own line is left as it was, to
  keep this change out of the scheduler.
- **"That server" (nit).** The summary's new bullet now says "the server the list is made in".
- **The recorder's hook (nit).** Its comment now says it is called for commands that read rows
  and are sent asynchronously, as every read of the API is.
- **Records (nit).** The fifth merge is recorded above and in the state file.

The mutation checks were run over the reshaped code, with the text they change brought up to
date: 41 rules. They are the 38 of round 1, with its one check for passing over too much split
into three — one for each check in `CanReachWindow` — and one more that moves the check after
the rule is read instead of before it. Each built and failed a committed test.

### Review rounds 3 and 4 (2026-11-18)

On `56a7473` the Release build had no error and its 22 warnings, and 1,849 tests passed — 920
API, 427 bot, 153 web, 349 for the release-summary tool. Round 3, both agents on `56a7473`: no
finding in the code from either; two wording nits from code quality (how this section counted
the mutation checks, and "the window's last day" where the code uses the date the window ends
on) and one low finding on wording from security. All answered in `0139ee8`, which changes
records and comments only; a from-scratch Release build there has the same 22 warnings, none in
this change's files. Round 4, both agents on `0139ee8`: no finding from either.

The commits the agents read, in order: `104c587`, `ed0daa9` with the merge `1a62379`,
`56a7473`, and `0139ee8`.

What differs from the plan as approved, for the completion gate: the refinements R29–R32 of
round 1, among them the wording of 4.8, which now says "hold a seat in on their attending
option" (R31); `RsvpPolicy.cs` and `ComponentTests.cs` changed although section 5 did not list
them; `DeliveryScheduler.EventLength` added beside the constant; more tests than section 7
listed; and the release summary of 4.9, which gained a bullet for members who saved hours and
says that whoever created an event stays blocked whatever they answer (R30).

### Before the first push (2026-11-18)

The completion gate was answered "Continue", and the tracking issue was filed as #192. Asked how
the branch's local history goes out, Joshua answered "Rebuild first": the branch was rebuilt as
one commit on `master`, with `master` taken in twice more on the way (#191 and #194, records
only; the audit trail merged by its stamps as before). The commits this section names before
the first push stay on the local branch `feature/free-slots-calcrony-events-history`, which is
never pushed.

Round 5, both agents on the first rebuild, `56e381f`: its code, tests and docs byte-identical to
`0139ee8`, and the merge exact. Security and privacy: no finding above information. Code
quality: two minor findings — the rebuild was already behind `master` (#194), and the state
file and this plan still described the time before the gate's answer — and five nits on the
pull request's text. All answered before the push: `master` taken in again, the state file and
this plan brought up to date, and the pull request's text made exact and given the template's
sections. Both agents read the difference before the push.

### After the push (2026-11-18)

Pushed as `8fc7105` and opened as pull request #197; every check passed on it. Another open pull
request changes the same lines of `DeliveryScheduler.cs`. Asked which of the two merges first,
Joshua answered "Go ahead" to the recommendation: that one first; this branch then takes in
`master`, resolves the overlap and goes out again after both agents' read, and its merge is asked
after that.

`master` then moved to `5fc9239` (#198, #193 and #195), and the app's Auto-fix reported a
conflict. `master` is merged in as `3fd509f`, and only the audit trail and the state file
conflicted. The audit trail is merged as a set in the order of its stamps, and taking either
side's entries out gives the other side's file exactly; the state file keeps `master`'s lines and
sections, with this change's section after them. `Program.cs`, `RsvpPolicy.cs` and the README
merged by themselves, and the lines this change adds and removes under `src`, `tests` and the
documents are the same against `5fc9239` as against `47209ab`. On the merged tree the Release
build has no error and 22 warnings, as before, and 2,102 tests pass with none failing (1,173
API, 427 bot, 153 web, 349 for the release-summary tool; 17 of the tool's tests skipped on
Windows).

Round 6, both agents on the four commits not yet pushed (`b33cda9`, `ef91352`, `3fd509f` and its
records `ee43871`): security and privacy found nothing; code quality found the merge exact, with
one minor finding and six nits, all on wording. "The other open pull request that changes
`DeliveryScheduler.cs`" also fits a second open pull request that changes that file, so the state
line says "the same lines"; which commits stay on the history branch is narrowed to those named
before the first push; the paragraph above is re-wrapped and its counts worded as in the earlier
notes; and "once since the push" replaces "once already". The messages of `b33cda9` and `3fd509f`
stay as they are, since changing them would change the ids the records name. The records commit
was amended into `a3b30a5`, both agents confirmed the difference, and the branch was pushed by
its name at 19:10:55Z.

### `master` taken in after the other pull request merged (2026-11-18)

The other pull request that changes the same lines of `DeliveryScheduler.cs` merged at
19:21:34Z, and the app's Auto-fix reported the conflict. `master` at `3e35071` is merged in as
`b33dd0a`. Three files conflicted: the audit trail (merged as a set in the order of its stamps;
taking either side's entries out gives the other side's file exactly), the state file (both
sections kept, `master`'s first) and `DeliveryScheduler.cs`, where `master` moved the 60-minute
default into `EventTimes`, the API's one place for an event's end. That file is taken as
`master` has it: the default this change had made internal, and the `EventLength` helper beside
it, are gone. In the same merge:

- `EventBusyTimes` and `SeriesProjection` take every end from `EventTimes.End`, as the scheduler
  and the feed do on `master`.
- `master`'s release summary for 0.37.2 moves into `0.38.0.md` under `CONTRIBUTING.md`'s rule
  that a later `feat:` renames the file, since the release pull request for 0.37.2 is open: its
  bullet follows this change's two, worded as it was. Should 0.37.2 be released before this
  change merges, `0.37.2.md` goes back as it was and `0.38.0.md` loses that bullet.
- The design document's component table (section 11) names `EventTimes` in place of the
  constant made internal.

`a8ad00e` then has `EventBusyTimes` step each series on its own, as the scheduler advances each
on its own: a series whose rule cannot be stepped is logged with its id and the server's, and
its later occurrences are left out, rather than failing every lookup that counts its creator.
The guard is in `EventBusyTimes`, not in the pure projection, so the two tests that show a series
passed over before its rule is read still fail if it is read. One test pins the guard (a series
with an interval of zero beside an event of the same member and another member's series); with
the guard taken out it fails.

D17 read again against `master`'s `FeedEndpoints.cs`: it stands. The feed takes its ends from
`EventTimes` too, and the projection still steps from the cursor with the day set.

On the tree of `a8ad00e` the Release build has no error and 22 warnings, as before; 2,167
tests pass with none failing (1,238 API, 427 bot, 153 web, 349 for the release-summary tool; 17
of the tool's tests skipped on Windows); and the release-summary tool's check finds no problem.

While both agents read the commits above, `master` moved to `6b85491` (#202, records only) and is
merged in as `ee820c4`, so that the branch goes out once: only the audit trail conflicted, merged
by its stamps as before. Nothing outside `aidlc-docs` differs from `406dd24`, so the results above
stand.

Round 7, both agents on `302262d`, `b33dd0a`, `a8ad00e` and `406dd24`, then on `ee820c4` and
`ce80856`: security and privacy found nothing; code quality found both merges exact and the guard
and its test sound, with one minor finding on the pull request's text, which did not say that
`0.37.2.md` moved, and five nits. Meanwhile `master` moved to `6cb41c8` (#199), which adds to
`0.37.2.md`, and is merged in as `f31d3ff`. Three files conflicted: the audit trail and the
state file, merged as before, and `0.37.2.md`, deleted here and changed on `master`, kept as
`master` has it; the live script merged by itself with both sides' items. `ca4b2cc` takes the
moved line back out of `0.38.0.md`. The release pull request for 0.37.2 is open, so its summary
stays in its own file until the order of that release and this change's merge is settled; should
this change go first, the fold of step 5.4 is made before it merges. That answers the minor
finding too. `6ce2b7f` says in `SeriesProjection.LaterOccurrences` that a rule that cannot be
stepped throws, and narrows the design document's row to the ends of events and later
occurrences; in `5935891`, "those commits" became "the commits above", and the state line names
`ee820c4`.
`ee43871`, named above, is kept on the local history branch. On the tree of `6ce2b7f` the
Release build has no error and 22 warnings; 2,472 tests pass with none failing (1,542 API, 427
bot, 153 web, 350 for the release-summary tool; 17 of the tool's tests skipped on Windows), and
the release-summary tool's check finds no problem.

### `master` taken in with the public calendar's projection (2026-11-18)

Round 8, two new agents on `f31d3ff`, `ca4b2cc`, `6ce2b7f` and `5935891`: security and privacy
found nothing; code quality found the four commits accurate and the merge exact, with two major
findings, one minor and four nits. First, `master` had moved again, to `1fce915` (#196), which
adds its own `SeriesProjection.cs` and tests for the public calendar. Second, should this change
go first, folding `0.37.2.md` into `0.38.0.md` gives a file longer than the 1,800 characters a
release summary may have, so the fold would also cut its text, with Joshua's approval of the
wording. The minor finding: step 5.4 read as done for a fold only planned, and now says when it
applies. The nits: the conflict list above left out `0.37.2.md`, the paragraph above had
answered three of round 7's five nits, the state line's condition read loosely, and the
projection said it leaves a series out where only its later occurrences are left out; each is
answered here, in the state line or in the merge below.

`master` at `1fce915` is merged in as `05476a2`. Both sides had added `SeriesProjection.cs` and
`SeriesProjectionTests.cs`; the two `SeriesProjection` classes are now one, with the public
calendar's `EndOf`, `FreeFrom` and `Upcoming` as `master` has them beside this change's
`LaterOccurrences` and `CanReachWindow`, and one `LiveOccurrence`, `master`'s, which
`EventBusyTimes` now builds with the series' id, and the tests of both sides are one test class.
The audit trail and the state file conflicted as well and are merged as before. The design
document's rows for `SeriesProjection` and `EventTimes` say what they are now (`7a55431`). On the
tree of `05476a2` the Release build has no error and 22 warnings, 2,495 tests pass with none
failing (1,565 API, 427 bot, 153 web, 350 for the release-summary tool; 17 of the tool's tests
skipped on Windows), and the release-summary tool's check finds no problem.

D17 and 4.4 read again against #196: the public calendar now starts a series' next dates once its
live occurrence has ended and leaves out the dates that occurrence passes, as free slots do; its
`Upcoming` still gives starts without ends, so D17 stands, and one stepping loop for both stays
the follow-up D17 names. The public calendar's `EndOf` and `DefaultLengthMinutes` are kept as
`master` has them; the ends free slots counts still come from `EventTimes.End`.

Round 9, two new agents on `05476a2` and `7a55431`: security and privacy found nothing above two
nits; code quality found the merge's code exact, with two minor findings and seven nits, all on
comments and records. The design document's row said that `Upcoming` takes the one
`LiveOccurrence`, which it does not (both agents); the plan did not read D17 and 4.4 again
against the public calendar's change; the audit entry of 20:11:31Z counts the design rows among
what `05476a2` did, where they changed in `7a55431` (security); and the nits asked for a comment
on the nullable values in `EventBusyTimes`, exact wording for the public calendar's entry point,
two sentences in the right tense, one subject made clear, the summary limit in step 5.4,
`5935891` named for two answers, and the pull request's text checked now that this change edits
`SeriesProjection.cs` rather than adding it. All but the last are answered in `5e5af40`, comments
and records only: the paragraph above reads D17 and 4.4 again, and the entry of 20:11:31Z stays
as written, corrected here and in the audit trail. The pull request's text was checked and still
holds, as it says only that a pure `SeriesProjection` steps a series' rule. No code line changed,
so the results of `05476a2` stand; a full rebuild has no error and 22 warnings. One projection for
the feed, the public calendar and free slots, with one way to work out an end, stays the
follow-up D17 names.

Both agents confirmed `5e5af40` and `96db72a`, with a few words left: the comment on the nullable
values now says "a default one", and this paragraph names every nit and the source of each
point. The branch was pushed by its name at 20:36:53Z (`a3b30a5..96db72a`).

Asked which goes first, the release of 0.37.2 or this change's merge, Joshua answered "A" the
same day: 0.37.2 first. So `0.37.2.md` stays where it is and step 5.4's fold does not apply; this
change goes out as 0.38.0 with `0.38.0.md` as it stands, and its merge is asked once 0.37.2 is
released and rolled out.

`master` then moved to `c97c15e` (#201, records only) and is merged in as `e0b2337`: only the
audit trail conflicted, merged by its stamps as before. Outside `aidlc-docs` the branch differs
from `96db72a` only by the one comment, so the results of `05476a2` stand. Both agents read the
four commits after `96db72a` and confirmed them, with wording left for this paragraph and the
state line, and the branch was pushed by its name at 20:56:14Z (`96db72a..ebaa7c6`).

## 10. Plan checklist

- [x] Requirements, workflow plan, design document and the two earlier plans loaded
- [x] Design decisions D12–D19, each with the alternative it rejects
- [x] Rules written; the reply's sentences and the texts drafted
- [x] Code facts verified against `master` 4af1305
- [x] Steps with checkboxes; delivery
- [x] Approved by Joshua — 2026-11-17, as written ("Continue")
- [x] Generated; completion gate — answered 2026-11-18 ("Continue"); tracking issue #192
