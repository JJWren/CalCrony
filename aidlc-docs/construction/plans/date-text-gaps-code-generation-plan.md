# Older gaps in date text (`by friday`, `every saturday`, a weekday on its own day, a time apart from its day, `easter sat`) — functional note and code-generation plan

**Task**: five older gaps in how CalCrony reads a date typed in words, listed at the end of
section 8 of the plan for `sat` and `sun` (`short-weekend-days-code-generation-plan.md`, pull
request #180). Find out by running the real parser what each does today; recommend for each
whether to fix it, give a better refusal or leave it; and put the decision to Joshua before
anything is built. Handed over on 2026-11-16 from the session that wrote that fix.
**Baseline**: `master` 4af1305, whose code is v0.37.0, which the hosted instance runs. The parser
was run as it stands on pull request #180, open and not merged, because the five were found on
that code and `sat` and `sun` must go on reading exactly as their full names do.
**Branch**: `fix/date-text-gaps` → one pull request, a `fix:`, titled once the scope is decided.
**Status**: written 2026-11-16 and approved by Joshua the same day, as written, with Q1–Q5 each
answered A (section 4). By Q5, generation waits for pull request #180 to be on `master`.

A change inside one class, so the workflow runs at minimal depth, as on the fix for `sat` and
`sun`: the task is the requirement; no user stories, application design, units, NFR or
infrastructure stages; one unit, and this file is both its functional note (sections 1–4) and its
code-generation plan (sections 5–7), approved together.

## 1. What happens today

Every date typed in words goes through `NaturalDateTimeParser`: `TryResolve` for a date and time
(event start, repeat-until, RSVP close, poll close and time-poll options, reminders, `/timestamp`,
the web's date fields) and `TryResolveDay` for a day (`/availability slots when:`). Both hand the
text to `Microsoft.Recognizers.Text.DateTime` 1.8.13 and read what it returns.

Everything below was run against the real parser, from a scratch harness with a project reference
to `src/CalCrony.Api` that is not committed. Unless a row says otherwise the moment is Wednesday
2026-07-15 at 10:00 in America/Chicago, the parser tests' fixture.

### 1.1 `by`, `until`, `before`: a date that is a deadline

| Text | Date and time (`TryResolve`) | Day (`TryResolveDay`) |
|---|---|---|
| `by friday`, `until saturday`, `before friday`, `till friday`, `no later than friday` | refused: not understood | refused: not understood |
| `by friday 6pm`, `by sat 6pm`, `before saturday 6pm`, `until friday 5pm`, `by 6pm friday` | refused: not understood | refused: not understood |
| `by 6pm`, `before noon`, `until 6:30pm`, `by midnight` | refused: not understood | refused: not understood |
| `by tomorrow`, `by nov 12 6pm`, `by 7/20`, `by next friday`, `by 2026-07-20 18:00`, `by end of day` | refused: not understood | refused: not understood |
| `friday before 6pm`, `friday by 6pm`, `friday until 6pm` | refused: not understood | refused: not understood |
| `by the weekend`, `before next month`, `by end of month`, `until next week`, `until 2027` | refused: not understood | refused: not understood |
| `2 hours before friday 6pm`, `30 minutes before 6pm` | refused: not understood | refused: not understood |
| `friday, by 6pm` | Friday **9 AM**: the time is dropped | Friday |
| `after friday`, `after friday 6pm`, `since friday`, `from friday 6pm` | Friday 9 AM; Friday 6 PM; Friday 9 AM; Friday 6 PM | Friday |

The recognizer returns each of the refused texts as a range that has an end and no start, with a
length of time beside it in the row of `2 hours before friday 6pm`. Both readers take a range's
start, so nothing is read. `after friday 6pm` is the mirror image, a range with a start and no
end, and it is read, as its start. So an RSVP close typed as `by saturday 6pm` is refused, while
`saturday 6pm` and `after saturday 6pm` in the same option are read.

### 1.2 `every …`: a repeat

| Text | Date and time | Day |
|---|---|---|
| `every saturday 8pm`, `every sat 8pm`, `every friday`, `each friday`, `fridays at 8pm` | refused: not understood | refused: not understood |
| `every day at 6pm`, `daily`, `weekly`, `monthly`, `every 2 weeks`, `every other friday`, `every weekday` | refused: not understood | refused: not understood |
| `friday 8pm every week`, `friday 8pm weekly` | Friday 8 PM, once: the repeat is dropped | Friday |
| `weekly on friday` | Friday 9 AM, once | Friday |
| `every friday starting july 24` | July 24 at 9 AM, once | July 24 |

The recognizer marks a repeat as a kind of its own, with no date in it. Neither reader takes one,
and the refusal is the general one: `Could not understand "every saturday 8pm" as a date/time.`
Where something else in the text can be read, the repeat is dropped and nothing says so.

### 1.3 A weekday typed on its own day

The text is typed on the fixture's Wednesday, at four moments. A Saturday behaves the same on a
Saturday.

| Text | 00:30 | 08:59 | 10:00 | 23:30 | Day, at any hour |
|---|---|---|---|---|---|
| `wed`, `wednesday`, `on wednesday` | today 9 AM | today 9 AM | refused: past | refused: past | today |
| `wed 9am` | today 9 AM | today 9 AM | refused: past | refused: past | today |
| `wed 6pm`, `wednesday at 6pm` | today 6 PM | today 6 PM | today 6 PM | refused: past | today |
| `wednesday night` | today 8 PM | today 8 PM | today 8 PM | refused: past | today |
| `this wednesday`, `today` | today 9 AM | today 9 AM | refused: past | refused: past | today |
| `next wednesday` | Jul 22, 9 AM | Jul 22, 9 AM | Jul 22, 9 AM | Jul 22, 9 AM | Jul 22 |

For a weekday with no week named, the recognizer offers last week's and this week's. On the day
itself this week's is today, so once its time is behind — 9 AM when none is typed — both have
passed and the text is refused: `"wed" resolves to a time in the past.` Typed the day before,
the same text is tomorrow; typed the day after, it is next week's. The refusal holds from the
time named until midnight, on that one day.

A date behaves differently, and is not part of this point: `jul 15` typed on July 15 after 9 AM
reads as July 15 of next year, because for a bare date the recognizer offers this year's and next
year's; `july 15 6pm` typed that evening is refused as past, as `today 6pm` is.

### 1.4 A time written apart from its day

| Text | Date and time | Day |
|---|---|---|
| `friday 6pm`, `friday at 6pm`, `friday, 6pm`, `friday - 6pm`, `friday @ 6pm`, `friday around 6pm`, `6pm friday` | Friday 6 PM | Friday |
| `fri (6pm)`, `fri. 6pm`, `friday from 6pm`, `friday: 6pm`, `friday; 6pm`, `friday / 6pm`, `friday about 6pm`, `friday starting at 6pm`, `6pm (friday)` | **today** 6 PM | Friday |
| `fri (9am)`, 9 AM being behind | **tomorrow** 9 AM | Friday |
| `tomorrow (6pm)`, `nov 12 (6pm)`, `7/20 (6pm)`, `sat july 18 (6pm)`, `next week (6pm)`, `easter 6pm` | **today** 6 PM | the day named |
| `friday (evening)`, `friday at night` | **today** 4 PM; **today** 8 PM | Friday |
| `friday or saturday (6pm)` | **today** 6 PM | Friday |
| `friday 6pm, ends 9pm` | **today** 9 PM | Friday |
| `friday 6pm (doors 5:30)` | **today** 5:30 PM | Friday |
| `friday 6pm or 8pm`, `tomorrow 6pm or 8pm` | **today** 8 PM | Friday; tomorrow |
| `friday 7pm est / 4pm pst` | **today** 4 PM Eastern | Friday |
| `today (6pm)`, typed at 23:30 | **tomorrow** 6 PM | today |
| `yesterday (6pm)` | **today** 6 PM | refused: already passed |

The recognizer joins a day and a time across a space, a comma, `at`, ` - `, `@` and `around`.
Across anything else it returns two readings: a day, and a time. The date-and-time reader gives
each a moment of its own — the day at 9 AM, the time today and tomorrow — and takes the earliest
that is still ahead, which is nearly always the time, today. A second time in a text whose day
already has one (`ends 9pm`, `doors 5:30`, `or 8pm`) goes the same way. The day reader has a rule
for this, written for free slots — a day the text names outranks a today that is only implied —
and gives the day that was typed in every row.

So this point reaches further than the three forms the list named, and of the five it is the one
that gives a wrong date and says nothing.

### 1.5 `easter sat`

`easter saturday` is a holiday the recognizer knows by name: the Saturday before Easter Sunday,
2027-03-27 for Easter on 2027-03-28. With `sat` spelled out, `easter sat` is given to the
recognizer as `easter saturday` and reads the same, from both readers, at every moment tried: in
July, and in Easter week of 2027 on the Wednesday, the Friday, the Saturday itself early and late,
Easter Sunday and the Monday after. That is the usual meaning of the name. It is right, and
nothing about it is to change.

## 2. What I recommend

| # | Gap | Recommendation | Question |
|---|---|---|---|
| 1 | `by`, `until`, `before` | **Fix**, in the date-and-time reader: the text reads as the date and time it names. The day reader keeps its refusal. | Q1 |
| 2 | `every …` | **A better refusal**, from both readers, wherever a repeat stands in the text. | Q2 |
| 3 | A weekday on its own day | **Fix**: next week's, once today's is behind. | Q3 |
| 4 | A time apart from its day | **Fix**: the time is read on the day the text names. | Q4 |
| 5 | `easter sat` | **Leave**: it is right (1.5). | none |

Two things hold for all of it. Text longer than `DateTextLimits.MaxLength` is still refused before
anything reads it, and that check stays the first thing both readers do. And every rule below works
on what the recognizer returns, after `sat` and `sun` have been spelled out, so the short and the
full name go on reading alike.

### 2.1 `by`, `until`, `before` (Q1)

**The rule.** A range that has an end and no start reads as its end, as a range that has a start
and no end already reads as its start. `by friday 6pm` then reads exactly as `friday 6pm` does,
`until saturday` as `saturday`, `before noon` as `noon`: the word in front changes nothing.

| Text | Today | After |
|---|---|---|
| `by saturday 6pm`, `by sat 6pm`, `before saturday 6pm` | not understood | Sat Jul 18, 6 PM |
| `by friday`, `until friday`, `no later than friday` | not understood | Fri Jul 17, 9 AM, as `friday` |
| `by 6pm`, `before noon` | not understood | today 6 PM; today noon — tomorrow's once today's has passed, as `6pm` |
| `by tomorrow 6pm`, `by nov 12`, `by 2026-07-20 18:00`, `by end of day` | not understood | tomorrow 6 PM; Nov 12, 9 AM; Jul 20, 6 PM; today 11:59 PM |
| `friday before 6pm`, `friday by 6pm`, `friday until 6pm` | not understood | Fri Jul 17, 6 PM |
| `by friday night` | not understood | Fri Jul 17, 8 PM, as `friday night` |
| `friday, by 6pm` | Friday 9 AM | Fri Jul 17, 6 PM |
| `by today`, typed at 10:00; `by yesterday` | not understood | refused: past, as `today` at 10:00 and `yesterday` are |

Three limits, each there so that a text which is refused today does not become a reading that is
wrong and says nothing:

- **Not in front of a whole period.** `by the weekend`, `before next month`, `by end of month`,
  `until next week` and `until 2027` stay not understood. There the recognizer's end is the edge
  of the period — the Saturday the weekend starts on, the first of next month — and a bare date
  reads as 9 AM, which is not what a member who types `by end of month` means.
- **Not beside a length of time.** `2 hours before friday 6pm` and `30 minutes before 6pm` stay
  not understood. Read by the rule they would be Friday 6 PM and 6 PM, with the length lost.
- **The date-and-time reader only.** `TryResolveDay` keeps refusing `by friday`. Its one caller
  asks for the first day to list free slots from, and a deadline does not name a first day: Friday
  would be wrong for a member who means "between now and Friday", and today would be wrong for
  `friday before 6pm`, which the recognizer returns in the same shape. Its refusal already says
  what to type.

**What changes for text that is read today.** One form: a day, then a deadline time written apart
from it. `friday, by 6pm` is Friday 9 AM today, the time dropped, and becomes Friday 6 PM.
Everything else the rule touches is refused today.

A bare date is 9 AM here as everywhere else, so `rsvp-close:"by friday"` closes on Friday at 9 AM,
as `rsvp-close:friday` does today. The reply shows the time.

### 2.2 `every …` (Q2)

A repeat cannot be read as one date, and making an event repeat from its date text would be a
feature of its own. So the recommendation is a refusal that says what the matter is, in place of
"could not understand":

- From the date-and-time reader:
  `"every saturday 8pm" repeats. Type one date and time here, for example "saturday 8pm"; to make an event repeat, use its repeat option.`
- From the day reader:
  `"every saturday" repeats. Type one day here, for example "today", "saturday" or "nov 12".`

The example in it is fixed text, not taken from what was typed. The same sentence serves every
option that takes a date; `repeat` is what the option is called on `/create` and on `/series edit`,
and "Repeats" on the web form.

**Where a repeat stands beside something that can be read.** `friday 8pm every week` makes one
event on Friday today, and nothing says the repeat was dropped. Recommended: the same refusal
there, wherever a repeat stands in the text. **That changes text that is read today**:
`friday 8pm every week`, `friday 8pm weekly`, `weekly on friday` and
`every friday starting july 24` are each read today, as one date, and would be refused with the
hint. The narrower choice (Q2, B) gives the hint only where the text is refused already, and
leaves those four as they are.

### 2.3 A weekday typed on its own day (Q3)

**The rule.** A weekday with no week named means the next time that weekday and time come round.
On the day itself, once the time is behind — 9 AM when none is typed — that is next week's.

| Text, typed on the Wednesday | Today | After |
|---|---|---|
| `wed`, `wednesday`, `on wednesday`, at 10:00 | refused: past | Wed Jul 22, 9 AM |
| `wed 9am`, at 10:00 | refused: past | Wed Jul 22, 9 AM |
| `wed 6pm`, at 10:00 | today 6 PM | the same |
| `wed 6pm`, `wednesday night`, at 23:30 | refused: past | Wed Jul 22, 6 PM; Wed Jul 22, 8 PM |
| `friday 6pm-8pm`, typed on the Friday at 19:00 | refused: past | Fri Jul 24, 6 PM |
| `this wednesday`, `today`, `today 9am`, at 10:00 | refused: past | the same: they name today |
| `wed`, at any hour, from the day reader | today | the same |

It is the rule a time alone already follows — `9am` typed at 10:00 is tomorrow's — and what the
class says of itself: it "always resolves to the nearest future occurrence". It covers a weekday
alone, with a time, or with a span of times on that day. It does not cover a span of days
(`mon to sat`, which stays as the plan for `sat` and `sun` records it), a weekday with a day of
the month (`fri 17` is Friday the 17th), or a date (`july 15 6pm`, 1.3).

**What changes for text that is read today.** One case: a text that names such a weekday and also
a date more than a week further on. `wednesday or aug 20`, typed on a Wednesday after 9 AM, is
August 20 today and becomes Wed Jul 22 — the earliest of the days named, which is what the same
text gives on every other day of the week.

**The choice inside it.** `wed`, typed on a Wednesday morning with no time, could also mean today.
It cannot be today's 9 AM any more, so the choice is next Wednesday or a refusal that says why
(Q3, B). Recommended: next Wednesday. One rule then holds for every form, and both the bot's reply
and the web form's preview show the date that was read.

### 2.4 A time written apart from its day (Q4)

**The rule** is the day reader's own: a day the text names outranks a today that is only implied.
In the date-and-time reader that comes to three cases:

1. A time with no day of its own is read on the day the text names, when that day has no time of
   its own. The day then has its time and does not take 9 AM. A day and a time written apart
   read as the same two written together: `fri (6pm)` as `fri 6pm`, `nov 12 (9am)` as
   `nov 12 9am`. That holds on the day itself too: typed on November 12 at 10:00,
   `nov 12 (9am)` is refused as past, as `nov 12 9am` is, and is not moved to next year as
   `nov 12` alone is (1.3).
2. Where the day already has its time, a further time is not read: `friday 6pm, ends 9pm` is
   Friday 6 PM.
3. Where the text names no day, a time means today, or tomorrow once today's has passed, as now.

| Text | Today | After |
|---|---|---|
| `fri (6pm)`, `fri. 6pm`, `friday from 6pm`, `friday: 6pm`, `friday starting at 6pm`, `6pm (friday)` | today 6 PM | Fri Jul 17, 6 PM |
| `fri (9am)` | tomorrow 9 AM | Fri Jul 17, 9 AM |
| `tomorrow (6pm)`, `nov 12 (6pm)`, `next week (6pm)`, `easter 6pm` | today 6 PM | tomorrow 6 PM; Nov 12, 6 PM; Mon Jul 20, 6 PM; Easter Sunday, 6 PM |
| `friday (evening)`, `friday at night` | today 4 PM; today 8 PM | Fri Jul 17, 4 PM; Fri Jul 17, 8 PM |
| `friday or saturday (6pm)` | today 6 PM | Fri Jul 17, 6 PM |
| `friday 6pm, ends 9pm`, `friday 6pm (doors 5:30)`, `friday 6pm or 8pm` | today 9 PM; today 5:30 PM; today 8 PM | Fri Jul 17, 6 PM |
| `tomorrow 6pm or 8pm` | today 8 PM | tomorrow 6 PM |
| `friday 7pm est / 4pm pst` | today 4 PM Eastern | Fri Jul 17, 7 PM Eastern |
| `today (6pm)`, typed at 23:30 | tomorrow 6 PM | refused: past, as `today 6pm` is |
| `yesterday (6pm)` | today 6 PM | refused: past, as `yesterday 6pm` is |
| `6pm`, `friday`, `friday 6pm` | today 6 PM; Friday 9 AM; Friday 6 PM | the same |

After it, wherever both readers answer, they give the same day in every row above.

**What changes for text that is read today** is every row of that table but the last: that is the
point of the fix. In most of them a reading on the wrong day becomes the day that was typed. Four
kinds of text are worth knowing, because there the reading of today can be the one that was meant:

- **Two times offered as a choice, on the day itself.** `friday 9am or 6pm`, typed on that Friday
  at 10:00, is today 6 PM today, which is likely what was meant. Under case 2 the second time is
  not read, so with 2.3 it becomes next Friday 9 AM; `today 9am or 6pm` at 10:00 becomes a refusal
  as past. On any other day the same text is read on the wrong day today (`friday 9am or 6pm` on a
  Wednesday is today 6 PM) and becomes Friday 9 AM.
- **A time first, then a day with its own time.** `6pm or friday 8pm` is today 6 PM today and
  becomes Friday 8 PM.
- **Two times and a day apart from both.** `friday. doors 6pm, start 7pm` becomes Friday 6 PM, the
  earlier of the two; `6pm ET / 3pm PT friday` becomes Friday at 3 PM Eastern, the earlier of the
  two as typed. The day is right, and which of the times was meant cannot be told.
- **Words that are not a date.** `game night friday 7pm` is today 7 PM today, because the
  recognizer reads `night friday` as Friday night and `7pm` apart from it. It becomes Friday 8 PM:
  the right day, and the hour the recognizer gives a night.

The narrower choice (Q4, B) takes case 1 only where the text has no other time in it, and leaves
`friday 6pm, ends 9pm` reading as today 9 PM.

### 2.5 How the four sit together

Each can be taken or left on its own, with one tie: a deadline's time written apart from its day
(`friday, by 6pm`, `by friday (6pm)`) is read on that day by 2.1 and 2.4 together. If 2.4 is left
and 2.1 taken, that form keeps today's reading.

## 3. Checked on a generated set of texts

The parser as it stands on pull request #180 and a scratch copy of it with the four rules read the
same texts side by side, at nine moments: the fixture's Wednesday morning; the Friday after it,
early and late; the Saturday at 00:30, 10:00 and 23:30; the Sunday and the Monday; and
November 12 at 10:00, for `nov 12` typed on its own day. Neither the copy nor the harness is
committed.

The texts: eleven ways to name a day (`friday`, `fri`, `saturday`, `sat`, `wednesday`, `wed`,
`tomorrow`, `today`, `nov 12`, `next friday`, `july 18`), five times, and seven ways of putting a
day and a time together, in either order — three that the recognizer joins and four that it does
not; each of those bare and with `by`, `until`, `before`, `after`, `every`, `on` or
`2 hours before` in front, and with nothing, `or 8pm`, `, ends 9pm` or `every week` behind; and
with them the texts of the parser's own tests and of the probes. 25,531 texts, each read by both
readers at each moment.

- With every rule switched off, the copy answers exactly as the parser does.
- **A text the recognizer returns as one phrase, and that is not a repeat: 2,183 texts. None that
  is read today reads differently, and none that is read today is refused.** 1,023 go from not
  understood to read, all of them deadlines (2.1); 184 from not understood to refused as past,
  deadlines whose date has gone by; 425 from refused as past to read, the weekday on its own day
  (2.3); and 733 answer the same at every moment. A date that works today, typed as one phrase,
  is not touched by any of the four rules.
- **A repeat anywhere in the text: 8,672 texts**, every one refused in the words of 2.2 by both
  readers. 6,804 of them are read today at some moment, as one date with the repeat dropped; that
  is what Q2 decides.
- **Several phrases and no repeat: 14,650 texts**, the forms of 1.4, bare and with one of the
  words in front. Nearly all change, as 2.4 means them to: 83 of the 12,284 without a length of
  time answer the same at every moment.
- The day reader answers the same for every text but the repeats.
- Each rule on its own. Next week's (2.3) turns 2,072 texts from refused as past into a reading at
  some moment, and makes one text read differently: `easter mon` typed on a Monday, which is the
  case 2.3 names. The refusal for a repeat changes nothing but the 8,672. The rule for a time
  apart (2.4) changes 10,037 texts in the date-and-time reader.

Of the 131 texts in the parser's tests, 13 change at the fixture's moment, each in a way section 2
describes: `wed`, `weds` and `wednesday` become next week's; `friday at night`, `friday (6pm)`,
`saturday from 6pm`, `tomorrow, after 6pm` and `sat (6pm)` move from today to the day they name;
`yesterday (6pm)` is refused as past; `by sat 6pm`, `until sun 18:00` and `before sat at noon` are
read; and `every sat 8pm` is refused in the new words. The tests that pin the old answers for
those are the ones step 2 moves.

## 4. Questions

Q1 — `by`, `until`, `before` in front of a date

- A) Read as the date and time named, in the date-and-time reader, with the three limits of 2.1.
  The day reader keeps its refusal. **(Recommended. It is what a member typing a close as
  `by saturday 6pm` means, it mirrors how `after …` is read already, and each limit keeps a
  refusal where a reading would be a guess.)**
- B) As A, and in the day reader too: `/availability slots when:"by friday"` lists from Friday.
- C) No reading. The refusal says to type the date without the word in front.
- D) Leave it as it is.
- E) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

