# Short weekend days in date text (`sat`, `sun`) — functional note and code-generation plan

**Task**: make `sat` and `sun` read as Saturday and Sunday wherever CalCrony reads a date typed in
words. Handed over on 2026-11-15 from the session that built free slots, whose plan recorded the
gap as known (`free-slots-code-generation-plan.md`, section 4.1).
**Baseline**: `master` 56b61ab — the code of v0.37.0, which the hosted instance runs; the records
give 1,292 tests green at that release, re-run as step 0.1.
**Branch**: `fix/short-weekend-days` → PR `fix: read sat and sun as Saturday and Sunday in date text`.
**Status**: written 2026-11-15; approved by Joshua on 2026-11-16 as written, with Q1 answered A
(section 4). Generated and reviewed the same day (section 8), with the review's answers read
again by both reviewers, and approved at the completion gate that day ("Continue to next
stage"). Delivery is section 7.

A small fix, so the workflow runs at minimal depth: the task is the requirement; no user stories,
application design, units, NFR or infrastructure stages; one unit, and this file is both its
functional note (sections 1–4) and its code-generation plan (sections 5–7), approved together.

## 1. What happens today

Every date typed in words goes through `NaturalDateTimeParser` — `TryResolve` for a date and time
(event start, repeat-until, RSVP close, poll close and time-poll options, reminders, `/timestamp`,
the web's date fields) and `TryResolveDay` for a day (`/availability slots when:`). Both hand the
text to `Microsoft.Recognizers.Text.DateTime` 1.8.13, the newest version published.

Run against the real parser — a scratch harness with a project reference to `src/CalCrony.Api`,
not committed — on Wednesday 2026-07-15 at 10:00 in America/Chicago, the parser tests' fixture:

| Text | Date and time (`TryResolve`) | Day (`TryResolveDay`) |
|---|---|---|
| `sat`, `sun` | refused: not understood | refused: not understood |
| `sat 6pm`, `6pm sat` | **today** 6 PM | **today** |
| `sun at noon` | **today** noon | **today** |
| `sat morning`, `sat night` | tomorrow 8 AM, today 8 PM | today |
| `sat or sun`, `sat-sun`, `a week from sat` | refused: not understood | refused: not understood |
| `on sat morning`, `on sun night` | refused: not understood | refused: not understood |
| `next sat`, `this sun`, `on sat`, `on sat 6pm`, `noon on sun`, `sat jul 18`, `sat next week` | right already | right already |
| `last sat` | refused: past | refused: already passed |
| `mon`, `tue`, `thu`, `fri`, `tues`, `thur`, `thurs` | that weekday, 9 AM | that weekday |
| `wed`, `weds` (typed on a Wednesday) | refused: past — 9 AM today is behind | today |
| `mon 6pm`, `fri at noon`, `6pm fri` | that weekday at that time | that weekday |
| `saturday`, `sunday`, `saturday 6pm`, `sunday at noon` | right | right |

The cause is in the recognizer's own patterns, read out of the package. Inside a longer form
(`next …`, `this …`, a date beside it) a weekday may be `sat` or `sun`. A weekday standing alone
may be `mon`, `tue(s)`, `wed(s)`, `thu(r)(s)` or `fri`, but `sat` and `sun` count only directly
after the word `on` — `((?<=on\s+)(sat|sun))` — because in ordinary prose they are also words.
So the short weekend days are read in some places and dropped in others, and where one is dropped
beside a time the rest is still read: `sat 6pm` becomes an event today.

The recognizer has no setting for this. Its patterns are compiled into the package, and replacing
one means rebuilding its English configuration classes by hand.

## 2. The change

Before the text reaches the recognizer, `sat` and `sun` standing as words of their own are spelled
out as `saturday` and `sunday`. Nothing else about the text changes, and text without them reaches
the recognizer exactly as it does today.

1. **One place.** A private helper on the parser, called at the one line in each method that hands
   text to the recognizer. The length check stays the first thing both methods do.
2. **A word of its own.** The pattern is `sat` or `sun` between word boundaries, in any letter
   case — the boundary the recognizer itself uses for weekdays. `saturday`, `sunday`, `sunrise`,
   `sunny`, `satisfy`, `sats` and `sat6pm` are not touched. (`mon6pm` is not read today either.)
3. **The same answer as the full name.** After the change `sat …` is read exactly as
   `saturday …` is read today, wherever it stands. That is the whole rule, and it is how `mon`
   to `fri` behave already: each reads exactly as its full name.
4. **Errors quote what was typed.** `"last sat" is a day that has already passed.`, not
   `"last saturday" …`: only the recognizer is given the spelled-out text.
5. **The recognizer still never reads more than 100 characters.** Spelling out lengthens the text
   by five characters for `sat` and three for `sun`. When the result would be longer than
   `DateTextLimits.MaxLength`, the text is read as typed, as it is today. `sat 6pm` reaches that
   only with 89 more characters after it.

What it changes, measured on the same harness:

| Text | Today | After |
|---|---|---|
| `sat`, `sun`, `Sat`, `SUN`, `sat.` | not understood | Sat Jul 18 / Sun Jul 19 (9 AM for a date and time) |
| `sat 6pm`, `sat at 6pm`, `sat, 6pm`, `6pm sat`, `sat 18:00`, `sat 6pm CST` | today 6 PM | Sat Jul 18, 6 PM |
| `sun at noon` | today noon | Sun Jul 19, noon |
| `sat morning`, `sat night` | tomorrow 8 AM, today 8 PM | Sat Jul 18, 8 AM / 8 PM |
| `sat or sun`, `sat and sun`, `sat to sun`, `sat-sun`, `sat/sun` | not understood | Sat Jul 18 |
| `sat 6pm or sun 2pm` | today 2 PM | Sat Jul 18, 6 PM |
| `a week from sat`, `2 weeks from sat` | not understood | Sat Jul 25, Sat Aug 1 |
| `on sat morning`, `on sun night` | not understood | Sat Jul 18, 8 AM; Sun Jul 19, 8 PM |
| `next sat`, `this sun`, `last sat`, `on sat`, `on sat 6pm`, `sat jul 18`, `sat next week` | as in section 1 | the same |

**Checked for side effects.** 2,640 generated phrases — thirty openings, `sat` or `sun`, forty-four
endings — were each read as typed and spelled out, at 42 moments across fourteen days. In 1,327 of
them the recognizer already reads the short form; spelling it out changed the answer in 14: ten of
the `on sat morning` kind, refused today and read afterwards, and four that are not dates anyone
would type (`2 weeks from sat in 2 weeks`), which the recognizer splits differently. In the rest
the short form is dropped today, and the day now counts.

*Corrected on 2026-11-16, after the review of the built change, with the paragraph above left as
approved: the day does not count in all of the rest. Where the full name is itself refused, the
short form is now refused with it, where the rest of the text was read before — `by sat 6pm`,
`every sat 8pm`. "What stops being read" in section 8 has the account and the counts.*

**Other ways considered.**

| Way | Why not |
|---|---|
| A newer recognizer | 1.8.13 is the newest. |
| Change the recognizer's pattern | Not configurable; it means copying and maintaining its English configuration. |
| Spell out only when nothing was understood | `sat` would work, but `sat 6pm` would stay today at 6 PM — the worse half of the gap. |
| Put `on` in front of the short form | It lengthens the text as well, and breaks forms that are right today (`next on sat`). |
| Read the short form only when the next word is one the recognizer reads | With `saturday` in place the recognizer leaves `or`, `and`, `/`, `from`, `late`, `early` and `eve` unread beside it, so `sat or sun`, `sat from 6pm` and `sat late` would stay as they are today. |

## 3. Where the task's two conditions pull apart

The task asks for `sat` and `sun` to behave as `mon` to `fri` do, and for `sat down` not to be
caught. Both cannot hold, because the recognizer ignores words it does not read:

| Text | Today |
|---|---|
| `mon down`, `the mon`, `fri-nav` | Monday, Monday, Friday |
| `saturday down`, `the sunday` | Saturday, Sunday |
| `mon down at 6pm` | date and time: today 6 PM (two separate readings, the earlier wins); day: Monday |
| `sat down`, `sun up`, `the sun`, `sat-nav` | not understood |
| `sat down at 6pm` | today 6 PM; day: today |

Under the rule of section 2, `sat down` reads as `saturday down` does: Saturday. `sat down at 6pm`
keeps today 6 PM as a date and time, and its day becomes Saturday. And a text in which `sun` or
`sat` is an ordinary word beside a real date can change: `friday at sun down`, typed on a Saturday,
would read as tomorrow, Sunday, where it reads as next Friday today.

Keeping those unread takes a rule about the neighbouring word, and the recognizer cannot supply
one (last row of the table above), so it would be a list of words that may follow a weekday, kept
by hand: `at`, `from`, `after`, `around`, `or`, `and`, `to`, the day parts, `next`, `this`, `in`,
`of`, month and weekday names, zone abbreviations. Whatever the list misses keeps today's reading —
for `sat <a word not listed> 6pm`, silently today at 6 PM — and `sat` stops reading exactly as
`saturday` does. It still could not tell `we sat friday` or `the sun` apart from a date.

## 4. Question

Q1 — How far should the short forms reach?

- A) Every `sat` or `sun` standing as a word of its own reads as `saturday` or `sunday`
  (section 2). `sat down` reads as Saturday, as `mon down` reads as Monday today.
  **(Recommended. These are date options: the mistake that costs something there is dropping the
  day without saying so — `sat 6pm` as an event today — not reading a day into `sat down`, which
  nobody types into one. It is exactly how `mon` to `fri` behave, it is one rule with no list to
  keep, and the short and the full name cannot drift apart.)**
- B) As A, but a short form followed by a plain word that is not on a fixed list of date words is
  left as typed. `sat down`, `sun up` and `sat-nav` stay unread. The price is in section 3: a
  list to keep, phrases the list misses keep today's reading, and about forty more lines with a
  second table of tests. I would show the list before building it.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve with A".

