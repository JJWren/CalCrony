# One place for an event's end — functional note and code-generation plan

**Task**: make `SeriesProjection.EndOf` call `EventTimes.End`, so that the API works out an
event's end in one place, with one value for the length of an event that has none set; and
leave no summary saying that the scheduler and the feed each keep their own copy of that value,
as the summary of `SeriesProjection.DefaultLengthMinutes` does. Handed over on 2026-11-18 by the
session of pull request #196, once that pull request had merged.
**Baseline**: `master` `aeff22d` (pull request #190 merged).
**Branch**: `refactor/event-end-one-place` → a pull request titled
`refactor: work out an event's end in one place`.
**Status**: written 2026-11-18 and approved by Joshua the same day as written, with Q1 answered
A ("Approve, Q1 A"); code generated and checked the same day; the change read twice by two review
agents without a fault, and each record after it read again before the push, their findings
answered; `master` taken in before the push, last at `f6152d7`, and after it at `dab6a31` and
`11afb47` (section 7). Approved at the completion gate the same day ("B) Continue"); pushed on
2026-11-19 on Joshua's go-ahead ("Push and open"), as pull request #210; the merge has his
go-ahead ("Merge when green"), taken up again on his "Try again".

A change inside one helper of the API, with a test, that nobody using CalCrony will notice, so
the workflow runs at minimal depth: the task is the requirement; no user stories, application
design, units, NFR or infrastructure stages; one unit, and this file is both its functional note
(sections 1 to 4) and its code-generation plan (sections 5 and 6), approved together.

## 1. What is there today

Two helpers in `src/CalCrony.Api/Services` add an event's length to its start:

| Helper | Added by | Used by |
|---|---|---|
| `EventTimes.End` | pull request #189 | the scheduler, to tell when a started event has ended; the ICS feed, for the end of each event, of each running series and of a series' live occurrence; the route that checks the availability of an event's attendees (`GET /events/{id}/availability`) |
| `SeriesProjection.EndOf` | pull request #196 | the public month calendar: the end of a series' live occurrence, and of each occurrence that `SeriesProjection.Upcoming` steps through |

Each has a constant for the length of an event that has none set, 60 minutes in both.
`EventTimes.DefaultLengthMinutes` is, in the words of its class summary, the API's one place for
that length. The summary of `SeriesProjection.DefaultLengthMinutes` says that the scheduler and
the feed each keep their own copy of the value; since #189 neither does, as both take it from
`EventTimes`.

## 2. The change

- `SeriesProjection.EndOf` returns `EventTimes.End(startsAt, durationMinutes)`. It keeps its name
  and its signature, so its two callers, `PublicCalendarBuilder.Build` and
  `SeriesProjection.Upcoming`, do not change; its summary says that it is `EventTimes.End`.
- `SeriesProjection.DefaultLengthMinutes` is removed (Q1): nothing outside `SeriesProjection`
  uses it, and `EventTimes.DefaultLengthMinutes` is then the one value. The summaries in
  `SeriesProjection.cs` that speak of "the default" length name `EventTimes.DefaultLengthMinutes`.
- Nothing else under `src` changes: `PublicCalendarBuilder`, the calendar's route, the ICS feed,
  the scheduler and `EventTimes` stay as they are on `master`.

## 3. What does not change

Anything anyone sees. Every date the public calendar shows stays where it is, and no existing
test is changed: every test passes as it does on `master`, the calendar's (its builder's and its
route's), the feed's, `EventTimes`' and `SeriesProjection`'s among them. The default hour stays
held, through the calendar, by the test pull request #196 added for it
(`An_occurrence_with_no_length_of_its_own_runs_an_hour`). No line in the release summary, since
nobody using CalCrony will notice the change.

## 4. Question

**Q1. What becomes of `SeriesProjection.DefaultLengthMinutes`?**

- A) It is removed, and `EventTimes.DefaultLengthMinutes` is the API's one value for the length
  of an event that has none set, as its class summary already says; the summary that is out of
  date goes with the constant. Nothing outside `SeriesProjection` uses it (recommended: one
  value, in one place).
- B) It stays, set to `EventTimes.DefaultLengthMinutes`, with its summary corrected to say that
  it is that value. Nothing else changes, but the API has two names for one value.
