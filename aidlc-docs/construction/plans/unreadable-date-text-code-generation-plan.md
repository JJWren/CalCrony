# Date text that cannot be read — functional note and code-generation plan

**Task**: two places that read a date typed in words refuse text they cannot read, each with the
sentence it already has for that. Handed over on 2026-11-16 from another session.
**Baseline**: `master` 4af1305 — the application code of v0.37.0, with the release tooling of
pull request #179 beside it. Run as step 0.1.
**Branch**: `fix/unreadable-date-text` (local) → PR `fix: refuse date text that is missing or cannot
be read`; with Q2 answered B, `fix: refuse a zone abbreviation or RSVP cutoff amount that cannot be
read`.
**Status**: written 2026-11-16 and approved by Joshua the same day, with Q1, Q2 and Q3 answered A
(section 3). Generated that day and reviewed on 2026-11-17 (section 7). The wording has been
edited since in places; the one change of substance is 2.2 rule 3, amended on review. Approved at
the completion gate on 2026-11-17 ("B) Continue"), with that rule as amended. Delivery is
section 6.

A small fix, so the workflow runs at minimal depth: the task is the requirement; no user stories,
application design, units, NFR or infrastructure stages; one unit, and this file is both its
functional note (sections 1–3) and its code-generation plan (sections 4–6), approved together.

## 1. What this change ships

1. **A zone abbreviation in date text.** `NaturalDateTimeParser.TryResolve` finds a zone
   abbreviation with its pattern and looks the zone up in its table. Text whose abbreviation the
   table has no zone for is refused with the parser's sentence for text it does not understand.
2. **The amount of a relative RSVP cutoff.** `RsvpPolicy.TryParseClose` reads the relative form
   (`2h before`, `90 min before start`, `1 day`). Text in that form whose amount cannot be read as
   a number is refused, with the same sentence behind the cutoff reader's own words. *(Amended on
   review: section 7.)*
3. **Missing date text** — only with Q2 answered A. Both of the parser's readers refuse text that
   is missing altogether, with a sentence of its own.

Nothing that is read today is read differently. The change is in the API, in the same two files
whichever way Q2 is answered. No migration, no configuration, nothing new stored. Contracts, the
bot and the web app are not changed: they show the API's sentence as they show its other refusals.

## 2. Rules

### 2.1 The zone abbreviation

1. As before: the first abbreviation the pattern finds selects the zone the text is read in, and
   that one token is taken out of the text before the recognizer reads it.
2. New: when the table has no zone for the abbreviation found, `TryResolve` answers false with
   `Could not understand "<text>" as a date/time.` — the sentence it gives for text that is not
   understood, with the text as typed. The recognizer is not asked.
3. As before: a word the pattern does not take as an abbreviation is left to the recognizer, which
   passes over words it does not read. `6pm AEST` is 6 PM in the caller's zone.
4. As before: an abbreviation after the first is left to the recognizer too (`6pm ET / 3pm PT`).
5. `TryResolveDay` is not changed by this rule. It looks no zone up: a day is always the caller's.
6. An RSVP cutoff that is not in the relative form is read by `TryResolve` (2.2 rule 4), so rule 2
   holds there as well, behind the cutoff reader's own words.

### 2.2 The relative RSVP cutoff

1. As before: the relative form is an amount of one to five digits, a unit (`m`, `min`, `mins`,
   `minute`, `minutes`, `h`, `hr`, `hrs`, `hour`, `hours`, `d`, `day`, `days`) and, optionally,
   `before` or `before start`, in any letter case.
2. As before: a relative cutoff must come to between 1 minute and `FieldLimits.MaxMinutes`
   (40,320 minutes, four weeks). Outside that it is refused with the sentence that names the range.
3. New: text in the relative form whose amount cannot be read as a number is refused by the
   cutoff reader itself, with `Couldn't read the RSVP cutoff: Could not understand "<text>" as a
   date/time.` — the sentence cutoff text gets when the date parser does not understand it. It is
   not handed on to be read as a date. *(Amended on review: section 7.)*
4. As before: cutoff text that is not in the relative form is read as a date and time by
   `NaturalDateTimeParser.TryResolve`. Where it is not one, the refusal is the parser's behind the
   reader's own words.
5. Every cutoff that is read today is read as before, and the text that goes to the date parser
   is the text that went to it before.
6. As before: cutoff text of more than `DateTextLimits.MaxLength` characters is refused as too
   long, before either form is read.

### 2.3 Missing date text (only with Q2 answered A)

1. `TryResolve` and `TryResolveDay` answer false for text that is missing altogether, with
   `The date/time text is required.`
