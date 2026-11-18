# Older gaps in date text (`by friday`, `every saturday`, a weekday on its own day, a time apart from its day, `easter sat`) — functional note and code-generation plan

**Task**: five older gaps in how CalCrony reads a date typed in words, listed at the end of
section 8 of the plan for `sat` and `sun` (`short-weekend-days-code-generation-plan.md`, pull
request #180). Find out by running the real parser what each does today; recommend for each
whether to fix it, give a better refusal or leave it; and put the decision to Joshua before
anything is built. Handed over on 2026-11-16 from the session that wrote that fix.
**Baseline**: `master` 4af1305, whose code is v0.37.0, which the hosted instance runs. The parser
was run as it stands on pull request #180, open and not merged, because the five were found on
that code and `sat` and `sun` must go on reading exactly as their full names do. The change was
built on `master` b582c2b, which has that pull request, and `master` has been taken in since,
last at 5fc9239.
**Branch**: `fix/date-text-gaps` → one pull request, a `fix:`, titled once the scope is decided.
**Status**: written 2026-11-16 and approved by Joshua the same day, as written, with Q1–Q5 each
answered A (section 4). Q6, which preparing the change turned up, was asked on 2026-11-17 and
answered A the same day. Generated on 2026-11-17, once pull request #180 was on `master` (Q5), and
reviewed before any push; section 8 has the notes. At the gate on 2026-11-18 Joshua answered B:
push the branch and open the pull request (section 7).

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
- [x] 3.4 The release summary, shown to Joshua before the pull request is opened.

**4. Verification**
- [x] 4.1 `dotnet build CalCrony.slnx -c Release` with no new warning;
      `dotnet test CalCrony.slnx -c Release` green; counts recorded against 0.1.
- [x] 4.2 Each rule and each limit taken out in a scratch copy fails a test meant to pin it.
- [x] 4.3 The comparison of section 3 run again through the parser as built, against the parser
      as it is on `master`: only the kinds of change sections 2.1 to 2.4 name.
- [x] 4.4 Two review agents read the local commits before anything is pushed, one for correctness
      and one for security and what the public text says. Their findings are answered and
      recorded in section 8.
- [x] 4.5 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 7. Delivery

Approving this plan covers the changes above and committing them locally. Everything that leaves
this machine waits for its own go-ahead.

- [x] 7.1 *Go-ahead:* push the branch and open the pull request, after taking `master` in if it
      has moved (the audit entries then go in by timestamp). Pull request #199, 2026-11-18.
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
was approved, in a scratch copy of that pull request's tree, outside the repository, and was
applied to the branch by script. Two agents then read the branch, three times (step 4.4), and
their findings changed the code, each change set out below. The numbers here are those of the code after
those answers unless a paragraph says otherwise.

**As built.** One file under `src` changed, `NaturalDateTimeParser.cs`.

- `ResolveValue`, which turned a recognizer value straight into instants, has given way to
  `ReadValue`: one value read as a day, a time of day or both, with no zone in the reading.
  `ReadPhrase` reads the values of one phrase and, for a weekday whose week is left open, adds
  next week's (2.3).
- `TryResolve` sorts the readings into days that have their own time; the days each phrase names,
  kept phrase by phrase as `NamedDay`, with its start for a part of the day; times alone; and the
  first days of phrases that name no single day. It puts them together by the rule of 2.4 and
  only then turns them into instants. From there it is as it was: nothing read is "could not
  understand", nothing ahead is "in the past", and the earliest that is ahead is the answer, with
  the minute's grace.
- 2.1 is one branch in `ReadValue`, which takes a range's end where it has no start.
  `IsReadByItsEnd` holds its limits: `IsEndOfADateOrTime` the edge of a period, and `readEnds` a
  length of time.
- 2.2 is `IsRepeat` and two sentences, `RepeatError` and `RepeatDayError`, asked by both readers
  as soon as the recognizer has answered. It is the only change to `TryResolveDay`.
- `TodayWhereNamed` keeps today where a phrase can mean today: when a time is read on it (2.4,
  case 1: `nov 12 (9am)` typed on November 12 is that morning) and when it is a deadline.
- `IsUnderWay`, `IsWholePeriod`, `IsPartOfDay` and `EndOfDayTimex` are for the points under
  "Where it differs", below.
- The class summary and the summaries of both methods say what is read.

In both readers a missing text and then the length are still checked before anything else — the
first of the two since pull request #184 — and in `TryResolve` the zone abbreviation is still
taken out next. `sat` and `sun` are still spelled out before the recognizer is given the text, and
each reader still asks the recognizer once.

**Where it differs from the plan as approved.** Six points, each keeping a text on the day it was
read on, or putting it on the day it means, where the rules as section 2 words them would not.
The first two were found while the change was being prepared, the others by the review of
correctness in its reads (step 4.4), which also added to the first, the third and the fifth.
The rules are otherwise as approved.

1. **A time beside a range that is under way.** `this week 6pm`, `this month 6pm` and
   `mon to sat 6pm`, typed on the Wednesday, are today at 6 PM; so are `this weekend 6pm` typed
   on the Sunday and `tonight (9pm)` typed at 8:30 PM, when the night has begun. By 2.4 the time
   would have been read on the day the range began, which has passed, and the text refused as
   past. The day reader already has the answer: a range that has begun and not ended means the
   rest of it, and names no day. So a time beside such a range stays a time alone, today's or
   tomorrow's, as before. A range of days has begun on its first day too: `this weekend 6pm`,
   typed on the Saturday at 7 PM, is Sunday at 6 PM, and `this week 6pm`, typed on the Monday at
   7 PM, Tuesday at 6 PM, as before. And beside a time, such a phrase no longer offers its first
   day at 9 AM, as `master` does, so the time is meant even before 9 AM on that day:
   `this weekend 6pm`, typed on the Saturday at 00:30, is that day at 6 PM, where it was 9 AM.
   A range that has not begun names its first day —
   `this weekend 6pm` on a Wednesday is Saturday at 6 PM, where it was today — and one that is
   over is refused as past: `last week (6pm)`, which was today at 6 PM.
2. **"End of day" beside a day.** `by end of day friday` and `by eod friday` were Friday at 9 AM:
   the recognizer takes "end of day" as today's last second, the deadline was not read, and
   Friday was. With 2.1 the deadline is read, and the text would have become today at 11:59 PM.
   "End of day" is a time with no day of its own, which is what 2.4 is about, so beside a day it
   is that day's last second: `by end of day friday` is Friday at 11:59 PM. **That also changes
   text that was read**: `eod friday`, `friday eod` and `friday end of day` were today at
   11:59 PM and are Friday at 11:59 PM. Alone, `eod` is today's, as before, and `by end of day`,
   which was not understood, is today's too. The day reader is not changed and still gives today
   for `eod friday`. This was put to Joshua as Q6 and answered A on 2026-11-17 (section 4).
3. **A part of the day with a time beside it.** As prepared, a part of the day (`friday night`,
   `tonight`, `tomorrow morning`) counted as the day's own time, so a time written beside it was
   not read (2.4, case 2): `tonight (7pm)` became today at 8 PM, where the night begins, though it
   was read as 7 PM, and `friday night (9pm)` became Friday at 8 PM. A part of the day now takes a
   time written beside it as its own, as the two written together are read (2.4, case 1):
   `tonight (7pm)` is today at 7 PM, as before; `friday night (9pm)` is Friday at 9 PM and
   `tomorrow evening (7pm)` tomorrow at 7 PM, where both were today; and `game night friday 7pm`,
   in which the recognizer reads `night friday` as Friday night, is Friday at 7 PM, where 2.4 said
   Friday at 8 PM. With no time beside it, a part of the day begins at its start: `friday night`
   is Friday at 8 PM. An end (`by 6pm`, `until 2am`) is not such a time. An end is the time only
   where the text has no other time and no part of the day, as `friday, by 6pm` is Friday at 6 PM
   by 2.1. Beside a time of its own it is an end and not read: `friday (9pm) till 2am` and
   `friday night (9pm) till 2am` are Friday at 9 PM, where both were today. Beside a part of the
   day it ends the part, which begins at its start, whether the part has begun or not and on
   whichever day the text names: `friday night till 2am` is Friday at 8 PM,
   `this evening until 9pm` today at 4 PM and `friday or saturday night until midnight` Friday at
   9 AM, as before; `tonight until 2am`, typed at 9 PM, is refused as past, as before; and typed
   during that Friday night, `friday night till 2am` is the next Friday at 8 PM, as
   `friday night` is then (2.3). A part of the day that is itself the deadline is read by its
   end, as any deadline is: `by night` is tonight at 8 PM, as `night` is; and an end beside such a
   part is not read either: `by friday night (until 2am)` is Friday at 8 PM. Two kinds of text
   read worse: an end written as a plain time
   beside a part of the day that has no start time, `tonight, ends 11pm`, is today at 11 PM,
   where it was today at 8 PM; and a deadline beside a time meant for something else,
   `friday, by 6pm (party 8pm)`, is Friday at 8 PM, and `by 6pm, ends 9pm` is 9 PM, as `master`
   reads it.
4. **A span from one weekday to another.** `fri 6pm to sun 6pm`, typed on that Friday after
   6 PM, would have been read as the Friday after, because the recognizer writes such a span, as
   it writes a weekday, with the week left open. 2.3 leaves a span of days alone, and the code now
   does too: such text is refused as past, as before. A span of times on one day
   (`friday 6pm-8pm`) is still moved, as 2.3 says.
5. **A deadline on today's date.** `by jul 15`, typed on July 15 after 9 AM, would have been read
   as July 15 of next year, because `jul 15` alone is: the recognizer offers this year's and next
   year's. A deadline that can mean today means today, so it is refused as past, as `by today`
   is, and so is one written with a part of the day: `by jul 15 evening`, typed that evening, is
   refused as past, and `by jul 15 night`, typed at 5 PM, is that night. `jul 15` alone is not
   changed.
6. **A whole period beside a time.** `6pm (may change)` would have been read as May 1 of next year
   at 6 PM, `6pm room 2027` as January 1, 2027, and `next month (6pm)` as August 1: the time read
   on the first day of the month or the year. A month, a season or a year is not a day, so a time
   beside a whole period longer than a week stays a time alone: each of these is today at 6 PM,
   as before. With the first day no longer offered beside a time (point 1), `next month (6pm)`,
   typed on December 31 at 11:30 PM, is January 1 at 6 PM, where it was 9 AM. A week names its first day, as 2.4 has it (`next week (6pm)`), and so does a span
   the text spells out (`jul 20 to jul 31 (6pm)`).

Smaller points, each inside what was approved:

- `by late friday` and `by early friday` stay not understood, with the whole periods of 2.1: the
  recognizer gives their end as the midnight after the day, or before it. `by the end of friday`
  is read, as Friday at 11:59 PM, and `on or before friday`, which the recognizer marks
  differently from `by`, as Friday at 9 AM.
- A deadline follows 2.3 as any weekday does: `by friday 6pm`, typed on that Friday at 19:00, is
  the Friday after at 6 PM, as `friday 6pm` is.
- A span of days that begins today is left as it is, as 2.3 says of `mon to sat`: `fri-sun` typed
  on that Friday at 10:00 is refused as past, and is today from the day reader.
- One check in the rule of 2.3 is reached by no text that was found. Next week's is added only
  where the recognizer's two values are a week apart. Some text has the timex of a weekday and
  values months apart: `fri 17`, Friday the 17th, on any day but that Friday. But its later value
  is always ahead, and on that Friday the recognizer writes it as that date, so adding a week to
  it would change no answer. The check stays, as the rule's own premise, and no test can fail
  without it (step 4.2).

In how it was built: the steps of 1 were not taken one after another on the branch. They were
taken that way while the change was prepared, and step 1.1 was checked there: with every rule
switched off, the scratch copy answered as the parser of pull request #180 does, over every list
of probes and the generated texts of section 3. On the branch the change arrived whole. What
holds 1.1 there is the test of 2.6, the suite, and the comparison of step 4.3.

In the tests: 259 more cases than `master` has, so that `NaturalDateTimeParserTests` has 435
where `master` has 176 (150 before pull request #184, whose tests are among the 176).

| Step | Tests |
|---|---|
| 2.1 | `A_deadline_reads_as_the_date_and_time_it_names` (23 texts); `A_deadline_at_the_end_of_a_day_is_that_days_last_second`; `The_word_in_front_of_a_deadline_changes_nothing` (18 dates, each with `by`, `until` and `before`, at five moments); `A_deadline_at_the_edge_of_a_period_is_still_not_understood` (7); `A_deadline_beside_a_length_of_time_is_still_not_understood` (3); `A_length_of_time_beside_a_date_that_is_not_a_deadline_changes_nothing`; `A_deadline_that_has_passed_is_refused_as_past` (5, today's date among them); `A_deadline_on_todays_date_is_todays_with_a_part_of_the_day_too`, with a date alone beside it; `The_day_reader_does_not_take_a_deadline_for_a_first_day` (6) |
| 2.2 | `Text_that_repeats_is_refused_in_words_that_say_so` (16 texts, both refusals word for word); `The_refusal_for_a_repeat_quotes_what_the_other_refusals_quote` |
| 2.3 | `A_weekday_typed_on_its_own_day_is_next_weeks_once_its_time_is_behind` (19 rows, at 00:30, 08:59, 10:00 and 23:30); `What_names_today_or_a_span_of_days_is_still_refused_as_past` (5); `A_weekday_with_a_day_of_the_month_is_not_moved_a_week`; `A_span_of_days_that_begins_today_is_not_moved_a_week` (with two spans written with times); `For_the_day_reader_a_weekday_typed_on_its_own_day_is_today_at_any_hour` (4 moments); `A_weekday_named_beside_a_later_date_is_the_earlier_of_the_two_on_its_own_day_too`; and, for a Saturday with `sat`, the older `On_the_day_itself_the_short_form_reads_as_the_full_name_does` |
| 2.4 | `A_time_written_apart_from_its_day_is_read_on_that_day` (22); `A_time_apart_is_read_on_a_date_in_another_month_and_on_a_holiday`; `The_end_of_day_is_read_on_the_day_the_text_names` (8); `A_further_time_beside_a_day_that_has_its_own_is_not_read` (6); `A_part_of_the_day_takes_a_time_written_beside_it_as_its_own` (10); `An_end_beside_a_part_of_the_day_ends_it_and_is_not_its_time` (8); `An_end_beside_a_part_of_the_day_that_has_begun_is_not_read_either`; `An_end_is_the_time_only_where_the_text_has_no_other` (6); `A_part_of_the_day_with_no_time_beside_it_begins_at_its_start` (4); `A_further_time_in_another_zone_is_not_read_either`; `Two_times_and_a_day_apart_from_both_are_the_earlier_time_on_that_day`; `Two_times_offered_as_a_choice_on_the_day_itself_are_the_first_of_them`; `A_time_beside_a_range_that_is_under_way_is_a_time_alone_as_before` (9); `A_range_names_its_first_day_until_it_has_begun_and_no_day_once_it_has`; `A_time_beside_a_whole_period_is_a_time_alone_as_before` (6); `A_time_beside_a_phrase_that_names_no_day_is_meant_before_9am_too` |
| 2.5 | `A_day_and_a_time_written_apart_read_as_the_same_two_written_together` (20 pairs, at six moments); `The_two_readers_give_the_same_day_for_a_day_and_a_time_apart` (11) |
| 2.6 | `Dates_that_were_read_before_are_read_as_before` (20) |
| 2.7 | `Text_over_the_length_limit_is_refused_in_the_limits_words_whatever_it_holds` (3); `A_deadline_and_a_repeat_at_the_length_limit_are_read_and_refused_as_shorter_ones_are`. That nothing reads the text before the length is checked is in the code; a test cannot see it. |
| 2.8 | Seven rows more in `A_short_weekend_day_reads_exactly_as_its_full_name`: a deadline, two more ways; a length of time before a deadline; a repeat behind a date; and a time apart, two ways and beside a further time |

Two older tests pinned an answer that changes, and moved with it. Both belong to the fix for
`sat` and `sun`:

- `Where_the_full_name_is_not_understood_neither_is_the_short_form` had four rows: `by sat 6pm`,
  `until sun 18:00`, `before sat at noon` and `every sat 8pm`. Each has an answer of its own now,
  in the tables of 2.1 and 2.2. Two texts that are still not understood stand in their place:
  `2 hours before sat 6pm` and `30 minutes before sun noon`.
- `On_the_day_itself_the_short_form_reads_as_the_full_name_does` had `sat` and `sat 9am`, typed on
  the Saturday at 10:00, refused as past. They are the Saturday after at 9 AM, and `sat 6pm` late
  that evening, the Saturday after at 6 PM, is added.

**Step 0.1.** On the branch with `master` taken in at `b582c2b`: `dotnet build CalCrony.slnx
-c Release` with no error and 22 warnings, and `dotnet test CalCrony.slnx -c Release` with 1,729
tests green — 813 API, 418 bot, 153 web and 345 for the release-notes tool, with 17 more of the
tool's skipped on Windows, which CI runs.

**Step 4.1.** On the branch as it stands at the end, with `master` taken in at `162fd42` and every
read of the reviews answered: `dotnet build CalCrony.slnx -c Release --no-incremental` with no
error and 22 warnings, in the same files and with the same codes as at 0.1, none of them in the
parser or its tests; and `dotnet test CalCrony.slnx -c Release` with 2,044 tests green — 1,124
API, 418 bot, 153 web and 349 for the tool, with the same 17 skipped. Of the API's, 259 are this
change's, all in `NaturalDateTimeParserTests`; the rest of the growth since 0.1 came with
`master`. Before the reviews, on the tree of 0.1, the same two commands gave 1,945 tests green.

**Step 4.2 — each rule and each limit taken out, in a scratch copy of the tree.** Fifty-two
changes, one at a time, each run against the parser's 435 tests: the 32 of the change as first
built, kept or brought up to date, and twenty for what the reads of the reviews added. Fifty-one
failed tests, the tests meant for them among them. One failed none: the check named above, which
no text reaches.

| Taken out or changed | Failed | The tests meant for it |
|---|---|---|
| 2.1 — a range with an end and no start is never read | 58 | the table of deadlines; the rule, with and without the word in front; the end of a day; parts of the day as deadlines; deadlines that have passed; an end as the time; a deadline at the length limit |
| 2.1 — the limit at the edge of a whole period, removed | 4 | `by the weekend`, `before next month`, `until next week`, `until 2027` |
| 2.1 — the limit to a plain `before` or `until`, removed | 2 | `by late friday`, `by early friday` |
| 2.1 — the limit beside a length of time, removed | 5 | its table of three; the two rows with `sat` and `sun` |
| 2.1 — the day reader takes a deadline's day | 18 | the day reader's table of six, and its half of the tables for an edge, a length of time and the short forms |
| 2.1 — a deadline on today's date keeps next year's (point 5) | 3 | `by jul 15`, `until jul 15`, `by jul 15 evening` |
| 2.1 — every phrase that can mean today kept to today, a date alone too | 1 | `jul 15` alone on its own day |
| 2.1 — `on or before` not read | 1 | `on or before friday` |
| 2.2 — `TryResolve` does not refuse a repeat | 18 | the table of repeats; what the refusal quotes; a repeat at the length limit |
| 2.2 — `TryResolveDay` does not refuse a repeat | 17 | the table of repeats; what the refusal quotes |
| 2.2 — `TryResolve` refuses a repeat only where nothing else is read | 4 | the four rows with a date beside the repeat |
| 2.2 — `TryResolveDay` refuses a repeat only where nothing else is read | 4 | the same four |
| 2.2 — the two sentences swapped, in `TryResolve` | 16 | the table of repeats |
| 2.2 — the two sentences swapped, in `TryResolveDay` | 16 | the table of repeats |
| 2.2 — `TryResolve` quoting the spelled-out text | 13 | `every sat 8pm`; the older tests for what a refusal quotes |
| 2.3 — next week's is never added | 15 | the table of the day itself; the Saturday with `sat`; a weekday beside a later date; two times offered as a choice; a Friday night begun |
| 2.3 — the check for values a week apart, removed | 0 | none: no text reaches it |
| 2.3 — a span from one weekday to another is moved a week too (point 4) | 1 | the test of spans that begin today |
| 2.3 — a span of times on the day is not moved | 1 | `wed 6pm-8pm` at 23:30 |
| 2.3 — two weeks on, not one | 15 | as the first of 2.3 |
| 2.4 — a time apart is never read on the day | 72 | the table of times apart; the rule, apart and together; the two readers; the end of day; parts of the day |
| 2.4 — a time apart is read today as well, and the day keeps its 9 AM | 72 | the same |
| 2.4 — a further time beside a day that has its own is read, today | 13 | the table of further times; a further time in another zone; ends beside a part of the day that is itself a deadline; the two readers; two times offered as a choice |
| 2.4 — a phrase that can mean today keeps its other days | 6 | the rule: `jul 15 (9am)` and the like, typed on the day; deadlines on today's date |
| 2.4 — next week's weekday is dropped where the phrase can mean today | 6 | the rule: `wed (9am)` and the like, typed on the day; `by wednesday` on the Wednesday |
| 2.4 — a range under way names a day | 7 | the table of ranges under way; ranges on their first day; `this week (6pm)` from both readers |
| 2.4 — a range of days that began before today is not seen as under way | 6 | the ranges of days in those tests |
| 2.4 — a range of times under way is not seen as one | 1 | `wednesday night (9pm)` at 10 PM |
| 2.4 — a range of times that has ended still counts as under way | 7 | parts of the day with a time or an end beside them, on days whose part of the day has ended |
| 2.4 — a range of times that has not begun counts as under way | 12 | parts of the day with a time or an end beside them |
| 2.4 — a range under way with its time counts as a day with its time | 4 | the table of ranges under way |
| 2.4 — a range of days that names no day loses its first day at 9 AM | 3 | `mon to sat` and `fri-sun` on a day of the range, refused as past and not as not understood |
| Q6 — "end of day" keeps today for its day | 6 | the table for the end of day |
| Point 1 — a range of days that begins today is not under way | 1 | ranges on their first day |
| Point 1 — a phrase that names no day offers its first day at 9 AM beside a time too | 1 | a time beside such a phrase before 9 AM |
| Point 6 — a whole period names a day | 6 | the table of whole periods |
| Point 6 — a week counts as a whole period | 3 | `next week (6pm)`, from one reader and from both |
| Point 6 — a whole period is one longer than two weeks | 1 | `next 2 weeks (6pm)` |
| Point 6 — a span the text spells out counts as a whole period | 1 | `jul 20 to jul 31 (6pm)` |
| Point 3 — a part of the day never takes the time beside it | 15 | the table of parts of the day; three pairs in the rule, apart and together; an end beside a part with a time |
| Point 3 — a part of the day with no time beside it is a day at 9 AM | 17 | the tables of parts of the day with no time and with an end; `by friday night`; `sat night` and `sun night` |
| Point 3 — an end read beside a part of the day | 2 | an end beside a part of the day that has begun; an end beside a part on another day |
| Point 3 — an end read beside a time of its own | 2 | `friday (9pm) till 2am`, `friday, by 6pm (party 8pm)` |
| Point 3 — an end never the time | 11 | deadline times alone (`by 6pm`, `by end of day`); `friday, by 6pm` |
| Point 3 — a part of the day that has begun does not keep an end from being read | 1 | an end beside a part of the day that has begun |
| Point 3 — a part of the day that is itself the deadline keeps its end from being read | 1 | `by night` |
| Point 3 — an end beside a part of the day that keeps its start read on its own | 3 | the deadline parts of the day with an end beside them, begun or not |
| Point 3 — an end read as a time like any other | 14 | the three tables of ends |
| Point 3 — an afternoon is not a part of the day | 1 | `friday afternoon (3pm)` |
| Point 3 — a part of the day not told apart | 18 | the table of parts of the day; three pairs in the rule; ends beside parts; a deadline at night on today's date |
| As before — a bare day is 8 AM, not 9 AM | 43 | every table with a bare day |
| As before — a time alone is today's only, never tomorrow's | 7 | `9am`, `by 9am`, ranges late in the day, a time beside a phrase that names no day, and the older test for a time that has passed |

**Step 4.3 — the comparison of section 3 again, through the parser as built.** `master`'s parser,
copied under another name, and the parser on the branch read the same texts side by side in a
scratch harness, at the nine moments of section 3. The texts are those of section 3 with the
texts of the tests as built added: 25,707 texts, 231,363 cases. These are the numbers of the code
after every read of the reviews, against the parser of `master` at `162fd42`.

- **One phrase to the recognizer, and not a repeat: 2,291 texts. None that `master` reads is read
  differently, and none is refused.** 1,046 go from not understood to read, the deadlines of 2.1;
  218 from not understood to refused as past, deadlines whose date has gone by, and deadlines on
  today's date once their time is behind (point 5 above); 454 from refused as past to read, the
  weekday on its own day (2.3); and 786 answer the same at every moment. 213 of them are in two of
  those groups, at different moments.
- **A repeat among the phrases: 8,682 texts**, every one refused in the words of 2.2 by both
  readers. `master` reads 6,809 of them at some moment, as one date with the repeat dropped.
- **Several phrases and no repeat: 14,708 texts**, 2,370 of them with a length of time. Of the
  12,338 others, 137 answer the same at every moment; the rest change as 2.1, 2.3 and 2.4 and the
  points above mean them to.
- 26 texts in which the recognizer finds nothing are refused as before.
- The day reader answers the same for every text but the 8,682 repeats.

The same comparison was run after each read of the reviews. Against the run before the first,
590 texts changed in how they change: 453 begin with `after`, whose range of days begins on the
day it names, so on that day a time beside it stays a time alone (point 1); 107 have a further
time beside a part of the day, which the part now takes as its time (point 3); and of the other
30, 17 are deadlines on a date that is today at one of the moments (point 5), eight are parts of
the day with a time beside them (point 3), four are ranges on their first day (point 1), and one
is a whole period, `july (6pm)` (point 6). Against the run after the first read, 178 more changed:
165 begin with `after`, a phrase that names no single day, so the time beside it is now meant
before 9 AM too (point 1); 12 are deadlines with a part of the day on a date that is today at one
of the moments (point 5); and one is `this evening until 9pm`, whose end the evening no longer
takes as its time (point 3). The generated texts have no end beside a part of the day; of the
texts from the tests, `this evening until 9pm` and `tonight, ends 11pm` have one, and point 3's
tests hold the rest. Against the run after the second read, 465 more changed, every one an end
beside a time of its own (`by 6pm, ends 9pm`, `by 6pm or 8pm`): each now reads that time, as
`master` does where no day is named. That is the cost point 3 names. Run once more on the final
code, with the line the last look restored, it gave the same, text by text. The run made before the
merge of pull request #184, on the code before the reviews, was the same text by text against that
`master`'s parser as against the one before it.

**Step 4.4 — the two reviews.** Two agents read the branch before any push: one for correctness,
and one for security and what the public text says. A first attempt at both stopped before either
reported; both were started again from the beginning on the branch with `master` at `723696d`.

The review of correctness ran the change: the parser's tests in a scratch copy, the comparison
harness over about 400 texts at about 30 moments in six zones, nine mutations of its own, and the
short and full weekday names over 138 pairs at 21 moments in three zones. It found the merge with
pull request #184 right and both of the task's constraints holding, and:

| Finding | Answer |
|---|---|
| C1, must fix — a part of the day with a time written beside it lost the time: `tonight (7pm)` was today at 8 PM, where `master` reads 7 PM, and `friday night (9pm)` Friday at 8 PM, where the same two written together are 9 PM. | Point 3 above. A table of nine and one of four, and three pairs in the test that a day and a time written apart read as the two written together. |
| C2, should fix — a range of days was not under way on its first day: `this weekend 6pm`, typed on the Saturday after 6 PM, was refused, where `master` reads Sunday at 6 PM. | Point 1 above. Three cases in the test of ranges. |
| C3, should fix — a span from one weekday to another, written with times (`fri 6pm to sun 6pm`), was moved a week on its first day. | Point 4 above. Two cases in the test of spans. |
| C4, should fix — a deadline on today's date (`by jul 15` on July 15) became that date of next year. | Point 5 above. Two rows in the test of deadlines that have passed, and a sentence in the test of the word in front. |
| C5, needs a decision — `by friday midnight` is Friday at 00:00, the night before; `by friday at 5` is Friday at 5 AM. | Not changed: each reads as the same text without the word in front, and `friday midnight` is Friday at 00:00 on `master`. Listed at the end of this section. |
| C6, should fix — a time beside words that are not a date moved to the day the recognizer found in them: `6pm (may change)` to May 1, 2027; `6pm, table 1999` refused as past. | Point 6 above, for months, seasons and years: a test of five rows. `8pm (week 2)` and `6pm in the sun` still move; they are listed at the end of this section. |
| C7, nit — four things no test held: a phrase that is under way only in part, `on or before`, the place of the length check, and what the comment of the test of `fri 17` claims. | A row for each of the first two. The length test is renamed to say what it shows. The comment is corrected, and so is this section's sentence on the same point. |
| C8, nit — words that the recognizer reads as a repeat, though they describe (`friday 6pm (annual party)`), refuse the text with the hint. | Not changed: by Q2 A a repeat is refused wherever it stands. Listed at the end of this section. |
| C9, nit — comments made untrue by C2 and C3, a summary that left out a case, and a comparison that did nothing (`<= today` where only today could be). | Corrected; `DaysForATime` became `TodayWhereNamed`, which keeps today and next week's weekday only. |

The review of security and of what the public text says checked that a missing text and the
length are still checked first in both readers, that the recognizer is never given more than the
limit and is asked once by each reader, that the new refusals quote what the existing ones quote
and are shown as those are, and that no caller's own check is passed by text it refused before.
It recounted every number in this section that comes from the runs of steps 4.1 to 4.3 and from
the tests. It found:

| Finding | Answer |
|---|---|
| S1, must fix — 0.37.1 was released while the branch was being reviewed, without this change, so its summary belongs in `0.37.2.md`. | Moved; `0.37.1.md` is as released. |
| S2, must fix — the summary said `by end of day friday` was not understood before; it was Friday at 9 AM. | Taken out of the summary. |
| S3, should fix — point 2 above said `by end of day` was today's "as before"; it was not understood. | Corrected. |
| S4, should fix — the state file said the two points beyond the plan keep text on the day it had; Q6 moves `eod friday`. | Corrected. |
| S5, should fix — the summary's bullet on repeats did not say that text read before is now refused. | Reworded, with `friday 8pm every week`. |
| S6, should fix — the live script's item for `fri (6pm)` gave the wrong answer of a build without the fix on a Thursday evening. | Reworded: before 6 PM, on any day but a Friday. |
| S7, should fix — statements made stale by the merge of pull request #184: the count of the parser's tests, the length check as the first, and the commit the change is built on. | Brought up to date. |
| S8, should fix — the state file and this plan's status line spoke of the reviews in the past while they ran. | Reworded. |
| S9, nits — `DaysForATime` was listed as a point beyond the plan, where it is case 1 of 2.4; the count of rows in 2.8's table; a missing word in the note at the end of the plan for `sat` and `sun`; "before, they came back as today" in the summary; the comments of `RepeatError` and `ReadValue`, and the class summary. | Each corrected. |
| S10, nits left as they are — the refusal of a repeat points to the repeat option where an option has none (`/remind`, `/timestamp`); step 1 of the how-to is refused on a Friday evening and on a Saturday before 6 PM, when the close it names comes after the start; the audit log quotes a remark of Joshua's about the session. | The refusal is the wording approved in 2.2, which says "to make an event repeat"; a close after the start is refused with any wording; and the audit log keeps every input of Joshua's word for word. |

Both agents then read the answers.

The review of correctness confirmed C1 to C9 at about 40 moments in four zones, the merge with pull
request #184, and `sat` and `sun` reading as their full names over 138 pairs at 21 moments in
three zones, and found three more:

| Finding | Answer |
|---|---|
| C10, should fix — C1 made an end beside a part of the day its time: `friday night till 2am` became Friday at 2 AM, before the night begins, where `master` reads 8 PM, and `tonight until 2am` was refused. | Point 3 above: an end ends a part of the day and is not its time. A table of six, each as before. |
| C11, should fix — beside a time, a phrase that names no day still offered its first day at 9 AM, and the earlier of the two won: `this weekend 6pm`, typed on the Saturday at 00:30, was 9 AM, as on `master`, where point 1 says the time is meant. | Point 1 above: the first day is offered only where the text has no time. A test at three moments. |
| C12, should fix — C4 did not reach a deadline written with a part of the day: `by jul 15 evening`, typed that evening, was July 15 of next year. | Point 5 above. A test, with `jul 15` alone beside it, which stays next year's. |
| C13, nits — no test held an afternoon with a time, a period of between eight and fourteen days, or a date alone on its own day; `next 8 days (6pm)` names tomorrow; and a time after midnight beside a night is read on the night's own date. | Rows for the first three. The other two are listed at the end of this section. |

The review of security and of what the public text says confirmed S1 to S9 and the code: the
checks come first, and each reader asks the recognizer once. It found four points of counting and
wording in this section, each corrected.

Both agents read these answers last. The review of security and of what the public text says
confirmed the code again, and the review of correctness confirmed that the answers to its second
read do what it had asked, each held by a test. Between them they found:

| Finding | Answer |
|---|---|
| S11, should fix — an end beside a part of the day that has begun was read on its own: `tonight until 2am`, typed at 9 PM, was 2 AM, where `master` refuses it as past. | One rule for ends, with C14 and C15: an end is the time only where the text has no other time and no part of the day (point 3). A test at three moments. |
| C14, should fix — an end beside a part of the day or a day that has its own time was read on that day as if it were a start: `friday night (9pm) till 2am` and `friday (9pm) till 2am` were Friday at 2 AM. | The same rule. A table of six, with its one cost, `friday, by 6pm (party 8pm)`, which is Friday at 8 PM. |
| C15, should fix — an end beside a part of the day was read on another day the text names: `friday or saturday night until midnight` was Friday at 00:00. | The same rule: it is Friday at 9 AM, as before. |
| C16, should fix — a range that names no day still offers its first day at 9 AM beside a day inside it that has its own time: `this week, friday 6pm`, typed on the Monday before 9 AM, is Monday at 9 AM. | Not changed: it reads as on `master`, and a fix would weigh a range against the days the other phrases name, a rule of its own. Listed at the end of this section. |
| S12, should fix — the account of the second comparison under step 4.3 had its breakdown wrong. | Corrected: 165, 12 and one. |
| S13, should fix — wording in this section that drew attention where it need not. | Reworded. |

The comparison of step 4.3, run on the rule for ends, found one more in that rule itself: `by
night`, `until night` and `before night` were not understood, as on `master`, because the
deadline was itself a part of the day and the rule took it for one beside an end. A part of the
day that is itself the deadline does not count: `by night` is tonight at 8 PM, as `night` is. A
row in the table of deadlines holds it.

Both agents then took a last look at the commits as rebuilt. The review of security and of what
the public text says confirmed them, with one nit of wording in the audit log, corrected. The
review of correctness confirmed the rule for ends everywhere but one shape, which the rule itself
had broken: an end beside a part of the day that is itself a deadline (`by friday night (until
2am)`) was read on its own, today or tomorrow, where the round before had read Friday at 8 PM. The
line that keeps such an end from being read, which the rule had taken out, is back, as the review
tried it, with three tests. It also noted that an end beside a whole period is read as a time alone
(`next month, by 6pm` is today at 6 PM), as `6pm (may change)` is; that is listed at the end of
this section.

**`master` taken in five more times.** `master` moved while the branch was being reviewed and
after the completion gate, and was taken in each time: this branch's audit entries by their
timestamps, and its section of the state file after the others.

- Pull requests #183, #182 and #186 (`916ecd1`). None of them touches the parser or its tests. On
  that tree the Release build had no error and the same 22 warnings, and 1,959 tests were green:
  1,043 API, the 14 more being those of #182; 418 bot; 153 web; and 345 for the tool, with the
  same 17 skipped.
- Pull request #184 (`723696d`), which changes the same file in other lines, near the start of
  both readers. The two changes met only in the comments that sum up the two methods, and both
  are kept. This change still begins below #184's lines in both readers. On that tree the Release
  build had no error and the same 22 warnings, and 1,997 tests were green: 1,081 API, the 38 more
  being those of #184; 418 bot; 153 web; and 345 for the tool. The rule removals of step 4.2 and
  the comparison of step 4.3, run again there against the parser's 392 tests and that `master`'s
  parser, gave what they had given before the merge, change by change and text by text.
- The release of 0.37.1 (`eb6afb8`), and pull requests #188 and #187 (`162fd42`). The release went
  out without this change, so its summary is in `docs/release-notes/0.37.2.md`, the next version
  after 0.37.1 for a `fix:`, and `0.37.1.md` is as released. The parser and its tests are as they
  were.
- Pull requests #191 and #194 (`47209ab`), merged after the completion gate was answered and
  before the push. Both change only records under `aidlc-docs`, which no test reads, so the
  build and the suite were not run again. The parser and its tests are as they were.
- Pull requests #198, #193 and #195 (`5fc9239`), merged after the push; the app that Joshua set to
  fix the pull request reported the conflict. Only the records met, and none of the three
  touches the parser or its tests. On that tree the Release build had no error and the same 22
  warnings, and 2,297 tests were green: 1,377 API, 418 bot, 153 web and 349 for the tool, with
  the same 17 skipped.

**Noticed while probing, not part of this.** Each is listed for Joshua to take up or leave:

- `2 hours before friday 6pm` and `30 minutes before 6pm` could be read as Friday 4 PM and
  5:30 PM. That is a sum, not a reading, and would be a feature of its own. They stay refused.
  The mirror image is read, as it was before: `2 hours after friday 6pm` is Friday at 6 PM, with
  the length lost, because `after …` is a range with a start and has always been read.
- `by the weekend`, `by end of month`, `before next month`: deadlines at the edge of a period
  (2.1). They stay refused.
- `by friday midnight`, `friday by midnight` and `by midnight friday` are Friday at 00:00, the
  start of the day, as `friday midnight` is: the recognizer reads midnight as the start of the day
  it names. As a deadline that is a day early. `by friday at 5` is Friday at 5 AM, as
  `friday at 5` is.
- Words that the recognizer reads as a repeat refuse the text even where they describe:
  `friday 6pm (annual party)`, `friday 6pm, every minute counts` (Q2).
- The refusal of a repeat from the date-and-time reader says "to make an event repeat, use its
  repeat option" wherever a date and time is typed, `/remind` and `/timestamp` among them, which
  have no repeat option.
- `8pm (week 2)` is refused as past: the recognizer reads `week 2` as the second week of the year,
  a week names its first day, and that has passed. It was today at 8 PM.
- `6pm in the sun` is Sunday at 6 PM: `sun` is read as Sunday, by the fix for `sat` and `sun`, and
  the time is read on that day. It was today at 6 PM.
- `next 8 days (6pm)` and `next 30 days (6pm)` are tomorrow at 6 PM, where they were today: the
  recognizer writes "next N days" as it writes a span the text spells out, which names its first
  day. `next 2 weeks (6pm)` is a whole period, and today's.
- A time after midnight beside a night is read on the night's own date, before the night begins:
  `friday night (1am)` is Friday at 1 AM, and `tonight (1am)` is refused as past. It was wrong
  before too, and the same two written together are no better: `friday night at 1am` is Friday at
  1 PM.
- A range that names no day offers its first day at 9 AM beside a day inside it that has its own
  time, and before 9 AM on that first day it is the earlier: `this week, friday 6pm`, typed on the
  Monday at 8 AM, is Monday at 9 AM, as on `master`; so is `this weekend, saturday 2pm`, typed on
  the Saturday at 00:30, and `this weekend (sat 2pm)`, typed on the Wednesday, is Saturday at
  9 AM.
- An end beside a whole period is read as a time alone, as any time beside one is (point 6):
  `next month, by 6pm` and `may, until 6pm` are today at 6 PM, where they were the period's first
  day at 9 AM.
- An end written as a plain time is read as a time: `friday (9pm), ends 2am` is Friday at 2 AM,
  the earlier of the two on that day. Only an end written with `by`, `until` or `before` is known
  to be an end.
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
  A deadline on a date that has passed this year is next year's too, as the date alone is:
  `by jul 14`, typed on July 18, is July 14, 2027. Only a deadline on today's date is kept to
  today (point 5).
- `nov 12 noon` is November 12 at 9 AM, and so is `by nov 12 noon`: the recognizer does not read
  `noon` after a date written that way.
- `wednesday morning (9am)`, typed on the Wednesday at 10:00, while the morning is under way, is
  Thursday at 9 AM, as before.
- `tuesday, next week` gives Monday, the earlier of the two days named; the free-slots plan
  records it.
- `the 15th` and `15th` alone are not understood; `on the 15th at 6pm` is read.