## 5. What is added, file by file

| File | Change |
|---|---|
| `src/CalCrony.Api/Services/NaturalDateTimeParser.cs` | A source-generated pattern beside the zone-abbreviation one; a private static helper that spells the short forms out and returns the text as typed when the result would pass the limit; the helper's result passed to `model.Parse` in `TryResolve` and in `TryResolveDay`; one sentence in the class summary. |
| `tests/CalCrony.Api.Tests/NaturalDateTimeParserTests.cs` | The tables of step 2. The `sat` row leaves `Day_resolution_refuses_what_it_cannot_read`, with its comment. |
| `aidlc-docs/construction/plans/free-slots-code-generation-plan.md` | Section 4.1: the `sat` row of the table and the "Known and left as they are" paragraph, amended in place with the date, as that plan records its other later changes. |
| `aidlc-docs/construction/build-and-test/live-e2e-script.md` | Two items: `when:"sat 6pm"` on `/create`, and `when:sat` on `/availability slots`. |
| `aidlc-docs/aidlc-state.md`, `aidlc-docs/audit.md` | The records. |

Not changed: Contracts, the bot, the web app, the database, every route's shape. No documentation
example becomes wrong — the README, the Docs page and the option descriptions show `tomorrow 6pm`,
`in 5 hours`, `saturday` and `nov 12`, all read as before — so none is edited.

