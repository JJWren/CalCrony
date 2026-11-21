# The scheduler takes the rows it changes before it changes them, and the edit route's status answers — code-generation plan

**Task**: the follow-up on the scheduler's sweep that Joshua was asked about on 2026-11-17. Handed
over on 2026-11-20 by the session for the follow-up work on older code; the points were checked
under test, and Joshua chose what to include on 2026-11-20 ("Go with recommended
answers"): the sweep takes each row before it changes it, takes series rows before event rows,
and works out a series' next occurrence from the series as it stands, under a lock; the event
edit route refuses a status outside the four and answers 409 where it answered 500; and a test
holds the order in which the sweep takes its rows.
**Baseline**: `master` `bcb9a2e` (#216). The hosted instance runs 0.38.0. Release pull request
#213 is open for 0.39.0, with #210, #212 and #215.
**Branch**: `fix/sweep-claims`, cut from `master` → one pull request with a `fix:` title.
**Status**: written 2026-11-20 and approved by Joshua the same day as written, with Q1–Q3 all
answered A ("Approve, all A"). Built on 2026-11-20 (section 6) and read three times by the
review agents, the third time with nothing that holds up a push; the completion gate answered
"B) Continue" on 2026-11-21. Next: the push and the pull request, on Joshua's go-ahead.

A change inside existing components, so the workflow runs at minimal depth: the task is the
requirement; no user stories, application design, units, NFR or infrastructure stages; one unit,
and this file is its workflow plan and its code-generation plan, approved together.

## 1. The change

### 1.1 The sweep

`DeliveryScheduler.SweepAsync` runs every 15 seconds, in one transaction. From now on it works in
three steps.

1. **It finds what it may change**, reading without taking anything: reminders that are due;
   events that are due to start; started events whose length has passed; series that are running
   and have no upcoming occurrence, counting an occurrence the sweep is about to end as over;
   events whose RSVP cutoff has passed; and polls whose closing time has passed.
2. **It takes those rows** (`SELECT … ORDER BY "Id" FOR NO KEY UPDATE`), in one fixed order: the
   series first; then the events, among them every occurrence of those series; then the
   reminders; then the polls; each kind in id order. A request that changes one of those rows
   while the sweep holds it waits for the sweep, and one that came first goes first.
3. **It reads the rows again, now that it holds them, and works from what it reads then**, with
   the code it has today: a reminder is sent if it is still due, not yet sent, and its event is
   still scheduled; an event starts if it is still scheduled and due; an event ends if it is
   still started and its length, as it now stands, has passed; a series gets its next occurrence
   if it is still running and none of its occurrences is upcoming, made from the series as it now
   stands; a poll closes if it is still open and due. The posts, role changes, thread archives
   and DM reminders are built from what is read in this step.

The save then writes changes only to rows the sweep holds, and new rows. `SweepSave` keeps its
log line for a save that fails (`The sweep could not be saved. Rows named by the failure: …`),
also when the database driver hands the failure on inside another exception, and its two rules
for a save that failed on a row that had changed are removed. What stays of it moves into
`DeliveryScheduler` as a private method, and `SweepSave.cs` is deleted.

`SeriesMaterializer` does not change, but for its summary, which names `SweepSave`.

### 1.2 The event edit route

`PATCH /events/{id}`:

- A status that is not one of scheduled, started, ended and cancelled is refused with a 400:
  `Choose a status of scheduled, started, ended or cancelled.` (Q2)
- When the change would give a series a second upcoming occurrence, the route answers 409:
  `This series already has an upcoming occurrence.` (it answered 500).
- When the route gives way to another transaction that waits for the same rows, it answers 409:
  `This event changed at the same time — try again.` (it answered 500), the wording of the vote
  route's answer of the same kind.

Both 409s come from a wrapper around the route's body, so its many early returns stay as they are.

## 2. What does not change

- What the sweep sends, and when: the tests of the sweep in `SchedulingTests` pass unchanged.
- The skip, delete, stop and series-edit routes, and every other route.
- The bot and the web: neither sends a status in an edit.
- The database: no migration.
- The sweep's count of what it queued, which stays the number of deliveries it stores.

## 3. Questions

Each has a recommendation, the first option.

**Q1 — A line in the release notes.** A) None: nothing changes for a user to see or do, as on the
change for text that cannot be stored. B) One plain line.
[Answer]: A

