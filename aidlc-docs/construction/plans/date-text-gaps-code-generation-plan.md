# Older gaps in date text (`by friday`, `every saturday`, a weekday on its own day, a time apart from its day, `easter sat`) — functional note and code-generation plan

**Task**: five older gaps in how CalCrony reads a date typed in words, listed at the end of
section 8 of the plan for `sat` and `sun` (`short-weekend-days-code-generation-plan.md`, pull
request #180). Find out by running the real parser what each does today; recommend for each
whether to fix it, give a better refusal or leave it; and put the decision to Joshua before
anything is built. Handed over on 2026-11-16 from the session that wrote that fix.
**Baseline**: `master` 4af1305, whose code is v0.37.0, which the hosted instance runs. The parser
was run as it stands on pull request #180, open and not merged, because the five were found on
that code and `sat` and `sun` must go on reading exactly as their full names do. The change is
built on `master` b582c2b, which has that pull request.
**Branch**: `fix/date-text-gaps` → one pull request, a `fix:`, titled once the scope is decided.
**Status**: written 2026-11-16 and approved by Joshua the same day, as written, with Q1–Q5 each
answered A (section 4). Q6, which preparing the change turned up, was asked on 2026-11-17 and
answered A the same day. Generated on 2026-11-17, once pull request #180 was on `master` (Q5), and
reviewed before any push; section 8 has the notes. Nothing is pushed until Joshua says so
(section 7).

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

Q6 — "End of day" beside a day *(asked on 2026-11-17, after the plan was approved; it comes from
what preparing the change turned up, the second point of section 8)*

`by end of day friday` is Friday at 9 AM today, and with Q1 answered A it would become today at
11:59 PM, because the recognizer takes "end of day" as the end of today.

- A) Beside a day, "end of day" is that day's last second: `by end of day friday` and
  `by eod friday` are Friday at 11:59 PM. `eod friday`, `friday eod` and `friday end of day`,
  which are today at 11:59 PM now, become Friday at 11:59 PM too. Alone, `eod` stays today's.
  **(Recommended, and how the change is prepared. It is the day that was typed, by the rule of
  2.4, and it leaves no text reading on a worse day than it does today.)**
- B) Leave `eod friday` reading as today at 11:59 PM, and keep `by end of day friday` at Friday
  9 AM by not reading an end-of-day deadline where a day stands beside it.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-17: "Yes, A".

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
- [x] 0.1 On a `master` that has #180 (Q5): `dotnet build CalCrony.slnx -c Release` and
      `dotnet test CalCrony.slnx -c Release` green; counts recorded.

**1. Parser**
- [x] 1.1 Each recognizer value read as a day, a time or both, with no zone inside the reading;
      `TryResolve` puts them together exactly as it does today (the day at 9 AM, the time today
      and tomorrow, the earliest that is ahead). The whole suite still green: nothing reads
      differently yet.
- [x] 1.2 Q1: a range with an end and no start reads as its end, within the three limits.
- [x] 1.3 Q2: a repeat in the text is refused in the words of 2.2, by both readers.
- [x] 1.4 Q3: a weekday with the week open also offers next week's.
- [x] 1.5 Q4: a time apart from a named day is read on that day; a further time beside a day that
      has its own is not read.
- [x] 1.6 The class summary and the method summaries say what is read.

**2. Tests** — in `NaturalDateTimeParserTests`, table-driven, at the fixture's Wednesday unless a
row says otherwise
- [x] 2.1 Deadlines, date and time: the rows of 2.1's table, each with its instant; `by` with
      `sat` and with `sun`; the three limits, each still refused; the day reader still refusing
      `by friday`.
- [x] 2.2 Repeats: both refusals word for word; each form of 1.2; a repeat beside a date (Q2).
- [x] 2.3 The day itself, at 00:30, 08:59, 10:00 and 23:30 on the Wednesday, and on a Saturday
      with `sat`: the rows of 2.3's table; what does not move (`this wednesday`, `today`,
      `mon to sat`, `fri 17`, `july 15 6pm`); the day reader unchanged.
- [x] 2.4 A time apart: the rows of 2.4's table, each with its instant; the four kinds of text
      listed under it, pinned as they will read.
- [x] 2.5 Apart reads as together: for a table of pairs (`fri (6pm)` and `fri 6pm`,
      `nov 12 (9am)` and `nov 12 9am`, `tomorrow. noon` and `tomorrow noon`), both give the same
      instant or the same refusal, at the four moments of 2.3 and on the date's own day. And the
      two readers agree: for texts that name a day ahead and a time, the instant falls on the day
      the day reader gives.