Q2 — `every …`

- A) The refusal of 2.2 wherever a repeat stands in the text. `friday 8pm every week`, read as one
  event today, is refused too. **(Recommended. One event where a repeating one was asked for, with
  nothing said, is the mistake worth stopping; the refusal says how to get the repeat.)**
- B) The refusal of 2.2 only where the text is refused today. `friday 8pm every week` goes on
  making one event.
- C) Leave it as it is.
- D) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

Q3 — A weekday typed on its own day

- A) Next week's, for a weekday alone and for one with a time (2.3). **(Recommended.)**
- B) Next week's only where a time is typed (`wed 9am`, `saturday 6pm`). A weekday alone is
  refused in words that say why:
  `"wed" is today, and 9:00 AM has passed. Add a time ("wed 6pm"), or type "next wed".`
- C) No reading. That refusal for both.
- D) Leave it as it is.
- E) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

Q4 — A time written apart from its day

- A) The rule of 2.4, all three cases. **(Recommended. It is the rule the day reader already
  follows, it has no list of separators to keep, and it ends the readings that put an event on
  today when a day was typed.)**
- B) Case 1 only, and only where the text has no other time in it. `friday 6pm, ends 9pm`,
  `friday 6pm (doors 5:30)` and `friday 6pm or 8pm` go on reading as today.