2. So a request that leaves out date text its route needs is answered with a 400 and that
   sentence: the parse route (`text`), a new reminder (`whenText`) and a new event (`whenText`).
   On every other route the date text is optional, or is checked before it is read.
3. Text that is there but empty or blank is refused as before, as text that is not understood.

## 3. Questions

Q1 — When to build. Pull request #180 (`sat` and `sun`) also edits `NaturalDateTimeParser`, is
ahead of this change and has not merged.

- A) Build now on `master` as it is, and take `master` in before the push. **(Recommended. This
  change edits lines of the parser that pull request #180 leaves alone, and its parser tests go
  in beside the zone tests, away from where that pull request adds its own. So the merge is
  expected to be clean in the code; the two records files are merged by hand, entries by
  timestamp. Nothing is pushed either way until Joshua says so.)**
- B) Wait until it has merged, then build.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

Q2 — Missing date text. Should text that is missing altogether be refused in this change too?

- A) Yes, as section 2.3. **(Recommended. It is the same kind of refusal in the same two readers,
  about ten lines with their tests, and it then goes out with this release, not as one more
  release of its own.)**
- B) No. This change stays with sections 2.1 and 2.2, and missing text is a change of its own
  later.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

Q3 — A zone abbreviation the table has no zone for. What should the parser do with the text?

- A) Refuse it as not understood (2.1 rule 2). **(Recommended, and what the task asks. The text
  names a zone, the parser cannot honour it, and it says so.)**
- B) Leave the abbreviation unread and read the rest in the caller's zone, as a word the pattern
  does not take is read today (2.1 rule 3). The price is a time in a zone other than the one the
  text names, given without a word.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-16: "Approve, all A".

## 4. What changes, file by file

| File | Change |
|---|---|
| `src/CalCrony.Api/Services/NaturalDateTimeParser.cs` | The zone lookup in `TryResolve` (2.1). One helper for the sentence of text that is not understood, which the cutoff reader uses as well. With Q2 answered A: the first lines of `TryResolve` and `TryResolveDay`, the line for their `text` parameter, and one constant for the sentence (2.3). |
| `src/CalCrony.Api/Services/RsvpPolicy.cs` | `TryParseClose`: the refusal of 2.2 rule 3 where the amount is read, and a clause in its summary. |
| `tests/CalCrony.Api.Tests/NaturalDateTimeParserTests.cs` | Beside the zone tests: step 1.1. |
| `tests/CalCrony.Api.Tests/RsvpPolicyTests.cs` | Beside the cutoff tests: step 1.2. |
| `tests/CalCrony.Api.Tests/ValidationApiTests.cs` | Route checks: step 1.3. |
| `aidlc-docs/aidlc-state.md`, `aidlc-docs/audit.md` | The records. |

Not changed: Contracts, the bot, the web app, the database, every route's shape. No documentation
example becomes wrong — the README, the Docs page, the event form's placeholder and the bot's
option descriptions show `2h before` and `friday 5pm`, both read as before — so none is edited.

## 5. Execution plan

A box is ticked in the turn its step completes.

**0. Baseline**
- [x] 0.1 On `fix/unreadable-date-text`, before the first change: `dotnet build CalCrony.slnx -c
      Release` and `dotnet test CalCrony.slnx -c Release` green; counts recorded. *(Run on
      2026-11-16 before approval, because it changes nothing: 0 errors and 22 warnings, the known
      baseline; 1,630 tests passed and none failed — 721 API, 418 bot, 153 web, 338 release
      notes — with 17 more of the release-notes project skipped by their own conditions on this
      machine. Ticked on approval.)*

**1. Tests first** — written and run against the unchanged code
- [x] 1.1 `NaturalDateTimeParserTests`, beside the zone tests. A zone abbreviation the table has
      no zone for is refused with the exact sentence and the text as typed, where it stands at
      the end of the text, at the start, alone, and before a listed one. Pinned beside it, as read
      today: the listed abbreviations still select their zone (rows added to the existing table);
      a listed abbreviation that comes first still selects the zone whatever follows it; and a
      word the pattern does not take is still left to the recognizer. With Q2 answered A: both
      readers refuse missing text with its sentence, and text that is there but empty is still
      refused as not understood (2.3 rule 3).
- [x] 1.2 `RsvpPolicyTests`, beside the cutoff tests. Cutoff text whose amount cannot be read is
      refused with the exact sentence of 2.2 rule 3, with neither form set, and is not read as a
      date; so is cutoff text with a zone abbreviation that has no zone (2.1 rule 6). Pinned
      beside it, as read today: the longest amount that is allowed, an amount written with leading
      zeros, and a unit in capitals with `BEFORE START` (rows added to the existing table); and an
      amount of six digits, which is not the relative form and is refused by the date parser.