**Q2 — The sentences of section 1.2.** A) As written. B) Other wording (give it).
[Answer]: A

**Q3 — Approval.** A) Approve this plan as written, with Q1 and Q2 as answered. B) Request changes.
[Answer]: A

## 4. Tests

Tests are written first and run red on today's code where the behaviour changes.

`tests/CalCrony.Api.Tests/SweepSaveTests.cs` is replaced by `SweepRowLockTests.cs`. Its tests
change rows between the sweep's first step and its second, through a command interceptor on the
sweep's own context, as the old ones changed rows before its save. Of the old sixteen:

| Old test | Now |
|---|---|
| A notification deleted while the sweep runs … | replaced: a reminder deleted before the sweep takes it is not sent, and the rest is saved at the first try |
| An event deleted while the sweep runs … | replaced: likewise for an event and its reminder |
| Many rows deleted … | replaced: seven reminders deleted, one save |
| Polls deleted while the sweep closes them … | replaced: likewise for polls |
| A series whose occurrence is deleted as the sweep ends it … | replaced: the series is as the delete left it, and nothing is posted for it |
| A series the sweep would have ended is not said to have ended … | replaced: kept, at the first save |
| A next occurrence is let go of when its series has a live occurrence again | replaced: that series gets no next occurrence, and the others do, at the first save |
| An occurrence the sweep starts counts as live for its series | removed: it changes a series by hand in the database, which no route does |
| A save is tried again as it is … | removed with the rule it pinned |
| Every next occurrence is let go of … | removed with the rule it pinned |
| The sweep's start of an occurrence is let go of … | replaced: an occurrence that is no longer scheduled when the sweep takes it is not started, and nothing is posted for it |
| An occurrence skipped as the sweep ends it … | replaced: the skip stands as it left the occurrence and the series |
| A second failure with no next occurrence left … | removed with the rule it pinned |
| A third failure on a live occurrence … | removed with the rule it pinned |
| The occurrences the sweep looked at are held until it is saved | replaced: the occurrences of a series the sweep takes are held until it is saved |
| The rows that are still there are held until the sweep is saved | replaced: every kind of row the sweep takes is held until it is saved |

New in `SweepRowLockTests.cs`:
- The sweep takes a series before its occurrences: while it waits for a series row that another
  transaction holds, it holds none of that series' events, and once that transaction ends the
  sweep is saved.
- The sweep takes an event before its reminders: while it waits for an event row held
  elsewhere, it holds none of that event's reminders.

New in `EventApiTests.cs` or a class of its own:
- A status outside the four is refused with the 400, and nothing is stored.
- Setting an occurrence back to scheduled while another occurrence of its series is upcoming
  answers the 409, and nothing is stored.
- An edit that gives way in a wait for rows answers the 409 (two transactions made to wait on
  each other in the test).

`SchedulingTests` stays as it is, `A_sweep_that_cannot_be_saved_names_the_rows_in_the_log` among
them.

## 5. Steps

### 5.0 Baseline
- [x] Release build of `CalCrony.slnx` (0 errors; note the warnings) and the whole suite on
  `bcb9a2e`, in this worktree.

### 5.1 Tests
- [x] `SweepRowLockTests.cs` and the edit-route tests of section 4; run them on today's code and
  note which fail.

### 5.2 Code
- [x] `DeliveryScheduler.SweepAsync` in the three steps of 1.1; the save's failure log as a
  private method; `SweepSave.cs` deleted; `SeriesMaterializer`'s summary.
- [x] `EventEndpoints.UpdateEvent`: the status check and the two 409s of 1.2.
- [x] Delete `SweepSaveTests.cs`.

### 5.3 Checks
- [x] The whole suite green; Release build with no new warning.
- [x] Rule removals: each of these taken out on its own fails a test named here: the series
  lock; the event lock; the reminder lock; the poll lock; the order of series before events, and
  of events before reminders; each re-check of step 3 (reminder, start, end, series running,
  upcoming occurrence, poll); the next occurrence made from the series read under the lock; the
  status check; each 409.
- [x] The release-summary check, if Q1 is B: not needed, Q1 is A.