## 6. Execution plan

A box is ticked in the turn its step completes.

**0. Baseline**
- [x] 0.1 On `fix/short-weekend-days`, before the first change: `dotnet build CalCrony.slnx -c Release`
      and `dotnet test CalCrony.slnx -c Release` green; counts recorded. *(Run 2026-11-15 on the
      branch with only this plan committed, so on the code of `master` 56b61ab: 1,292 tests
      green — 721 API, 418 bot, 153 web; the build has no error and 22 warnings, the known
      baseline.)*

**1. Parser**
- [x] 1.1 The pattern, the helper and the two call sites (section 2, rules 1–5); the class summary.

**2. Tests** — in `NaturalDateTimeParserTests`, at the fixture's Wednesday unless a row says otherwise
- [x] 2.1 Date and time, one table: `sat`, `sun`, `Sat`, `SUN`, `sat.`, `sat 6pm`, `sun at noon`,
      `6pm sat`, `sat, 6pm`, `sat morning`, `sat or sun`, `a week from sat`, `on sat morning`, and
      `sat 6pm CST` for a caller whose zone is UTC — each with the instant it must give.
- [x] 2.2 Day, one table: `sat`, `sun`, `sat 6pm`, `sun at noon`, `6pm sat`, `sat (6pm)`,
      `sat or sun`, `on sun night` — each with its day.