- C) Leave it as it is.
- D) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

Q5 — When to build

The task says to build only on a `master` that has the fix for `sat` and `sun`. Pull request #180
is open and waits for its merge.

- A) Wait for #180 to merge, then build on `master`. **(Recommended: nothing has to be moved
  afterwards, and the tests for `sat` and `sun` are there to run against.)**
- B) Build now on a local branch cut from #180's branch, and take `master` in when it merges.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

## 5. What is added, file by file

For the recommended answers. A different answer drops its rows.

| File | Change |
|---|---|
| `src/CalCrony.Api/Services/NaturalDateTimeParser.cs` | `ResolveValue` gives way to a reading of each recognizer value as a day, a time or both. `TryResolve` puts the readings together: a range's end where it has no start (2.1), next week's for a weekday with the week open (2.3), a time on the day the text names (2.4), and the refusal for a repeat (2.2). `TryResolveDay` gains that refusal, worded for a day, and nothing else. The class summary and the two method summaries. The length check stays first in both readers, and the text is still spelled out before the recognizer is given it. |
| `tests/CalCrony.Api.Tests/NaturalDateTimeParserTests.cs` | The tables of step 2, after those for `sat` and `sun`. Rows that pin what changes move into them; section 8 will list each. |
| `aidlc-docs/construction/plans/short-weekend-days-code-generation-plan.md` | The list at the end of its section 8: what became of each point, with the date. |
| `aidlc-docs/construction/plans/free-slots-code-generation-plan.md` | Section 4.1, where it says of `friday (6pm)` that the time alone would be today: a dated note that the date-and-time reader now gives the named day too. |
| `aidlc-docs/construction/build-and-test/live-e2e-script.md` | One item for each gap that is fixed. |
| `docs/release-notes/` | The release's summary, in the file for the version the change goes out in: a bullet on what is new and a short how-to. |
| `aidlc-docs/aidlc-state.md`, `aidlc-docs/audit.md` | The records. |