- C) Other (please describe).

[Answer]: A

## 5. Tests

One new test in `tests/CalCrony.Api.Tests/SeriesProjectionTests.cs`, with four cases: an
occurrence that starts on Monday 2 November 2026 at 18:00 UTC ends at 19:00 with no length set
(an hour, `EventTimes.DefaultLengthMinutes`), at 18:01 with a length of one minute, at 19:30 with
90 minutes, and on 30 November at 18:00 with 40,320 minutes (four weeks, the longest an event
may last); in each case where `EventTimes.End` says that event ends.

The rest is held by the tests already there, unchanged: the calendar's, the feed's and those of
`EventTimes`.

## 6. Steps

### 0. Baseline
- [x] 0.1 On `aeff22d`: the Release build's errors and warnings, and the whole suite's counts.

### 1. Code
- [x] 1.1 `SeriesProjection.EndOf` returns `EventTimes.End(startsAt, durationMinutes)`; its
  summary says so.
- [x] 1.2 `SeriesProjection.DefaultLengthMinutes` removed (Q1 = A); the summaries of
  `LiveOccurrence` and of `EndOf` name `EventTimes.DefaultLengthMinutes`.

### 2. Tests
- [x] 2.1 The test of section 5.

### 3. Checks
- [x] 3.1 Release build: no error, and the warnings of the baseline.
- [x] 3.2 The whole suite passes, with the new test's four cases as the only addition and no
  other test changed.
- [x] 3.3 Under `src`, the branch changes `Services/SeriesProjection.cs` alone.
- [x] 3.4 The summaries' references resolve: the API built once with its documentation file
  turned on, outside the commit, with no warning about them.
- [x] 3.5 No invisible characters in what the branch adds.

### 4. Records
- [x] 4.1 This plan ticked, with what was built and checked; the state file's section; audit
  entries.
- [x] 4.2 Committed locally, without attribution lines.

### 5. Review, and what follows
- [ ] 5.1 Two review agents read every commit before any push, one for security and privacy and
  one for quality and accuracy, until neither has a finding.
- [x] 5.2 The completion gate: "Code generation complete. Choose: A) Request Changes  B) Continue
  to Next Stage". Answered B on 2026-11-18 ("B) Continue").
- [x] 5.3 Before the push, `master` merged in: its records as they are, with this branch's audit
  entries placed among them by timestamp. Done at `f05e1d0`, `b1f5c58` and `f6152d7` (section 7),
  the last just before the push, and after the push at `dab6a31` and `11afb47`, before the
  merge.
- [x] 5.4 The push and the pull request, on Joshua's go-ahead. Given on 2026-11-19 ("Push and
  open"); pushed the same day, after `master` was taken in at `f6152d7`, as pull request #210.
- [ ] 5.5 The merge, on his go-ahead. The change then goes out with the next release, whose
  release and rollout each have their own go-ahead.

## 7. As built

Generated on 2026-11-18, after the approval.

**The change** is the one of section 2, with Q1 answered A:

- `src/CalCrony.Api/Services/SeriesProjection.cs`: `EndOf` returns
  `EventTimes.End(startsAt, durationMinutes)`, and its summary names `EventTimes.End`;
  `DefaultLengthMinutes` is removed; the two summaries that said a length of null meant "the
  default" (`LiveOccurrence`'s `DurationMinutes` and `EndOf`'s `durationMinutes`) now name
  `EventTimes.DefaultLengthMinutes`.
- `tests/CalCrony.Api.Tests/SeriesProjectionTests.cs`: the test of section 5,
  `EndOf_is_where_EventTimes_says_an_event_ends`, with its four cases. The class's summary now
  names both things it asks of the helper, which the plan did not say.

**Checks**

- Baseline on `aeff22d`: the Release build has no error and 22 warnings; 2,533 tests pass and
  none fails (1,540 API, 490 bot, 154 web and 349 for the release-notes tool, seventeen of whose
  tests are skipped on this Windows machine and run in CI).
- With the change: the Release build has no error and the same 22 warnings; 2,537 tests pass and
  none fails (1,544 API, 490 bot, 154 web and 349 for the release-notes tool, with the same
  seventeen skipped): the baseline's tests and the new test's four cases.