### 5.4 Records
- [x] This plan's section 6 (as built), the state file, the audit log.

### 5.5 Review before any push
- [x] Two review agents, one for security and one for code quality, read the commit; their
  findings are answered in a commit they read again. The pushed head is a commit they read.
- [x] The completion gate with Joshua. The push, the pull request, the merge, the release and the
  rollout each wait for his own go-ahead.

## 6. As built

Built on 2026-11-20 on `fix/sweep-claims`, tests first, as section 5 sets out; `master` stayed at
`bcb9a2e` throughout.

**Baseline** (`bcb9a2e`): a full Release build of `CalCrony.slnx` with no error and 21 warnings;
the whole suite green: 2,835 tests (1,765 API, 562 bot, 157 web, 351 for the release-notes tool,
seventeen of whose tests are skipped on this machine and run in CI).

**Code**
- `DeliveryScheduler`: `SweepAsync` in the three steps of 1.1. `FindAsync` reads what the sweep
  may change without tracking anything. `TakeAsync` takes it with four statements of the form
  `SELECT "Id" … ORDER BY "Id" FOR NO KEY UPDATE`, each left out when it has nothing to take. The
  third step, in `SweepAsync`, reads the rows again with the re-checks of 1.1 and then does what
  the sweep did before; the RSVP-cutoff claims are as they were, on events the sweep now holds.
  `SaveAsync` keeps the failure log line, and writes it also when the database provider passes the
  failure on inside an `InvalidOperationException` of its own. `SweepSave.cs` is deleted.
- `SeriesMaterializer`: its summary.
- `EventEndpoints`: `UpdateEvent` wraps `UpdateEventCoreAsync`, which is the route as it was, with
  the status check of 1.2 before the length checks. The wrapper answers the two 409s, and finds a
  deadlock through the exceptions the provider wraps it in.

**Tests**
- `SweepRowLockTests` (23 tests) replaces `SweepSaveTests` as the table of section 4 says, with
  ten more written during the build and its review: an event cancelled, or moved later, before the
  sweep takes it is not started; a started event moved later is not ended; a reminder of an event
  cancelled, or moved later, is not sent; a series stopped before the sweep takes it gets no next
  occurrence; a change to a whole series made before the sweep takes it reaches the next
  occurrence; a poll closed by hand keeps its closing time; a save that fails inside an exception
  of the provider's names the rows in the log; and rows made due after the sweep has found its rows
  are left to the next sweep.
- `EventStatusEditTests` (10 cases): the three of section 4, a case for each of the four statuses,
  which are stored, and an edit that gives way as it saves.
- On the code before the change, 28 of these 33 fail; the other 5 check what the old code already
  did.