Not changed: Contracts, the bot, the web app, the database, every route's shape. The README, the
Docs page and the option descriptions show `tomorrow 6pm`, `in 5 hours`, `friday at 8`,
`saturday`, `nov 12` and `Aug 30`, all read as before, so none is edited unless the build shows
otherwise.

## 6. Execution plan

A box is ticked in the turn its step completes. Steps for a gap that is left are struck out when
the answers are in.

**0. Baseline**
- [ ] 0.1 On a `master` that has #180 (Q5): `dotnet build CalCrony.slnx -c Release` and
      `dotnet test CalCrony.slnx -c Release` green; counts recorded.

**1. Parser**
- [ ] 1.1 Each recognizer value read as a day, a time or both, with no zone inside the reading;
      `TryResolve` puts them together exactly as it does today (the day at 9 AM, the time today
      and tomorrow, the earliest that is ahead). The whole suite still green: nothing reads
      differently yet.
- [ ] 1.2 Q1: a range with an end and no start reads as its end, within the three limits.
- [ ] 1.3 Q2: a repeat in the text is refused in the words of 2.2, by both readers.
- [ ] 1.4 Q3: a weekday with the week open also offers next week's.
- [ ] 1.5 Q4: a time apart from a named day is read on that day; a further time beside a day that
      has its own is not read.