- Under `src` and `tests`, the branch changes `Services/SeriesProjection.cs` and
  `SeriesProjectionTests.cs` alone.
- The API built once with its documentation file turned on, in a build configuration of its own
  whose output was then deleted: the new references resolve, to `EventTimes.End(Instant, int?)`
  and to `EventTimes.DefaultLengthMinutes`, and no warning names `SeriesProjection.cs`.
- No invisible characters in what the branch adds.

**`master` taken in.** Before the review, `master` had moved from `aeff22d` to `ee844fb` (pull
request #204, which changes code and tests outside this change's files, and pull requests #203,
#205 and #206, records and documentation). The review read the branch with that merged in:
`98b4d81` and `f71d3ad`, the merge `055a7c5` and its record `f33a05c`, none of which was pushed.
Pull request #200, the release of 0.37.2, had merged seconds after that merge, and while the
review read, pull request #197 merged, which rewrites `SeriesProjection.cs` and
`SeriesProjectionTests.cs` for the free-slots lookup; `master` was then at `f05e1d0`. So the
branch is made again on `aeff22d`, as `8c93b63` and `844f8ff`, with one merge of `master`, at
`f05e1d0`, as `73fc5bf`, in place of the merge of `ee844fb`; the audit entry of 23:12:47Z, which
recorded that merge, goes with it. The records keep master's text as it is, with this branch's
audit entries each placed among master's by its timestamp and this state section after
master's. The two files are #197's, with this change made to them: the constant removed, `EndOf`
returning `EventTimes.End`, and the summaries that spoke of the default length naming
`EventTimes.DefaultLengthMinutes`, three of them now with #197's `SeriesSchedule`; the test
class's summary, #197's, names `EndOf` as well, and the new test sits before `Upcoming`'s.
#197's own new code already takes an occurrence's end from `EventTimes.End`.
`PublicCalendarBuilder.cs` and `EventTimes.cs` are as on `master`. On the merged tree the Release
build has no error and the same 22 warnings, and 2,661 tests pass and none fails (1,658 API, 499
bot, 154 web and 350 for the release-notes tool, with the same seventeen skipped); with the
documentation file turned on, the references this change adds resolve and no warning names
`SeriesProjection.cs`.

**`master` taken in again.** After the completion gate, `master` moved to `b1f5c58` (pull request
#208, records only, merged at 00:23:55Z on 2026-11-19). It is merged in the same way, as
`f2ee6c7`: only the audit file conflicted, and git merged the state file by itself, to master's
file with this section after it. Outside the records the tree is that of `73fc5bf`; on
`f2ee6c7` the Release build has no error and the same 22 warnings, and the same 2,661 tests pass
and none fails.

**Review.** Two review agents read the branch before any push, together with the draft text of
the pull request: one for security and privacy, one for quality and accuracy.

*First read*, of `98b4d81`, `f71d3ad`, `055a7c5` and `f33a05c`: neither found a fault in the
change. Between them, by reading and by runs of their own: `EndOf` gives the end `EventTimes.End`
gives; the builder, `EventTimes` and the calendar's route are as on `master`; no route's answer
changes; the references this change adds resolve; and the merge kept master's records as they
are, with this branch's entries by their timestamps. Their findings, with the answers:

- Both (quality: high; security: medium): `master` had moved on to `f05e1d0`, and #197 changes
  the two files this change touches. *Answered*: `master` taken in, as above.
- Quality (low): the status did not say that `master` had been taken in. *Answered* in the
  status.
- Quality (nit): the audit entry of 22:47:36Z gives `master` on GitHub as `aeff22d`; pull request
  #203 had merged at 22:40:33Z, so it was `5b30e5b` by then. The branch was cut from `aeff22d`,
  where the baseline ran. *Answered* in the entry of 23:29:42Z.
- Quality (nit): step 0.1 was ticked in the change's commit, not in the plan's. No change.

*Second read*, of `8c93b63`, `844f8ff`, `73fc5bf` and `e775448`: neither found a fault in the
change or in the merge. The quality agent's own run of the whole suite on `73fc5bf` gave the
counts above, and the security agent's runs of 165 tests on that code, the public calendar's and
the free-slots lookup's route tests among them, passed. Their findings, with the answers:

- Quality (low): the records did not name the commits the first read covered, nor say what
  became of the audit entry of 23:12:47Z. *Answered* above.
- Both (nit): the entry of 23:29:42Z gives #203's merge as 22:40:32Z, the time of its commit;
  GitHub gives 22:40:33Z. Quality (nit): that entry also says that `master` moved to `f05e1d0`
  while the review read, when #200 had merged before it began, and that every reference in the
  API resolves, when the build reports three ambiguous references in `AttendeeRoleSync.cs`, which
  this change does not touch. *Answered* in this section and in the next audit entry.
- Quality (nit): the step-0.1 nit was missing from this section. *Answered* above.
- Both (nit): a line of the pull request's draft text was longer than the rest. *Answered*: the
  summary's long line and three checklist items rewrapped.

*Third read*, of `500c188` (the record of the second read, amended into `95b1c81` before anything
was pushed) and the draft text: neither found a fault. Their nits are answered here and in that
record: a paragraph of this section rewrapped, the first read's first item given its severity,
the item above made exact about what was rewrapped, and the second read's paragraph and its
audit entry made exact about who ran which tests.

*Fourth read*, of `95b1c81` (records only): the security agent found nothing; the quality agent
found (low) that the status and the audit entry of 23:42:23Z said the change had been read three
times, when the third read was of the records alone, and (low) that the third read named no
commit. *Answered* in the status, above and in the next audit entry.

*Later reads*, of the records alone, each before the push:

- `2992182`, the record of the completion gate: the security agent found nothing; the quality
  agent found (low) that "without a fault" was said of the reads of the records as well.
  *Answered* by amending it into `18607c7`.
- `18607c7`: both found (nit) a line broken in mid-paragraph. *Answered* by amending it into
  `85c050c`, in which neither found anything.
- `de7fd42` and `f2ee6c7`, the record of the go-ahead for the push and the merge of `b1f5c58`:
  neither found anything in the merge; the quality agent asked (low) for that take-in to be
  recorded, and both for the pull request's draft text to name `b1f5c58`. *Answered* in
  `2d71749`.
- `2d71749`: the security agent found nothing; the quality agent found (low) that this section
  stopped at the fourth read. *Answered* by this list, amended into that commit before the push.
  Both agents read the amended commit before the push.
- `5e4ea37`, the amended commit, and `2573c7f`, the merge of `f6152d7` (below): neither found
  anything in them; both asked for the pull request's draft text to name `f6152d7` rather than
  `b1f5c58`. *Answered* before the pull request was opened.

**`master` taken in a third time, and the push.** Just before the push, `master` moved to
`f6152d7`: pull request #207, the release of 0.38.0, `CHANGELOG.md` only, merged at 01:24:11Z on
2026-11-19. It is merged in without a conflict, as `2573c7f`, and on it the Release build has no
error and the same 22 warnings, and the same 2,661 tests pass and none fails. The branch was
pushed by its name with `2573c7f` at its head, and pull request #210 opened with the text both
agents read. Every check passed on `2573c7f`, and Joshua gave the merge its go-ahead ("Merge when
green").

**`master` taken in after the push.** Before the merge, `master` moved to `dab6a31` (pull request
#209, merged at 03:25:44Z on 2026-11-19, which adds tests and records), and the pull request's
records conflicted with it. It is merged in as `315fac2`: only the audit file conflicted, and it
keeps master's text as it is, master's own change to one of its entries included, with this
branch's entries placed among master's by their timestamps; git merged the state file by itself.
On `315fac2` the Release build has no error and the same 22 warnings, and 2,677 tests pass and
none fails (1,674 API, 499 bot, 154 web and 350 for the release-notes tool, with the same
seventeen skipped), the sixteen more being #209's. The squash commit's message is the pull
request's title and text, with the checklist's line on the suite brought up to date to that
count and to the `master` merged in last; the text on GitHub is left as it is.

Every check passed on `8507b5a`, the head pushed with that merge, but `master` moved again four
minutes later, to `11afb47` (pull request #211, records only, merged at 03:40:15Z), and the pull
request's records conflicted with it. On Joshua's "Try again" it is merged in as `5b83a53`, in
the same way; git merged the state file by itself. Outside the records the tree is that of
`315fac2`, so its result stands.