**Checks**, on the change
- A full Release build with no error and the same 21 warnings.
- The whole suite green: 2,852 tests (1,782 API, 562 bot, 157 web, 351 for the release-notes
  tool; seventeen of the tool's tests skipped on this machine).
- Rule removals, each made on its own in a checkout of its own, with `SweepRowLockTests`,
  `EventStatusEditTests` and `SchedulingTests` run: each fails at least one test.

| Taken out | Fails |
|---|---|
| The series lock | `Every_kind_of_row_the_sweep_takes_is_held_until_it_is_saved`, `The_sweep_takes_a_series_before_its_occurrences` |
| The event lock | `A_started_event_moved_later_before_the_sweep_takes_it_is_not_ended`, `An_event_cancelled_before_the_sweep_takes_it_is_not_started` and 6 more |
| The reminder lock | `Every_kind_of_row_the_sweep_takes_is_held_until_it_is_saved` |
| The poll lock | `A_poll_closed_before_the_sweep_takes_it_keeps_its_closing_time`, `Every_kind_of_row_the_sweep_takes_is_held_until_it_is_saved` |
| Series taken before events | `The_sweep_takes_a_series_before_its_occurrences` |
| Events taken before reminders | `The_sweep_takes_an_event_before_its_reminders` |
| A reminder: its event still scheduled | `A_reminder_of_an_event_cancelled_before_the_sweep_takes_it_is_not_sent` |
| A reminder: still due | `A_reminder_of_an_event_moved_later_before_the_sweep_takes_it_is_not_sent` |
| A start: still scheduled | `An_event_cancelled_before_the_sweep_takes_it_is_not_started`, `An_occurrence_no_longer_scheduled_when_the_sweep_takes_it_is_not_started` |
| A start: still due | `An_event_moved_later_before_the_sweep_takes_it_is_not_started` |
| An end: still started | `An_occurrence_skipped_before_the_sweep_takes_it_stays_as_the_skip_left_it` |
| An end: its length still passed | `A_started_event_moved_later_before_the_sweep_takes_it_is_not_ended` |
| A series: still running | `A_series_the_sweep_would_have_ended_is_not_said_to_have_ended_when_its_occurrence_was_deleted` |
| A series: no live occurrence | `A_series_whose_occurrence_is_set_back_to_scheduled_before_the_sweep_takes_it_gets_no_next_occurrence`, `An_occurrence_skipped_before_the_sweep_takes_it_stays_as_the_skip_left_it` |
| The series read under the lock | `A_change_to_a_whole_series_before_the_sweep_takes_it_reaches_the_next_occurrence` |
| A poll: still open | `A_poll_closed_before_the_sweep_takes_it_keeps_its_closing_time` |
| The occurrences of a series taken with it | `The_occurrences_of_a_series_the_sweep_takes_are_held_until_it_is_saved` |
| The failure log for a failure passed on inside another exception | `A_save_that_fails_inside_an_exception_of_the_providers_names_the_rows_in_the_log` |
| The status check | `A_status_outside_the_four_is_refused_and_nothing_is_stored` |
| The 409 for a second live occurrence | `Setting_an_occurrence_back_to_scheduled_while_another_is_upcoming_answers_409` |
| The 409 for a deadlock | `An_edit_that_gives_way_as_it_saves_answers_409`, `An_edit_that_gives_way_in_a_wait_for_rows_answers_409` |
| A start: looked for only among the events taken | `Rows_made_due_after_the_sweep_has_found_its_rows_are_left_to_the_next_sweep` |
| A reminder: looked for only among the reminders taken | `Rows_made_due_after_the_sweep_has_found_its_rows_are_left_to_the_next_sweep` |
| An end: looked for only among the events taken | `Rows_made_due_after_the_sweep_has_found_its_rows_are_left_to_the_next_sweep` |
| A poll: looked for only among the polls taken | `Rows_made_due_after_the_sweep_has_found_its_rows_are_left_to_the_next_sweep` |
| The event of a due reminder taken | `Every_kind_of_row_the_sweep_takes_is_held_until_it_is_saved` |
| A deadlock found inside the exceptions around it | `An_edit_that_gives_way_as_it_saves_answers_409` |
| The cutoffs read without tracking | `A_started_event_moved_later_before_the_sweep_takes_it_is_not_ended` |

**Review, round 1**, by two agents, one for security and one for code quality, on `d0d23c8`:
nothing in the code held up a push. Answered in the commit that replaces it: a test that rows made
due after the sweep has found its rows are left to the next sweep; the event of the reminder, and
a series, among the rows held in the test of every kind of row; a passed RSVP cutoff and a posted
message in the test of a started event moved later; posted messages for the polls of the test of
deleted polls, so that what it says can be seen; the count of what the sweep queued in the test of
many reminders; in both deadlock tests the other transaction waits ten seconds before the database
looks for a cycle, so that the edit is always the one stopped; a second deadlock test, met as the
edit saves; the comment on the order in which the series are advanced; the overload of
`SaveFailure.Describe` that takes rows made private; and this plan's steps ticked. The wording of
the plan's opening and of step 2 of 1.1, and of four earlier entries in the audit log, was
changed at the review, before anything was pushed; what was asked, answered and approved is
unchanged.

**Review, rounds 2 and 3**, by the same two agents. Round 2, on `c406000`, the commit that
answered round 1: nothing in the code held up a push. Answered in `b01d370`, which replaces it:
the example of what `SaveFailure.Describe` returns moved to the overload that other code calls,
and the wording of the records. Round 3, on `b01d370`: nothing holds up a push.

**Completion gate**, 2026-11-21: Joshua answered "B) Continue". The push and the pull request are
asked next, as their own go-ahead; the merge, the release and the rollout each wait for theirs.