- [x] 2.3 Forms that were right before stay as they were: `next sat`, `this sun`, `on sat`,
      `on sat 6pm`, `noon on sun`, `sat jul 18`, `sat next week`; `last sat` still refused as passed.
- [x] 2.4 The other short weekdays, pinned for comparison: `mon`, `tue`, `tues`, `wed`, `weds`,
      `thu`, `thur`, `thurs`, `fri` as days; `mon 6pm` and `fri at noon` as a date and time.
- [x] 2.5 The rule itself: for a table of phrases, the short form and the full name give the same
      answer from both readers — the same instant or day, or the same kind of refusal — at four
      moments: the fixture's Wednesday, a Saturday at 10:00 and at 23:30, a Sunday at 10:00.
- [x] 2.6 Longer words are left alone: `sunrise`, `sunny`, `sundown`, `satisfy`, `sats`, `suns`,
      `sat6pm` are refused as not understood by both readers, as before.
- [x] 2.7 The rows Q1 decides: under A, `sat down` reads as Saturday, as `mon down` reads as
      Monday; under B, the list's own table.
- [x] 2.8 On the day itself, a Saturday at 10:00: `sat` is today for the day reader, `sat 6pm` is
      today at 6 PM, and the refusal of bare `sat` as a date and time quotes `sat` as typed.
- [x] 2.9 The limit: `sat 6pm` filled out with dots to 95 characters is read as Saturday at 6 PM
      (spelled out it is exactly 100); filled to 96 it is read as typed; and a short form in text
      of 101 characters is refused with the limit's sentence, by both readers.

**3. Records in the repository**
- [x] 3.1 `free-slots-code-generation-plan.md`, section 4.1, as in section 5.
- [x] 3.2 `live-e2e-script.md`: the two items.

**4. Verification**
- [x] 4.1 `dotnet build CalCrony.slnx -c Release` with no new warning; `dotnet test CalCrony.slnx
      -c Release` green; counts recorded against 0.1.
- [x] 4.2 Each rule taken out in a scratch copy fails a test meant to pin it: the call in
      `TryResolve`; the call in `TryResolveDay`; the word boundaries; the letter case; the two
      names swapped; the limit on the spelled-out text.
- [x] 4.3 The comparison of section 2 run again through the changed parser: every generated phrase
      with a short form answers as the same phrase with the full name.
- [x] 4.4 Two review agents read the local commit before it is pushed, one for correctness and one
      for security and what the public text says, as on PR #173. Their findings are answered and
      recorded in section 8.
- [x] 4.5 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 7. Delivery

Approving this plan covers the changes above and committing them locally. Everything that leaves
this machine waits for its own go-ahead.

- [x] 7.1 *Go-ahead:* push the branch and open the PR, after taking `master` in if it has moved
      (the audit entries then go in by timestamp). *(2026-11-16, on the go-ahead at the completion
      gate: pushed at 16:51Z, and pull request #180 opened at 16:52:08Z. `master` moved between
      the two, when pull request #179 merged at 16:51:53Z, so it was taken in straight after:
      the audit entries in the order of their stamps, and both new sections of the state file
      kept.)*
- [ ] 7.2 Checks green; a review if one starts. *Go-ahead:* squash merge. The test stack then takes
      `:main`.
- [ ] 7.3 *Go-ahead:* release 0.37.1 — the release PR that release-please opens — with a short
      summary and a two-line how-to for the notes, shown to Joshua first. *(Since pull request
      #179 the summary is a file in the pull request that makes the change:
      `docs/release-notes/0.37.1.md` was added to #180 with the merge of `master`, and its text
      is put to Joshua before the merge.)*
- [ ] 7.4 *Go-ahead:* prod rollout, by the usual steps: backup, the pinned tag, app services only.
      No migration.

## 8. Notes from generation

Generated on 2026-11-16, after Joshua's approval with Q1 answered A, and reviewed the same day by
two agents before any push (step 4.4, below). Their findings changed the tests, one option on
the pattern and this section; the numbers here are those of the code after the answers.