- [ ] 1.6 The class summary and the method summaries say what is read.

**2. Tests** — in `NaturalDateTimeParserTests`, table-driven, at the fixture's Wednesday unless a
row says otherwise
- [ ] 2.1 Deadlines, date and time: the rows of 2.1's table, each with its instant; `by` with
      `sat` and with `sun`; the three limits, each still refused; the day reader still refusing
      `by friday`.
- [ ] 2.2 Repeats: both refusals word for word; each form of 1.2; a repeat beside a date (Q2).
- [ ] 2.3 The day itself, at 00:30, 08:59, 10:00 and 23:30 on the Wednesday, and on a Saturday
      with `sat`: the rows of 2.3's table; what does not move (`this wednesday`, `today`,
      `mon to sat`, `fri 17`, `july 15 6pm`); the day reader unchanged.
- [ ] 2.4 A time apart: the rows of 2.4's table, each with its instant; the four kinds of text
      listed under it, pinned as they will read.
- [ ] 2.5 Apart reads as together: for a table of pairs (`fri (6pm)` and `fri 6pm`,
      `nov 12 (9am)` and `nov 12 9am`, `tomorrow. noon` and `tomorrow noon`), both give the same
      instant or the same refusal, at the four moments of 2.3 and on the date's own day. And the
      two readers agree: for texts that name a day ahead and a time, the instant falls on the day
      the day reader gives.