- [x] 2.6 What must not move: a table of texts read today and untouched by any rule, each with
      the instant and the day it gives now.
- [x] 2.7 The limit: text over 100 characters is refused in the limit's words by both readers,
      whatever it holds (`by …`, `every …`, a time apart), with dots as the filling.
- [x] 2.8 Short and full names still agree: the rule's table from the fix for `sat` and `sun`
      gains a deadline, a repeat, the day itself and a time apart.

**3. Records in the repository**
- [x] 3.1 The list in `short-weekend-days-code-generation-plan.md`, section 8.
- [x] 3.2 `free-slots-code-generation-plan.md`, section 4.1.
- [x] 3.3 `live-e2e-script.md`: the items.
- [ ] 3.4 The release summary, shown to Joshua before the pull request is opened.

**4. Verification**
- [x] 4.1 `dotnet build CalCrony.slnx -c Release` with no new warning;
      `dotnet test CalCrony.slnx -c Release` green; counts recorded against 0.1.
- [x] 4.2 Each rule and each limit taken out in a scratch copy fails a test meant to pin it.
- [x] 4.3 The comparison of section 3 run again through the parser as built, against the parser
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

Generated on 2026-11-17, the day pull request #180 was merged (16:36Z, as `b582c2b`). `master`
was taken into the branch first, with this branch's audit entries put in by their timestamps, and
step 0.1 was run on that tree. The change itself had been prepared on 2026-11-16, after the plan
was approved, in a scratch copy of that pull request's tree, outside the repository. It was
applied to the branch by script, and the parser and its tests on the branch are the prepared
files, line for line. Two agents read the branch before any push (step 4.4). The numbers below
are those of the branch.

**As built.** One file under `src` changed, `NaturalDateTimeParser.cs`.

- `ResolveValue`, which turned a recognizer value straight into instants, has given way to
  `ReadValue`: one value read as a day, a time of day or both, with no zone in the reading.
  `ReadPhrase` reads the values of one phrase and, for a weekday whose week is left open, adds
  next week's (2.3).
- `TryResolve` sorts the readings into days that have their own time, days alone, kept phrase by
  phrase, and times alone. It puts them together by the rule of 2.4 and only then turns them into
  instants. From there it is as it was: nothing read is "could not understand", nothing ahead is
  "in the past", and the earliest that is ahead is the answer, with the minute's grace.
- 2.1 is one branch in `ReadValue`, which takes a range's end where it has no start.
  `IsEndOfADateOrTime` holds the limit at the edge of a period, and `readEnds` the limit beside a
  length of time.
- 2.2 is `IsRepeat` and two sentences, `RepeatError` and `RepeatDayError`, asked by both readers
  as soon as the recognizer has answered. It is the only change to `TryResolveDay`.
- `IsUnderWay`, `EndOfDayTimex` and `DaysForATime` are for the points under "Where it differs",
  below.
- The class summary and the summaries of both methods say what is read.

The length check is still the first thing both readers do. In `TryResolve` the zone abbreviation
is still taken out next, and `sat` and `sun` are still spelled out before the recognizer is given
the text. Each reader still asks the recognizer once.

**Where it differs from the plan as approved.** In the rules, two kinds of text that the rules as
section 2 words them would have read worse than `master` reads them. Both were found while the
change was being prepared. In the change as built each keeps the right day, and the rules are
otherwise as approved.

1. **A time beside a range that is under way.** `this week 6pm`, `this month 6pm` and
   `mon to sat 6pm`, typed on the Wednesday, are today at 6 PM; so is `this weekend 6pm` typed on
   the Sunday; and `tonight (9pm)` typed at 8:30 PM, when the night has begun, is today at 9 PM.
   By 2.4 the time would have been read on the day the range began, which has passed, and the
   text refused as past. The day reader already has the answer: a range that has begun and not
   ended means the rest of it, and names no day. So a time beside such a range stays a time
   alone, today's or tomorrow's, as before. A range that has not begun names its first day —
   `this weekend 6pm` on a Wednesday is Saturday at 6 PM, where it was today — and one that is
   over is refused as past: `last week (6pm)`, which was today at 6 PM.
2. **"End of day" beside a day.** `by end of day friday` and `by eod friday` were Friday at 9 AM:
   the recognizer takes "end of day" as today's last second, the deadline was not read, and
   Friday was. With 2.1 the deadline is read, and the text would have become today at 11:59 PM.
   "End of day" is a time with no day of its own, which is what 2.4 is about, so beside a day it
   is that day's last second: `by end of day friday` is Friday at 11:59 PM. **That also changes
   text that was read**: `eod friday`, `friday eod` and `friday end of day` were today at
   11:59 PM and are Friday at 11:59 PM. Alone, `eod` and `by end of day` are today's, as before.
   The day reader is not changed and still gives today for `eod friday`. This was put to Joshua
   as Q6 and answered A on 2026-11-17 (section 4).