**As built.** One file under `src` changed, `NaturalDateTimeParser.cs`: the pattern
`ShortWeekendDayRegex`, source-generated beside the zone-abbreviation one; the private helper
`SpellOutWeekendDays`; its result handed to the recognizer at the one `model.Parse` line in
`TryResolve` and in `TryResolveDay`; and one sentence in the class summary. The length check is
still the first thing both readers do. In `TryResolve` the zone abbreviation is still taken out
first, and the text is spelled out after that.

**Where it differs from the plan as approved.** Nothing in the rule. In the tests:

- More tests than the steps list. `sun or sat` is in both tables of 2.1 and 2.2: every short form
  in a text is spelled out, not the first alone. `saturn` is in 2.6. 2.7 has `sun up`, `the sun`
  and `sat-nav` beside `sat down`, with `mon down` for comparison, from both readers. 2.8 also has
  a time already behind on the day itself (`sat 9am` at 10:00 on the Saturday).
- Beside 2.9 there is a table of two, `landsat` and `sunrise`, for the rule that only a word of
  its own is spelled out. The longer words of 2.6 are refused whether or not the letters in them
  are spelled out, because the recognizer reads neither `sundayrise` nor `saturdays`, so 2.6
  shows only that they are still refused. The boundary behind the short form also shows in the
  full names, which stop being read without it. The boundary in front shows from outside only at
  the limit, and that is what the table of two is for.
- Also beside 2.9, after the review: text with two short forms is spelled out in full or not at
  all (`sat or sun` at 92 and at 93 characters).
- After the review, two tests for what stops being read (next paragraph): four phrases that are
  now not understood, as with the full name, and a range that ends on a short form.
- 2.5 runs 26 phrases, each with `sat` and with `sun`, at the four moments, and compares the whole
  refusal rather than its kind: the same sentence, with the text as typed in the quotation marks.
- `last sat` of 2.3 became the test for rule 4: both refusals are checked word for word.

**What stops being read — wider than section 2 says.** The short form now reads as the full name
does. Where the full name is itself refused, the short form is therefore refused with it, and
before it was not: the short form was dropped and the rest of the text was read. Section 2 says
of those phrases that "the day now counts"; that is so for most and not for these. The released
parser and the changed one, over the 2,640 phrases at the 42 moments:

- **96 phrases are not understood now, by either reader, at any moment.** All begin with `by`,
  `until` or `before`, 32 of each: `by sat 6pm`, `until sun 18:00`, `before sat at noon`. Before,
  the rest of each was read, so `by sat 6pm` was today at 6 PM. `by saturday 6pm` and `by fri 6pm`
  have always been refused: the first point of the list at the end of this section.
- **273 phrases are refused as past by the date-and-time reader at some moment where it answered
  before.**
  - 211 of them only on the Saturday or the Sunday that the phrase itself names. The plain case
    is a time already behind: on a Saturday, `sat 9am` at 10:00 and `sat 6pm` at 23:30, as
    `saturday 9am` and `saturday 6pm` are. Before, the day was dropped and the time rolled on to
    the next day, with nothing said.
  - The other 62 are all ranges that have begun: 37 only on the weekend (`fri-sun` on the
    Saturday) and 25 on a weekday as well (`mon to sat` from the Tuesday on, `sat to mon` on the
    Monday it ends).
    They are now read as ranges, as `monday to saturday` and `mon to fri` are: no date and time,
    because a range's start is its instant and that has passed, and today from the day reader.
    Before, the short form was dropped and the other end was read alone, so `mon to sat` on a
    Wednesday was next Monday.
- **369 phrases in all** have a reader that answered before and refuses now at some moment. The
  day reader refuses none as passed that it read before; its 96 are those of the first point.
- Outside those phrases: `every sat 8pm` was today at 8 PM and is not understood now, as
  `every saturday 8pm` and `every fri 8pm` are.

In each of these a reading that was wrong and said nothing has become the refusal the full name
gets. It is set out here because the text was read before, and section 2 did not say so.

**At the limit**, where rule 5 is briefer than the code:

- Spelling out is all or nothing. `sat or sun` filled out to 92 characters is read with both
  spelled out, which is exactly 100; at 93 neither is, and the text is read as typed. The one
  that would still fit is not spelled out alone.