- [ ] 2.6 What must not move: a table of texts read today and untouched by any rule, each with
      the instant and the day it gives now.
- [ ] 2.7 The limit: text over 100 characters is refused in the limit's words by both readers,
      whatever it holds (`by …`, `every …`, a time apart), with dots as the filling.
- [ ] 2.8 Short and full names still agree: the rule's table from the fix for `sat` and `sun`
      gains a deadline, a repeat, the day itself and a time apart.

**3. Records in the repository**
- [ ] 3.1 The list in `short-weekend-days-code-generation-plan.md`, section 8.
- [ ] 3.2 `free-slots-code-generation-plan.md`, section 4.1.
- [ ] 3.3 `live-e2e-script.md`: the items.
- [ ] 3.4 The release summary, shown to Joshua before the pull request is opened.

**4. Verification**
- [ ] 4.1 `dotnet build CalCrony.slnx -c Release` with no new warning;
      `dotnet test CalCrony.slnx -c Release` green; counts recorded against 0.1.
- [ ] 4.2 Each rule and each limit taken out in a scratch copy fails a test meant to pin it.
- [ ] 4.3 The comparison of section 3 run again through the parser as built, against the parser
      as it is on `master`: only the kinds of change sections 2.1 to 2.4 name.
- [ ] 4.4 Two review agents read the local commits before anything is pushed, one for correctness
      and one for security and what the public text says. Their findings are answered and
      recorded in section 8.