Smaller points, each inside what was approved:

- `by late friday` and `by early friday` stay not understood, with the whole periods of 2.1: the
  recognizer gives their end as the midnight after the day, or before it. `by the end of friday`
  is read, as Friday at 11:59 PM.
- A deadline follows 2.3 as any weekday does: `by friday 6pm`, typed on that Friday at 19:00, is
  the Friday after at 6 PM, as `friday 6pm` is.
- A span of days that begins today is left as it is, as 2.3 says of `mon to sat`: `fri-sun` typed
  on that Friday at 10:00 is refused as past, and is today from the day reader.
- A part of the day is the day's own time: `friday night (9pm)`, typed before the Friday, is
  Friday at 8 PM, where the night begins, and the further time is not read (2.4, case 2). It was
  today at 9 PM.
- One check in the rule of 2.3 is reached by no text that was found. Next week's is added only
  where the recognizer's two values are a week apart. `fri 17`, Friday the 17th, is written by
  the recognizer as `fri` is and has values months apart, but it never has two that have both
  passed, so adding a week to it would change no answer. The check stays, as the rule's own
  premise, and no test can fail without it (step 4.2).

In how it was built: the steps of 1 were not taken one after another on the branch. They were
taken that way while the change was prepared, and step 1.1 was checked there: with every rule
switched off, the scratch copy answered as the parser of pull request #180 does, over every list
of probes and the generated texts of section 3. On the branch the change arrived whole. What
holds 1.1 there is the test of 2.6, the suite, and the comparison of step 4.3.

In the tests: 216 more cases, so that `NaturalDateTimeParserTests` has 366 where it had 150.

| Step | Tests |
|---|---|
| 2.1 | `A_deadline_reads_as_the_date_and_time_it_names` (21 texts); `A_deadline_at_the_end_of_a_day_is_that_days_last_second`; `The_word_in_front_of_a_deadline_changes_nothing` (18 dates, each with `by`, `until` and `before`, at five moments); `A_deadline_at_the_edge_of_a_period_is_still_not_understood` (7); `A_deadline_beside_a_length_of_time_is_still_not_understood` (3); `A_length_of_time_beside_a_date_that_is_not_a_deadline_changes_nothing`; `A_deadline_that_has_passed_is_refused_as_past` (3); `The_day_reader_does_not_take_a_deadline_for_a_first_day` (6) |
| 2.2 | `Text_that_repeats_is_refused_in_words_that_say_so` (16 texts, both refusals word for word); `The_refusal_for_a_repeat_quotes_what_the_other_refusals_quote` |
| 2.3 | `A_weekday_typed_on_its_own_day_is_next_weeks_once_its_time_is_behind` (19 rows, at 00:30, 08:59, 10:00 and 23:30); `What_names_today_or_a_span_of_days_is_still_refused_as_past` (5); `A_weekday_with_a_day_of_the_month_is_not_moved_a_week`; `A_span_of_days_that_begins_today_is_not_moved_a_week`; `For_the_day_reader_a_weekday_typed_on_its_own_day_is_today_at_any_hour` (4 moments); `A_weekday_named_beside_a_later_date_is_the_earlier_of_the_two_on_its_own_day_too`; and, for a Saturday with `sat`, the older `On_the_day_itself_the_short_form_reads_as_the_full_name_does` |
| 2.4 | `A_time_written_apart_from_its_day_is_read_on_that_day` (21); `A_time_apart_is_read_on_a_date_in_another_month_and_on_a_holiday`; `The_end_of_day_is_read_on_the_day_the_text_names` (8); `A_further_time_beside_a_day_that_has_its_own_is_not_read` (9); `A_further_time_in_another_zone_is_not_read_either`; `Two_times_and_a_day_apart_from_both_are_the_earlier_time_on_that_day`; `Two_times_offered_as_a_choice_on_the_day_itself_are_the_first_of_them`; `A_time_beside_a_range_that_is_under_way_is_a_time_alone_as_before` (8); `A_range_names_its_first_day_until_it_has_begun_and_no_day_once_it_has` |
| 2.5 | `A_day_and_a_time_written_apart_read_as_the_same_two_written_together` (17 pairs, at six moments); `The_two_readers_give_the_same_day_for_a_day_and_a_time_apart` (11) |
| 2.6 | `Dates_that_were_read_before_are_read_as_before` (20) |
| 2.7 | `Text_over_the_length_limit_is_refused_before_any_of_it_is_read` (3); `A_deadline_and_a_repeat_at_the_length_limit_are_read_and_refused_as_shorter_ones_are` |
| 2.8 | Seven rows more in `A_short_weekend_day_reads_exactly_as_its_full_name`: a deadline, three ways; a length of time before one; a repeat behind a date; and a time apart, two ways and beside a further time |