- Where a zone abbreviation is typed, the two readers measure different texts. `TryResolve` takes
  the abbreviation out first, as it always has, and measures what is left; `TryResolveDay`
  measures the whole text. So for typed text of 96 to 100 characters that carries an
  abbreviation, the first can read `sat` as Saturday where the second reads the text as typed.
  Each follows rule 5, no request gives one text to both, and neither hands the recognizer more
  than the limit. Rule 5's "89 more characters" is for text with no abbreviation.

**Rule 4, more exactly.** A refusal quotes what it quoted before the change: the text as typed,
and in `TryResolve` without the zone abbreviation that chose the zone, so `last sat CST` is
refused as `"last sat" resolves to a time in the past.` The spelled-out text is never quoted.

**Step 4.1.** `dotnet build CalCrony.slnx -c Release --no-incremental`: no error and 22 warnings,
the same 22 as at 0.1, none of them in the parser or its tests. `dotnet test CalCrony.slnx
-c Release`: 1,384 tests green — 813 API, 418 bot, 153 web; 1,292 at 0.1. The 92 more are all in
`NaturalDateTimeParserTests`: 93 new cases, and the `sat` row that left
`Day_resolution_refuses_what_it_cannot_read`. Before the review's answers the same two commands
gave 1,375 tests, with 84 new cases.

**Step 4.2 — each rule taken out, in a scratch copy of the tree.** Fourteen changes, one at a
time, each run against the parser's 150 tests. Every one failed tests, the tests meant for it
among them. The fourteenth came from the review, with the test that fails it:

| Taken out or changed | Failed | The tests meant for it |
|---|---|---|
| The call in `TryResolve` | 48 | the date-and-time table, the zone abbreviation, the rule |
| The call in `TryResolveDay` | 40 | the day table, the rule |
| The word boundary in front | 1 | `landsat` in the table of two |
| The word boundary behind | 27 | `sunrise` in the table of two; the rule; two older rows with `saturday` |
| The letter case | 2 | `Sat` and `SUN` |
| The two names swapped | 55 | both tables, the rule, the forms already read |
| The limit on the spelled-out text, removed | 2 | both limit tests, one character over |
| The limit, one character early | 4 | both limit tests, at the limit; the table of two |
| Only the first short form spelled out | 3 | `sun or sat` in both tables; the limit with two short forms |
| `TryResolve` quoting the spelled-out text | 26 | the refusal test; the rule |
| `TryResolveDay` quoting the spelled-out text | 10 | the refusal test; the rule |
| `sun` not spelled out | 29 | both tables, the rule |
| `sat` not spelled out | 48 | both tables, the rule |
| Each short form spelled out while it still fits, in place of all or none | 1 | the limit with two short forms |

**Step 4.3 — the comparison again, through the parser as built.** The released parser, a copy of
`master`'s file, and the changed one ran side by side in a scratch harness over the same 2,640
phrases at the same 42 moments: 110,880 cases. Run again on the code after the review's answers,
with the same result.

- In every case the changed parser read the typed text as the released parser reads the same text
  with the full name, and a refusal quoted the text as typed.
- The texts with the full name answered identically from both parsers.
- Against the released parser, the answer changed in 14 of the 1,327 phrases whose short form the
  recognizer already read — the fourteen of section 2 — and in 1,181 of the 1,313 others. The
  remaining 132 answer the same either way (`by sat`, refused before and after).
- The same run gives the counts of "What stops being read" above.
- A further 188 texts with no short form in them — the texts of the parser's tests at `master`,
  the documentation's examples and the earlier probes — answered identically from both parsers at
  five moments.

**Step 4.4 — the two reviews.** Two agents read the branch's three local commits — the plan, the
code with its tests, and the records — before any push. Neither found anything that must change
in the code.

The correctness review ran the change itself: the parser's tests in a scratch copy, the
comparison and the mutation checks again, and corpora and mutations of its own.