- [x] 1.3 `ValidationApiTests`: the parse route answers 400 with the sentence of 2.1 rule 2; a new
      event and an edited event answer 400 with the sentence of 2.2 rule 3 for such a cutoff.
      With Q2 answered A: the three routes of 2.3 rule 2 answer 400 with its sentence for a
      request without the text.
- [x] 1.4 The run on the unchanged code: every new refusal test fails, and every test that pins
      what is read today passes. *(2026-11-16, before the change, with the tests as first written:
      12 of the 124 tests in the three classes failed. With the tests as they are now, in a
      scratch copy with the two source files as on `master`: 14 of 141. Section 7 has the list.)*

**2. The change**
- [x] 2.1 `NaturalDateTimeParser.TryResolve`: rule 2 of 2.1, where the zone is looked up.
- [x] 2.2 `RsvpPolicy.TryParseClose`: rule 3 of 2.2, where the amount is read. *(Amended on
      review: section 7.)*
- [x] 2.3 With Q2 answered A: the two readers refuse missing text (2.3).

**3. Verification**
- [x] 3.1 `dotnet build CalCrony.slnx -c Release` with no new warning; `dotnet test CalCrony.slnx
      -c Release` green; counts recorded against 0.1. *(Section 7 has the counts.)*
- [x] 3.2 Each rule taken out in a scratch copy fails a test meant to pin it: 2.1 rule 2, taken
      out and turned into reading on; 2.2 rule 3, taken out and turned into handing the text on;
      and, with Q2 answered A, 2.3 rule 1 in each reader. *(Fifteen changes, each caught: the table
      is in section 7, with a comparison of the code before and after over the same texts.)*
- [x] 3.3 The whole diff read against section 2, rule by rule.
- [x] 3.4 Two review agents read the local commits before anything is pushed, one for correctness
      and one for security. Their findings are answered, and section 7 says what changed.
- [x] 3.5 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 6. Delivery

Approving this plan covers the changes above and committing them locally. Everything that leaves
this machine waits for its own go-ahead.

- [x] 6.1 *Go-ahead (the completion gate):* take `master` in if it has moved — the audit entries
      by timestamp, every section of the state file kept, the suite again and the reviewers' read
      of the result — then push the branch and open the pull request. *(2026-11-17, on the
      go-ahead at the completion gate: `master` had not moved since the rebuild on `ab32e96`, so
      nothing was taken in; pushed at 16:36:05Z, and pull request #184 opened at 16:36:24Z. Pull
      request #180 merged twelve seconds later, and `master` was taken in straight after:
      section 7.)*
- [ ] 6.2 Checks green; a review if one starts. *Go-ahead:* squash merge. The test stack then
      takes `:main`.
- [ ] 6.3 *Go-ahead:* the release — the patch release that release-please opens — once it is
      settled with Joshua whether this change gets a line in the release summary
      (`docs/release-notes/<version>.md`).
- [ ] 6.4 *Go-ahead:* prod rollout, by the usual steps: backup, the pinned tag, app services only.
      No migration.

## 7. Notes from generation

### Where the code differs from the plan as approved

- **2.2 rule 3 was amended on review.** As approved, the relative form took only an amount it could
  read, and other text in that form went on to the date parser. Now text in the relative form
  whose amount cannot be read as a number is refused by the cutoff reader itself, with the
  sentence of rule 3, so text in that form is never read as a date. The text that reaches the
  date parser is exactly the text that reached it before the change. Section 1, rules 2.2 and
  steps 1.2, 2.2 and 3.2 are worded for the code as it is now. This was put to Joshua at the
  completion gate, and he approved it there on 2026-11-17 ("B) Continue").
- The sentence for text that is not understood has a helper, `NotUnderstoodError`, used by the
  refusal of 2.1 rule 2 and by the refusal of 2.2 rule 3. The parser's older refusal in the same
  words is left as it is, and tests hold all three to the same sentence.
- Tests were added to the lists of steps 1.1 and 1.2, each for a rule of section 2 that had
  none: cutoff text with a zone abbreviation that has no zone (2.1 rule 6); the day reader
  looking no zone up (2.1 rule 5); a row for every listed abbreviation (2.1 rule 1); text that
  is there but empty or blank (2.3 rule 3); an amount of six digits (2.2 rule 1); and relative
  text whose amount cannot be read not being read as a date (2.2 rule 3).
- The constant for the sentence of 2.3 and the helper sit beside `LocalPattern`. The two readers'
  summaries are left as they are: the line for the `text` parameter says what happens to missing
  text.