Two older tests pinned an answer that changes, and moved with it. Both belong to the fix for
`sat` and `sun`:

- `Where_the_full_name_is_not_understood_neither_is_the_short_form` had four rows: `by sat 6pm`,
  `until sun 18:00`, `before sat at noon` and `every sat 8pm`. Each has an answer of its own now,
  in the tables of 2.1 and 2.2. Two texts that are still not understood stand in their place:
  `2 hours before sat 6pm` and `30 minutes before sun noon`.
- `On_the_day_itself_the_short_form_reads_as_the_full_name_does` had `sat` and `sat 9am`, typed on
  the Saturday at 10:00, refused as past. They are the Saturday after at 9 AM, and `sat 6pm` late
  that evening, the Saturday after at 6 PM, is added.

**Step 0.1.** On the branch with `master` taken in: `dotnet build CalCrony.slnx -c Release` with
no error and 22 warnings, and `dotnet test CalCrony.slnx -c Release` with 1,729 tests green —
813 API, 418 bot, 153 web and 345 for the release-notes tool, with 17 more of the tool's skipped
on Windows, which CI runs.

**Step 4.1.** `dotnet build CalCrony.slnx -c Release --no-incremental`: no error and 22 warnings,
in the same files and with the same codes as at 0.1, none of them in the parser or its tests.
`dotnet test CalCrony.slnx -c Release`: 1,945 tests green — 1,029 API, 418 bot, 153 web and 345
for the tool, with the same 17 skipped. The 216 more are all in `NaturalDateTimeParserTests`.

**Step 4.2 — each rule and each limit taken out, in a scratch copy of the tree.** Thirty-two
changes, one at a time, each run against the parser's 366 tests. Thirty-one failed tests, the
tests meant for them among them. One failed none: the check named above, which no text reaches.

| Taken out or changed | Failed | The tests meant for it |
|---|---|---|
| 2.1 — a range with an end and no start is never read | 47 | the table of deadlines; the rule, with and without the word in front; the end of a day; a deadline at the length limit |
| 2.1 — the limit at the edge of a whole period, removed | 4 | `by the weekend`, `before next month`, `until next week`, `until 2027` |
| 2.1 — the limit to a plain `before` or `until`, removed | 2 | `by late friday`, `by early friday` |
| 2.1 — the limit beside a length of time, removed | 5 | its table of three; the two rows with `sat` and `sun` |
| 2.1 — the day reader takes a deadline's day | 18 | the day reader's table of six, and its half of the tables for an edge, a length of time and the short forms |
| 2.2 — `TryResolve` does not refuse a repeat | 18 | the table of repeats; what the refusal quotes; a repeat at the length limit |
| 2.2 — `TryResolveDay` does not refuse a repeat | 17 | the table of repeats; what the refusal quotes |
| 2.2 — `TryResolve` refuses a repeat only where nothing else is read | 4 | the four rows with a date beside the repeat |
| 2.2 — `TryResolveDay` refuses a repeat only where nothing else is read | 4 | the same four |
| 2.2 — the two sentences swapped, in `TryResolve` | 16 | the table of repeats |
| 2.2 — the two sentences swapped, in `TryResolveDay` | 16 | the table of repeats |
| 2.2 — `TryResolve` quoting the spelled-out text | 13 | `every sat 8pm`; the older tests for what a refusal quotes |
| 2.3 — next week's is never added | 14 | the table of the day itself; the Saturday with `sat`; a weekday beside a later date; two times offered as a choice |
| 2.3 — the check for values a week apart, removed | 0 | none: no text reaches it |
| 2.3 — a span of days is moved a week too | 1 | `fri-sun` on the Friday |
| 2.3 — a span of times on the day is not moved | 1 | `wed 6pm-8pm` at 23:30 |
| 2.3 — two weeks on, not one | 14 | as the first of 2.3 |
| 2.4 — a time apart is never read on the day | 57 | the table of times apart; the rule, apart and together; the two readers; the end of day |
| 2.4 — a time apart is read today as well, and the day keeps its 9 AM | 57 | the same |
| 2.4 — a further time beside a day that has its own is read, today | 12 | the table of further times; a further time in another zone; the two readers; two times offered as a choice |
| 2.4 — a phrase that can mean today keeps its later readings for a time | 3 | the rule: `jul 15 (9am)` and the like, typed on the day |
| 2.4 — next week's weekday is dropped where the phrase can mean today | 4 | the rule: `wed (9am)` and the like, typed on the day |
| 2.4 — a range under way counts as a day that is named | 10 | the table of ranges under way; `this weekend 6pm` on the Sunday; `this week (6pm)` from both readers |
| 2.4 — a range of days under way is not seen as one | 7 | the ranges of days in those tests |
| 2.4 — a range of times under way is not seen as one | 3 | `tonight (9pm)`, `this afternoon (3pm)`, `this morning (11am)` |
| 2.4 — a range of times that has ended still counts as under way | 2 | `friday night (9pm)`, `game night friday 7pm` |
| 2.4 — a range of times that has not begun counts as under way | 2 | the same two |
| 2.4 — a part of the day under way still counts as a day with its time | 3 | the three parts of the day under way |
| 2.4 — a range of days under way loses its first day at 9 AM | 2 | `mon to sat` on the Wednesday, refused as past and not as not understood |
| Q6 — "end of day" keeps today for its day | 6 | the table for the end of day |
| As before — a bare day is 8 AM, not 9 AM | 40 | every table with a bare day |
| As before — a time alone is today's only, never tomorrow's | 4 | `9am`, `by 9am`, `this week 6pm` late in the day, and the older test for a time that has passed |