| Finding | Answer |
|---|---|
| C1, should fix — these notes understated what stops being read: besides the day itself, phrases with `by`, `until` or `before`, ranges that have begun, and `every sat 8pm` were read before and are refused now, each as its full name is. | Confirmed by a run of my own, with the same counts. Set out in "What stops being read" above, corrected in place in section 2, and put to Joshua at the completion gate. Three phrases added to the rule's table, and two tests for the class. |
| C2, should fix — the live script's `sat 6pm` item could not tell the fix from the release on a Friday after 6 PM, when the time alone rolls to Saturday. | Reworded: Sunday to Thursday, or a Friday before 6 PM. |
| C3, should fix — rule 5 was held for text with one short form only: spelling out each short form while it still fits passed every test. | A test with two short forms, at 92 and at 93 characters; that change is the fourteenth mutation check, and fails it. |
| C4, nit — the test of longer words passes with either word boundary removed, and its name read as what holds them. | Renamed `…_is_still_refused`, with a comment naming the test that holds the boundaries. |
| C5, nit — the option on the pattern that names the invariant culture did nothing. | Dropped. |
| C6, nit — "the limit is the one place where that rule can be seen from outside" is true of the boundary in front only. | Reworded above. |
| C7, nit — "Sunday at 9 AM" fits `sat 9am` only; `sat 6pm` at 23:30 rolled to Sunday at 6 PM. | Reworded: the next day. |
| C8, nit — the state file, the audit and this plan's status line disagreed about where the work stood. | Brought into line. |
| C9, nit — the free-slots plan's amendment read as if the fix were in effect from the day it was written, and "the coming Saturday" is today on a Saturday. | Reworded: it applies from the release that carries it, and today on a Saturday. |
| C10, nit — a corner at the limit was not recorded: with a zone abbreviation typed, the two readers measure different texts. | "At the limit" above. |
| C11, nit — no output was kept of the comparison of texts with no short form, and one of the texts in `master`'s tests was missing from it. | Run again with that text, and kept: 188 texts. |

The review of security and of what the public text says found nothing in the code: the length
check is still first, the recognizer is never given more than the limit, refusals quote what
they quoted before, and no check made by a caller is affected by the spelling out. It noted
three points of wording:

| Note | Answer |
|---|---|
| S1 — "refusals quote the text as typed" is loose where a zone abbreviation is typed. | "Rule 4, more exactly" above; the helper's comment says it that way. |
| S2 — the corner at the limit of C10, and that spelling out is all or nothing. | "At the limit" above, and C3's test. |
| S3 — "a word of its own" means a word as the regex engine divides text. | Said in the pattern's comment: it is also where the recognizer's own weekday patterns end a word, which is why the short form and the full name agree. |

Both agents then read the answers. The correctness review found all eleven answered and ran the
tests, the mutation checks and the comparison again on that code. The security review found the
code and the new statements sound. Their further points of wording were taken, among them the
live script's clause about when the two builds differ and the split of the 273 phrases above.
The branch was then rebuilt as two commits with the same content, and both read that before the
completion gate.

**After the gate.** `master` moved as the branch was pushed (step 7.1) and was taken in. With it
came the release summary's convention, so the pull request also carries
`docs/release-notes/0.37.1.md`: one bullet on what is new and two steps of how-to, passed by the
repository's check. On the merged tree the Release build has no error and 22 warnings, and
1,723 tests are green — 813 API, 418 bot, 153 web and 339 for the release-notes tool that came
with `master`, with 17 more of the tool's skipped on Windows, which CI runs. `master` moved once
more that night, with pull request #181, and was taken in again: 1,729 tests then, the tool's
own having grown to 345.

**Noticed while probing, not part of this fix.** Each is true of every weekday, short or full,
and is listed for Joshua to take up or leave:

- `by friday`, `until saturday`, `before saturday` are refused as not understood: the recognizer
  gives a range with an end only, and both readers want a start. `every saturday 8pm` is refused
  in the same words: it is a repeat, and neither reader takes one.
- A weekday typed on its own day after 9 AM is refused as past by the date-and-time reader
  (`wed` on a Wednesday at 10:00): a bare date means 9 AM, and the recognizer offers last week's
  and today's, not next week's. With a time still ahead (`wed 6pm`) it is read; with a time
  already behind (`wed 9am`) it is refused in the same way.
- A day and a time split by a full stop, brackets or `from` are two readings, and the
  date-and-time reader takes the earlier: `fri (6pm)`, `fri. 6pm` and `friday from 6pm` give today
  at 6 PM, while the day reader gives Friday. The free-slots plan records the same for
  `tuesday, next week`.
- `easter sat` reads as Easter Sunday today — "easter" is read and the rest dropped. Under this
  change it reads as the Saturday before.
