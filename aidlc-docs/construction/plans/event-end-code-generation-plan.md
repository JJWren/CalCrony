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
A ("Approve, Q1 A"); code generated and checked the same day, and read by two review agents, who
found no fault in the change; `master` taken in at `f05e1d0` before the push (section 7). Next:
the agents' read of that merge, then the completion gate.

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
- [ ] 5.2 The completion gate: "Code generation complete. Choose: A) Request Changes  B) Continue
  to Next Stage".
- [x] 5.3 Before the push, `master` merged in: its records as they are, with this branch's audit
  entries placed among them by timestamp. Done at `f05e1d0` (section 7); again if `master` moves
  before the push.
- [ ] 5.4 The push and the pull request, on Joshua's go-ahead.
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
#205 and #206, records and documentation), and the review read the branch with that merged in.
While it read, `master` moved on to `f05e1d0`: pull request #200, the release of 0.37.2, and pull
request #197, which rewrites `SeriesProjection.cs` and `SeriesProjectionTests.cs` for the
free-slots lookup. Nothing had been pushed, so the branch is made again on `aeff22d` with one
merge of `master`, at `f05e1d0`, as `73fc5bf`, in place of the merge of `ee844fb`. The records
keep master's text as it is, with this branch's audit entries each placed among master's by its
timestamp and this state section after master's. The two files are #197's, with this change
made to them: the constant removed, `EndOf` returning `EventTimes.End`, and the summaries that
spoke of the default length naming `EventTimes.DefaultLengthMinutes`, three of them now with
#197's `SeriesSchedule`; the test class's summary, #197's, names `EndOf` as well, and the new
test sits before `Upcoming`'s. #197's own new code already takes an occurrence's end from
`EventTimes.End`. `PublicCalendarBuilder.cs` and `EventTimes.cs` are as on `master`. On the
merged tree the Release build has no error and the same 22 warnings, and 2,661 tests pass and
none fails (1,658 API, 499 bot, 154 web and 350 for the release-notes tool, with the same
seventeen skipped); with the documentation file turned on, every reference in the API resolves
and no warning names `SeriesProjection.cs`.

**Review.** Two review agents read the branch before any push, with `ee844fb` merged in, and the
draft text of the pull request: one for security and privacy, one for quality and accuracy.
Neither found a fault in the change. Between them, by reading and by runs of their own: `EndOf`
gives the end `EventTimes.End` gives; the builder, `EventTimes` and the calendar's route are as
on `master`; no route's answer changes; the references resolve; and the merge kept master's
records as they are, with this branch's entries by their timestamps. Both found that `master` had
moved on, answered above. The quality agent also found that the status did not say that
`master` had been taken in, answered there, and that the audit entry of 22:47:36Z gives `master`
on GitHub as `aeff22d` when pull request #203 had merged at 22:40:32Z, answered in the entry that
records this merge. Both read the new merge and its records next.