- `RsvpPolicy.TryParseClose` is not given missing text: both places that call it check first,
  and it is left as it is.

### Tests added

37 cases, all in `CalCrony.Api.Tests` (721 before, 758 now):

- `NaturalDateTimeParserTests`, 25: `Refuses_a_zone_abbreviation_it_has_no_zone_for` (four
  texts), `A_listed_abbreviation_that_comes_first_selects_the_zone_whatever_follows`,
  `A_word_the_zone_pattern_does_not_take_is_left_to_the_recognizer`,
  `Day_resolution_looks_no_zone_up`, fourteen rows in `Zone_abbreviations_cover_common_us_zones`,
  `Refuses_missing_text`, `Day_resolution_refuses_missing_text` and
  `Empty_or_blank_text_is_still_refused_as_not_understood` (two texts).
- `RsvpPolicyTests`, 9: `Cutoff_text_whose_amount_cannot_be_read_is_refused` (three texts),
  `Relative_text_whose_amount_cannot_be_read_is_not_read_as_a_date`,
  `Cutoff_text_with_a_zone_abbreviation_that_has_no_zone_is_refused`,
  `An_amount_of_more_than_five_digits_is_not_the_relative_form` and three rows in
  `Relative_text_becomes_minutes_before_start`.
- `ValidationApiTests`, 3: `Parse_datetime_refuses_a_zone_abbreviation_it_has_no_zone_for`,
  `Create_and_update_event_refuse_an_rsvp_cutoff_whose_amount_cannot_be_read` and
  `Routes_that_need_date_text_refuse_a_request_without_it`.

### The run on the unchanged code (step 1.4)

It was made before the change, on 2026-11-16, with the tests as first written: 124 tests in the
three classes, of which 12 failed and 112 passed. It was made again on 2026-11-17 with the tests
as they are now, in a scratch copy with the two source files as on `master`: 141 tests, of which
14 failed and 127 passed. The fourteen are the refusal tests: the four texts of
`Refuses_a_zone_abbreviation_it_has_no_zone_for`, `Refuses_missing_text`,
`Day_resolution_refuses_missing_text`, the three texts of
`Cutoff_text_whose_amount_cannot_be_read_is_refused`,
`Relative_text_whose_amount_cannot_be_read_is_not_read_as_a_date`,
`Cutoff_text_with_a_zone_abbreviation_that_has_no_zone_is_refused`, and the three route checks.
Every test that pins what is read today passed both times.

### Rules taken out, one at a time (step 3.2)

Each change below was made on its own in a scratch copy of the code as it is now, and put back
after. Each time the three classes were run: 141 tests, all green with nothing taken out.
Fifteen changes; each failed the tests named and no others.

| Rule taken out | Tests that failed |
|---|---|
| 2.1 rule 2 — the zone looked up as before the change | 6: the four texts of `Refuses_a_zone_abbreviation_it_has_no_zone_for`, `Cutoff_text_with_a_zone_abbreviation_that_has_no_zone_is_refused`, `Parse_datetime_refuses_a_zone_abbreviation_it_has_no_zone_for` |
| 2.1 rule 2 — turned into reading on in the caller's zone | 5: the same six without the text that is the abbreviation alone, which is refused either way |
| 2.1 rule 2 — the refusal quoting the abbreviation in place of the text as typed | 5: the same five |
| 2.1 rule 5 — the refusal of rule 2 made by `TryResolveDay` as well | 1: `Day_resolution_looks_no_zone_up` |
| 2.2 rule 3 — the amount read as before the change | 5: the three texts of `Cutoff_text_whose_amount_cannot_be_read_is_refused`, `Relative_text_whose_amount_cannot_be_read_is_not_read_as_a_date`, `Create_and_update_event_refuse_an_rsvp_cutoff_whose_amount_cannot_be_read` |
| 2.2 rule 3 — turned into handing the text on to the date parser | 1: `Relative_text_whose_amount_cannot_be_read_is_not_read_as_a_date` |
| 2.2 rule 3 — refused with the range's sentence (2.2 rule 2) in place of its own | 5: the same five as two rows up |
| 2.2 rule 1 — an amount of at most four digits | 3: `40320m` and `00090 min` in `Relative_text_becomes_minutes_before_start`, and `Relative_cutoffs_beyond_four_weeks_are_rejected`, which was there before |
| 2.2 rule 1 — an amount of up to six digits | 1: `An_amount_of_more_than_five_digits_is_not_the_relative_form` |
| 2.3 rule 1 — taken out of `TryResolve` | 2: `Refuses_missing_text`, `Routes_that_need_date_text_refuse_a_request_without_it` |
| 2.3 rule 1 — taken out of `TryResolveDay` | 1: `Day_resolution_refuses_missing_text` |
| 2.3 rule 3 — empty text taken for missing text, in `TryResolve` and in `TryResolveDay` (one run each) | 1 each: the empty text of `Empty_or_blank_text_is_still_refused_as_not_understood` |
| 2.3 rule 3 — blank text taken for missing text, in `TryResolve` and in `TryResolveDay` (one run each) | 1 each: the blank text of the same test |