**Step 4.3 — the comparison of section 3 again, through the parser as built.** `master`'s parser,
copied under another name, and the parser on the branch read the same texts side by side in a
scratch harness, at the nine moments of section 3. The texts are those of section 3 with the
texts of the tests as built added: 25,707 texts, 231,363 cases. Text by text, the kinds of change
are the same as in the run made on the prepared copy the day before.

- **One phrase to the recognizer, and not a repeat: 2,291 texts. None that `master` reads is read
  differently, and none is refused.** 1,046 go from not understood to read, the deadlines of 2.1;
  189 from not understood to refused as past, deadlines whose date has gone by; 454 from refused
  as past to read, the weekday on its own day (2.3); and 786 answer the same at every moment.
  184 of them are in two of those groups, at different moments.
- **A repeat among the phrases: 8,682 texts**, every one refused in the words of 2.2 by both
  readers. `master` reads 6,809 of them at some moment, as one date with the repeat dropped.
- **Several phrases and no repeat: 14,708 texts**, 2,370 of them with a length of time. Of the
  12,338 others, 103 answer the same at every moment; the rest change as 2.1, 2.3 and 2.4 mean
  them to.
- 26 texts in which the recognizer finds nothing are refused as before.
- The day reader answers the same for every text but the 8,682 repeats.

**Noticed while probing, not part of this.** Each is listed for Joshua to take up or leave:

- `2 hours before friday 6pm` and `30 minutes before 6pm` could be read as Friday 4 PM and
  5:30 PM. That is a sum, not a reading, and would be a feature of its own. They stay refused.
  The mirror image is read, as it was before: `2 hours after friday 6pm` is Friday at 6 PM, with
  the length lost, because `after …` is a range with a start and has always been read.
- `by the weekend`, `by end of month`, `before next month`: deadlines at the edge of a period
  (2.1). They stay refused.
- From the day reader, `by friday (6pm)` is today, as before: the deadline is not read there, and
  the time alone means today.
- A short weekday in a holiday's name is read as the plain weekday: `easter mon` is next Monday,
  where `easter monday` is the Monday after Easter; `good fri` and `black fri` are next Friday,
  `cyber mon` next Monday. Only `sat` and `sun` are spelled out, so `easter sat` and `palm sun`
  are right.
- `eod friday` and `friday end of day` are today from the day reader: the recognizer takes "end
  of day" as the end of today, and that reader reads the date in it. *(The date-and-time
  reader's side of this was taken up as Q6: see the second point near the top of this section.)*
- `friday 1700` is Friday at 9 AM, the number being read as the year 1700; `friday 5` is Friday
  the 5th of whichever month next has one.
- `9:00` and `09:00` typed after 9 AM are 9 PM today, alone and beside today's weekday: the
  recognizer offers both halves of the day for an hour with no `am` or `pm`.
- `july 15 6pm` typed on July 15 after 6 PM is refused as past, and from midnight on reads as
  July 15 of next year; `jul 15`, with no time, reads as next year's from 9 AM on the day (1.3).
- `tuesday, next week` gives Monday, the earlier of the two days named; the free-slots plan
  records it.
- `the 15th` and `15th` alone are not understood; `on the 15th at 6pm` is read.