- [ ] 4.5 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 7. Delivery

Approving this plan covers the changes above and committing them locally. Everything that leaves
this machine waits for its own go-ahead.

- [ ] 7.1 *Go-ahead:* push the branch and open the pull request, after taking `master` in if it
      has moved (the audit entries then go in by timestamp).
- [ ] 7.2 Checks green; a review if one starts. *Go-ahead:* squash merge. The test stack then takes
      `:main`.
- [ ] 7.3 *Go-ahead:* the release that carries it — the release PR that release-please opens or
      already has open — with its summary read first.
- [ ] 7.4 *Go-ahead:* prod rollout, by the usual steps: backup, the pinned tag, app services only.
      No migration.

## 8. Notes from generation

*Empty until the plan is approved and the change is built.*

**Noticed while probing, not part of this.** Each is listed for Joshua to take up or leave:

- `2 hours before friday 6pm` and `30 minutes before 6pm` could be read as Friday 4 PM and
  5:30 PM. That is a sum, not a reading, and would be a feature of its own. They stay refused.
- `by the weekend`, `by end of month`, `before next month`: deadlines at the edge of a period
  (2.1). They stay refused.
- A short weekday in a holiday's name is read as the plain weekday: `easter mon` is next Monday,
  where `easter monday` is the Monday after Easter; `good fri` and `black fri` are next Friday,
  `cyber mon` next Monday. Only `sat` and `sun` are spelled out, so `easter sat` and `palm sun`
  are right.
- `eod friday` and `friday end of day` read as today at 11:59 PM, from both readers: the
  recognizer takes "end of day" as the end of today.
- `friday 1700` is Friday at 9 AM, the number being read as the year 1700; `friday 5` is Friday
  the 5th of whichever month next has one.
- `9:00` and `09:00` typed after 9 AM are 9 PM today, alone and beside today's weekday: the
  recognizer offers both halves of the day for an hour with no `am` or `pm`.
- `july 15 6pm` typed on July 15 after 6 PM is refused as past, and from midnight on reads as
  July 15 of next year; `jul 15`, with no time, reads as next year's from 9 AM on the day (1.3).
- `tuesday, next week` gives Monday, the earlier of the two days named; the free-slots plan
  records it.
- `the 15th` and `15th` alone are not understood; `on the 15th at 6pm` is read.