With the whole change taken out, the two source files as on `master`, the fourteen tests of the
section above fail.

### What is read today (2026-11-17)

The code before the change and the code as it is now were given the same generated texts:
ordinary date phrases, every listed abbreviation in several places in a text, every form of the
relative cutoff, and random texts built from date words, numbers and punctuation. Date texts went
to `TryResolve` in two zones, to `TryResolveDay` and to `TryParseClose`; cutoff texts went to
`TryParseClose`. Every reading and every refusal the code gave before the change, 477,282 of
them, is the same now, word for word.

### Verification (steps 3.1 and 3.3)

On `master` at `ab32e96` with this change: `dotnet build CalCrony.slnx -c Release`, without
incremental state, has 0 errors and 22 warnings, the baseline's, none in a file this change
touches. `dotnet test CalCrony.slnx -c Release`: 1,673 tests passed and none failed — 758 API,
418 bot, 153 web, 344 release notes — with the same 17 of the release-notes project skipped as
in step 0.1. That is `master`'s own 1,636 and the 37 added here: pull request #181 had brought
the release-notes tests from the 338 of step 0.1 to 344. The diff was read against section 2
rule by rule: each rule marked "new" has its lines and its tests, and no line changes a rule
marked "as before".

### Review (step 3.4)

Both review agents read the change on 2026-11-16 and 17 as three local commits (the plan, the
change with its tests, and the records), each in a copy of its own in which it built and ran
them. Correctness found the code to do what section 2 said for every caller, and found no text
whose reading or refusal had changed. It asked for rule 2.2 to be tightened, as the first point
of this section describes; for tests of rules that had none (2.1 rule 5, every listed
abbreviation, the blank half of 2.3 rule 3); and for exact statements in these notes and in the
audit entries: where `master` stood, how many rules were taken out, and what the comparison ran.
Security found the refusals in place at every route that reads date text. Asked for and not
taken: further rows for the new refusal tests; the new constant placed beside the
older one, and missing text named in the two readers' summaries; and a comment and two unpinned
details in the older tests and the older pattern, which this change does not touch.

What was taken is answered in commits rebuilt on `master` at `ab32e96`. Both agents read those on
2026-11-17, each again in a copy of its own, and found the code and the tests sound, with every
finding on them answered. What they asked for then was in the wording of these records and of two
comments. Both agents read the commits that carry it and had no objection to them going to the
completion gate. The records of the gate itself were added after that read.

### `master` taken in after the push (2026-11-17)

Pull request #180 merged twelve seconds after this change's pull request was opened, so `master`,
at `b582c2b`, was taken in by a merge. The parser and its tests merged by themselves, because this
change's lines and that pull request's are apart; the two records files were merged by hand, the
audit entries in the order of their stamps. One row was added to
`Refuses_a_zone_abbreviation_it_has_no_zone_for` for the two changes together: text with a short
weekend day and a zone abbreviation that has no zone is refused, and the refusal quotes the text
as typed. That makes 38 cases added by this change. On the merged tree the build has 0 errors and
the baseline's 22 warnings, and 1,767 tests pass — 851 API, 418 bot, 153 web, 345 release notes,
with the same 17 skipped. A clean copy of `master` at `b582c2b` has 1,729 (813 API, 418 bot, 153
web, 345 release notes).

Pull requests #183 and #182 then merged, at 16:49:14Z and 17:02:31Z, before that merge was
pushed, and `master`, at `4fdfa60`, was taken in by a second merge on top of the first. Neither
touches this change's code or tests, so only the two records files needed a hand again. On that
tree the build has 0 errors and the baseline's 22 warnings, and 1,781 tests pass — 865 API, 418
bot, 153 web, 345 release notes, with the same 17 skipped. A clean copy of `master` at `4fdfa60`
has 1,743 (827 API).

Pull request #186, which changes records only, merged at 17:08:55Z, nineteen seconds after those
two merges were pushed. `master`, at `916ecd1`, was taken in a third time: the two records files
by hand again, and nothing else. The code and the tests are those of the second merge.
