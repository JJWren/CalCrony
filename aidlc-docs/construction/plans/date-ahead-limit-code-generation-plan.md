# Date limit — how far ahead a date can be set: code-generation plan

**Change**: a limit on how far ahead a date and time typed in words may lie, and date arithmetic in
the API that stays inside the calendar.
**Baseline**: `master` 56b61ab, whose code is release 0.37.0 (1,292 tests green there — 721 API,
418 bot, 153 web). **Branch**: `fix/date-ahead-limit` → PR `fix: limit how far ahead a date can be set`.
**Status**: built on 2026-11-16 from the proposal Joshua agreed to that day ("Yes, as recommended"),
which set the limit at ten years. He chose to read this written plan together with the code, at
the completion gate, before any push.

## 1. What this change ships

1. A date and time typed in words may lie at most ten years ahead
   (`DateTextLimits.MaxYearsAhead`, in Contracts). A later date is refused with its reason. Every
   place that takes a date and a time in words reads it through one method, so the one check
   covers them all: an event's start, new or edited; the date a repeat ends, new or edited; an
   RSVP cutoff given as a time, new or edited; a reminder; a poll's closing time; a time poll's
   slots, when the poll is made and when one is added; and the parse route, through which
   `/timestamp` and `/availability role` read their time. A time poll's winning time, which was
   read when the poll was made, is held to the same limit when it becomes an event.
2. The repeat calculation answers "no next occurrence" where a schedule's next date would lie past
   the last day the calendar has (the end of the year 9999).
3. The scheduler takes each repeating event on its own: one it cannot advance is logged and passed
   over. It also says in its log when a series ends.
4. An event's end is worked out in one place (`EventTimes`) and is never past the last moment the
   calendar has. The calendar feed, the event availability route and the scheduler use it.

The change is in the API, with one constant in Contracts. No migration, no configuration, nothing
new stored. The bot and the web app are not changed: they show the API's reason as they show its
other refusals.

## 2. Carried in

| From | Decision |
|---|---|
| Joshua, 2026-11-16 | The limit is ten years. |
| Joshua, 2026-11-16 | The change is made on its own, with its own pull request, release and rollout. |
| Joshua, 2026-11-16, at the completion gate | The branch is pushed together with a second change, which is built first, so that one release and one rollout carry both. |
| Joshua, 2026-11-16 | Building starts from the proposal; the written plan is shown with the code at the completion gate, before any push. |
| Standing practice | Two review agents, one on security and one on code quality, read each commit before it is pushed, and the pushed head stays the commit they read. The push, the pull request, the merge, the release and the rollout each wait for Joshua's go-ahead. |
| Joshua, 2026-11-18, after the push | The desktop app's Auto-fix is on for the two pull requests: what it reports on them (a failed check, a conflict with `master`, a review comment) is answered, and a fix pushed to the branch, without asking him first. The two review agents still read each fix before it is pushed. |

## 3. Rules

### 3.1 The limit

1. The last day allowed is today plus `MaxYearsAhead` years, where today is the date in the zone
   the text is read in: the caller's zone, or the zone that a zone abbreviation in the text
   selects. (Ten years after a Feb 29 is Feb 28.)
2. A reading of the text is judged by its local date. A time on the last day allowed is read,
   whatever the time; anything on a later day is not.
3. A reading on a later day is left out before it is turned into an instant.
4. The earliest reading that is still ahead wins, as it always has. Text that names a date inside
   the limit and one past it is read as before.
5. When nothing in the text is both ahead and inside the limit, and the text named a day past the
   limit, the refusal is `"<text>" is more than 10 years away; dates can be set up to 10 years
   ahead.` — also where another date in the text has passed.
6. Text inside the limit is read exactly as before.
7. The first day of `/availability slots` is read by `TryResolveDay`, which works in whole days
   and is not changed: its one caller already limits the first day to a year ahead
   (`FreeSlotLimits.MaxDaysAhead`), with wording of its own.
8. A time poll's slots are read when the poll is made, and the winning one becomes an event's
   start later, without being read again. Converting the poll holds that time to the same last
   day, counted in the poll's zone (`NaturalDateTimeParser.IsWithinAheadLimit`): a winning time
   past it is refused with a 409, `The winning time is more than 10 years away; dates can be set
   up to 10 years ahead.`, beside the refusal that a winning time which has passed already gets.
   The parser and this check write that sentence in one place (`TooFarAheadError`). The zone a
   slot was typed in and the poll's zone can count the last day differently: a slot typed with a
   zone abbreviation, late on the last day that zone allows, can lie past the last day in the
   poll's own zone, and such a poll is then not converted until the next day has begun there.

### 3.2 The repeat calculation

1. `RecurrenceCalculator.NextOccurrence` answers "none" when the next date, or the instant of its
   start in the series' zone, lies outside the calendar. "None" is the answer it already gives
   when an end date has been reached, and every caller already handles it: the scheduler and the
   skip route end the series, the series edit refuses the settings, and the feed and the public
   calendar project nothing.
2. `NextDate` behaves as before; its summary now says where it throws.

### 3.3 The scheduler

1. A series that cannot be advanced — working out or building its next occurrence fails — is
   logged as an error with its id and its server's id, and passed over. It is left exactly as it
   was and is tried again on the next sweep.
2. A series that ends in a sweep, because it has no next occurrence, is named in the log with its
   server's id, once the sweep is saved.
3. A sweep reads the series in id order, so it takes them in the same order every time.
4. The scheduler's end-of-event check uses `EventTimes.End`.
5. Nothing else in the sweep changes.

### 3.4 An event's end

1. `EventTimes.End(start, minutes)` is the start plus the length; the default length, 60 minutes,
   when none is set; and the last moment there is when the sum would lie past it. It has two
   forms: an instant, and a local date and time for output written in a zone's wall time.
2. It is used by the calendar feed (an event's end, the end of a series' live occurrence, and a
   series entry's end in UTC and in the series' zone), by the event availability route (the end of
   the window) and by the scheduler (3.3).
3. In the feed, the `UNTIL` of a series whose end date is the calendar's last day is the last
   instant there is.

## 4. What changes, file by file

| File | Change |
|---|---|
| `src/CalCrony.Contracts/Events.cs` | `DateTextLimits.MaxYearsAhead` (10); the class summary says which limit the bot declares too. |
| `src/CalCrony.Api/Services/NaturalDateTimeParser.cs` | `TryResolve` applies 3.1. `ResolveValue` hands back local date-times, so each reading can be judged by its date before it is turned into an instant. `IsWithinAheadLimit` judges a stored instant by the same last day (3.1 rule 8). `TryResolveDay` and the class summary are not touched. |
| `src/CalCrony.Api/Endpoints/PollEndpoints.cs` | Converting a poll applies 3.1 rule 8. |
| `src/CalCrony.Api/Services/RecurrenceCalculator.cs` | `NextOccurrence` applies 3.2; the search itself moves into a private method unchanged. |
| `src/CalCrony.Api/Services/EventTimes.cs` (new) | 3.4, and the one constant for the default length. |
| `src/CalCrony.Api/Services/DeliveryScheduler.cs` | 3.3. Its own constant for the default length goes. |
| `src/CalCrony.Api/Services/SeriesMaterializer.cs` | Summary only. |
| `src/CalCrony.Api/Services/IcsRecurrence.cs` | 3.4 rule 3. |
| `src/CalCrony.Api/Endpoints/FeedEndpoints.cs`, `CalendarEndpoints.cs` | 3.4 rule 2. |
| `tests/CalCrony.Api.Tests/DateAheadLimitTests.cs` (new) | 3.1 at the parser, to the day, and `IsWithinAheadLimit`, to the minute. |
| `tests/CalCrony.Api.Tests/DateAheadLimitApiTests.cs` (new) | 3.1 at every route that reads date text, and at the conversion of a poll. |
| `tests/CalCrony.Api.Tests/EventTimesTests.cs` (new) | 3.4 rule 1. |
| `tests/CalCrony.Api.Tests/RecurrenceCalculatorTests.cs`, `IcsRecurrenceTests.cs` | 3.2; 3.4 rule 3. |
| `tests/CalCrony.Api.Tests/SchedulingTests.cs` | 3.3 rules 1 and 2, with the log read; and a started event ending by its own length (rule 4). |
| `tests/CalCrony.Api.Tests/FeedTests.cs`, `IcsText.cs` | 3.4 rule 2 in the feed: every entry ends its length after its start, and a series is projected from the end of its live occurrence. `IcsText` gains a reader for an entry's length. |
| `tests/CalCrony.Api.Tests/EventAvailabilityTests.cs` | 3.4 rule 2 at the event availability route: the window ends the event's own length after its start. |
| `docs/release-notes/0.37.2.md` (new) | The release summary for this change, which CONTRIBUTING's "Release summary" asks of a change users will notice; the file of the next release, since 0.37.1 went out without this change (step 5.8). |

Another change in progress, on a branch of its own, edits the same parser class. So that the two
merge cleanly whichever lands second, this one keeps away from the lines that hand the text to
the recognizer and from the class summary, and its parser tests are in a file of their own.

## 5. Execution plan

- [x] 0.1 Branch `fix/date-ahead-limit`, on `master` 56b61ab; since step 5.4 on `4af1305`,
      since step 5.5 on `ab32e96`, since step 5.6 on `916ecd1`, since step 5.7 on `723696d`,
      and since step 5.8 on `162fd42`, with `04beae8` merged in at step 5.9.
- [x] 1.1 `DateTextLimits.MaxYearsAhead`, documented in the file's style.
- [x] 1.2 `TryResolve` (3.1). `DateAheadLimitTests`: a time on the last day allowed is read and the
      same time a day later is refused, with the refusal's exact words; relative text that lands
      on the last day is read; ten years after a Feb 29 the last day is Feb 28; a date past the
      limit is refused however it is written; the limit counts from today in the zone the text is
      read in, and a zone abbreviation in the text decides which today; a date inside the limit
      is read although the text also names one past it; a date past the limit beside one that has
      passed is refused as too far ahead; a time on the calendar's last day is refused as too far
      ahead in a zone at, behind and ahead of UTC.
- [x] 1.3 `DateAheadLimitApiTests`: every route that reads date text reads text inside the limit
      (nine years out, or eight where a later date has to follow it or nine is already taken)
      and turns the refusal of text a year past the limit into a 400 that carries the reason — the
      parse route; a new event's start; a repeat's end on create and on a series edit; an edited
      start (refused first, with the event left as it was); an RSVP cutoff on create and on
      edit; a reminder; a poll's closing time; and a time poll's slot, when the poll is made and
      when one is added.
- [x] 1.4 The winning time of a poll (3.1 rule 8). `DateAheadLimitTests`: a stored time is inside
      the limit through the last minute of the last day allowed and outside it a minute later,
      the zone deciding which day is the last; the same where the zone skips the following day
      whole; the last instant there is lies outside it in every zone. `DateAheadLimitApiTests`: a poll whose winning time is stored a year past the limit is
      not converted, and with the time a year inside it is.
- [x] 2.1 `NextOccurrence` (3.2). `RecurrenceCalculatorTests`: no next occurrence on the calendar's
      last day for every kind of rule; none within one step of it; a start on the last day is an
      occurrence only where its zone has that instant; none when asked from the calendar's last
      instant, in a zone at, behind and ahead of UTC.
- [x] 3.1 `EventTimes` (3.4 rule 1). `EventTimesTests`: the plain sum; the default length; the
      longest event; an end that reaches the calendar's last minute is still the plain sum; an end
      past the last moment is that moment — each in both forms.
- [x] 3.2 The feed, the event availability route and the scheduler use it (3.4 rule 2); the feed's
      `UNTIL` (rule 3), with a test in `IcsRecurrenceTests` either side of the last day.
      `FeedTests`, which had no test of an entry's end: an event with a length, one with none and
      a series each end their length after their start, for a server in UTC and one in another
      zone; a series whose stored zone is not known, which the feed writes in UTC on a path of
      its own; and a daily series whose live occurrence runs two and a half days is projected
      from the third day, the first slot after that occurrence ends. `EventAvailabilityTests`: the
      window of an event with a length of its own.
- [x] 4.1 The scheduler passes over a series it cannot advance (3.3 rule 1), names in its log a
      series that ends (rule 2) and reads the series in id order (rule 3). `SchedulingTests`: of
      two series due their next occurrence, the one that comes first in id order is given a rule
      that nothing can step through; it is logged once as an error naming it and its server and
      is left as it was, the other series gets its occurrence and an event that was due starts,
      and with its rule put right the next sweep advances it. A series that ends in a sweep is
      named in the log once. A started event with a length of ninety minutes is still running
      after sixty and has ended after ninety (rule 4).
- [x] 5.1 `dotnet build CalCrony.slnx -c Release` with no new warnings; `dotnet test CalCrony.slnx
      -c Release` green. Result (2026-11-16, with both review rounds answered and `master` taken
      in): 0 errors and 22 warnings, the number `master` had at 56b61ab; 1,693 tests green — 783
      API, 418 bot, 153 web and 339 for the release-notes tool, which came in with `master` — and
      17 of that tool's tests skipped, which this machine cannot run. The change adds 62 to the
      API's 721, and its release summary one case to the tool's.
- [x] 5.2 Each rule taken out in turn, to see which tests fail (section 7 has the table, and what
      no test pins).
- [x] 5.3 The whole diff read against section 3, rule by rule; `aidlc-state.md` and `audit.md`
      brought up to date; this plan's boxes ticked.
- [x] 5.4 `master` taken in at `4af1305` (pull request #179), which brings the release summaries:
      `docs/release-notes/0.37.1.md` written for this change — with 0.37.0 the last release and
      no release pull request open, a `fix:` makes 0.37.1 — and passed by `dotnet run --project
      tools/ReleaseNotes -- check`.
- [x] 5.5 `master` taken in again at `ab32e96` (pull request #181), after the completion gate.
      Result on that tree: 0 errors and 22 warnings; 1,699 tests green — 783 API, 418 bot, 153
      web and 345 for the release-notes tool, six of them from that pull request — and 17 of
      the tool's skipped, as before.
- [x] 5.6 `master` taken in a third time at `916ecd1` (pull requests #180, #183, #182 and
      #186), after the completion gate. #180 changes `NaturalDateTimeParser`, where the limit
      lives; git merges the two without a conflict, and two test cases are added where they
      meet (section 7). Result on that tree: 0 errors and 22 warnings; 1,807 tests green — 891
      API, 418 bot, 153 web and 345 for the release-notes tool — and 17 of the tool's
      skipped, as before. The change adds 64 to the API's tests.
- [x] 5.7 `master` taken in a fourth time at `723696d` (pull request #184), the same day. #184
      adds refusals to `TryResolve` and `TryResolveDay` that come before the recognizer reads
      the text, and so before this change's lines; git merges it without a conflict, and this
      change's lines in `NaturalDateTimeParser` are as they were. Result on that tree: 0 errors
      and 22 warnings; 1,845 tests green — 929 API, 418 bot, 153 web and 345 for the
      release-notes tool — and 17 of the tool's skipped.
- [x] 5.8 `master` taken in a fifth time at `162fd42`, the same day: 0.37.1 was released
      without this change (pull request #185), and #188 and #187 followed (records,
      `CONTRIBUTING.md`, CI, and the release-notes tool's own tests). The summary moves to
      the next release's file, `docs/release-notes/0.37.2.md`, which this change now starts;
      `0.37.1.md` stays as 0.37.1 shipped it. No code under `src` differs from step 5.7's,
      and no test this change adds or changes. Result on that tree: 0 errors and 22
      warnings; 1,849 tests green — 929 API, 418 bot, 153 web and 349 for the release-notes
      tool — and 17 of the tool's skipped.
- [x] 5.9 `master` merged in at `04beae8` (pull request #191, records only) on 2026-11-18, after the
      push, when GitHub reported that the pull request conflicts with it. The branch is on GitHub,
      so `master` is merged in rather than put under the change again. Only `aidlc-docs/audit.md`
      conflicts: it is `master`'s entries with this change's put in by their timestamps. Nothing
      outside `aidlc-docs` differs from the tree of step 5.8, so its result stands for this one.

## 6. Delivery

- [x] 6.1 Both review agents read each commit before any push; what they find is answered in a
      commit they read again.
- [x] 6.2 *Go-ahead:* push the branch and open the pull request, at the commit the agents passed.
      Pushed on 2026-11-18, on Joshua's go-ahead of 2026-11-17 ("A, push both"), together with the
      second change, at `6620517`, and opened as pull request #189 into `master`.
- [ ] 6.3 *Go-ahead:* squash merge. The test stack picks up `:main`.
- [ ] 6.4 *Go-ahead:* the release (a patch release, opened by release-please).
- [ ] 6.5 *Go-ahead:* prod — a backup first, then the pinned tag, app services only.

## 7. Notes from generation

### Where the code differs from the proposal

- The proposal had the limit in `TryResolve` and in `TryResolveDay`. `TryResolveDay` is left as it
  is (3.1 rule 7): the only place that calls it already holds the first day to a year ahead and
  says so in its own words, which the ten-year refusal would have replaced for dates further out.
- The proposal named three sums on an event's start. Read through, the same sum was written in
  six places — four in the feed, one in the event availability route and one in the scheduler —
  and all six now go through `EventTimes`. The feed's `UNTIL` conversion (3.4 rule 3) was not in
  the proposal.
- 3.1 rule 8 and 3.3 rules 2 and 3 were not in the proposal; they came out of the first review
  round.

### Tests added

64, all in `CalCrony.Api.Tests`: `DateAheadLimitTests` 25, `DateAheadLimitApiTests` 11,
`EventTimesTests` 5, `RecurrenceCalculatorTests` 14, `IcsRecurrenceTests` 1, `SchedulingTests` 3,
`FeedTests` 4, `EventAvailabilityTests` 1. Two of the 25 came on 2026-11-17, when `master` was
taken in a third time (below).

### Rules taken out, one at a time (2026-11-16)

Each change below was made on its own to the built code, and the code was put back after it.
Each time the same eight test classes were run: the two limit classes, `EventTimesTests`,
`RecurrenceCalculatorTests`, `IcsRecurrenceTests`, `SchedulingTests`, `FeedTests` and
`EventAvailabilityTests` — 129 tests, all green with nothing taken out. Twenty-six changes in
twenty-eight runs, on the code as it stands after review round 2: one of them was run three
times, because its test once depended on the order in which two rows were read. Every change
failed the tests named, and no others.

| Rule taken out | Tests that failed |
|---|---|
| 3.1 — a later day is no longer left out | 23: the ten route tests that read date text, and every refusal in `DateAheadLimitTests` (13 cases) |
| 3.1 rule 2 — the last day allowed is refused too | 4: `Reads_a_time_on_the_last_day_allowed`, `Reads_relative_text_that_lands_on_the_last_day_allowed`, `The_limit_counts_from_today_in_the_zone_the_text_is_read_in`, `Ten_years_after_a_feb_29_the_last_day_allowed_is_feb_28` |
| 3.1 rule 1 — today taken in UTC instead of the zone the text is read in | 1: `The_limit_counts_from_today_in_the_zone_the_text_is_read_in` |
| 3.1 rule 1 — the last day worked out without the step back from a Feb 29 | 1: `Ten_years_after_a_feb_29_the_last_day_allowed_is_feb_28` |
| 3.1 rule 5 — no reason given when nothing inside the limit was named | 22: the first row's, without the date beside one that has passed |
| 3.1 rule 5 — the reason for a passed date given instead | 1: `A_date_past_the_limit_beside_one_that_has_passed_is_refused_as_too_far_ahead` |
| 3.1 rule 8 — a poll is converted whatever its winning time | 1: `Convert_poll_refuses_a_winning_time_past_the_limit_and_takes_one_inside_it` |
| 3.1 rule 8 — a stored time's last day ends a day early | 2: `A_stored_time_is_inside_the_limit_through_the_end_of_the_last_day_allowed`, `A_day_that_a_zone_skips_whole_ends_the_last_day_allowed_like_any_other` |
| 3.1 rule 8 — for a stored time, today taken in UTC | 1: `A_stored_time_is_inside_the_limit_through_the_end_of_the_last_day_allowed` |
| 3.1 rule 8 — the end of the last day read strictly, not leniently | 1: `A_day_that_a_zone_skips_whole_ends_the_last_day_allowed_like_any_other` |
| 3.2 — `NextOccurrence` no longer answers "none" at the calendar's end: taken out whole, and in each of its two halves (three runs) | 14, 6 and 8: every case of the four new tests in `RecurrenceCalculatorTests` fails in the first run and in one of the other two |
| 3.3 rule 1 — the scheduler does not pass over | 1: `A_series_that_cannot_be_advanced_is_logged_and_passed_over` |
| 3.3 rule 1 — `break` where the code has `continue` (three runs) | 1 each time: the same test |
| 3.3 rule 1 — the failure is logged below an error | 1: the same test |
| 3.3 rule 2 — a series that ends is not named in the log | 1: `A_series_that_ends_in_a_sweep_is_named_in_the_log` |
| 3.3 rule 4 — the scheduler leaves out the event's own length | 1: `A_started_event_ends_once_its_own_length_has_passed` |
| 3.4 rule 1 — the end is the plain sum, in both forms at once | 1: `An_end_past_the_calendars_last_moment_is_that_last_moment` |
| 3.4 rule 1 — the default length is another number | 4: `An_event_with_no_length_set_runs_an_hour`, both cases of `Every_entry_ends_its_length_after_its_start`, and `Grid_covers_going_members_only_with_event_window`, which was there before |
| 3.4 rule 3 — the `UNTIL` of the last day converted like any other | 1: `Until_on_the_calendars_last_day_is_the_last_instant_there_is` |
| 3.4 rule 2 — the feed's entry for an event leaves out the event's length | 2: both cases of `Every_entry_ends_its_length_after_its_start` |
| 3.4 rule 2 — the same for a series entry written in its zone | 2: both cases of the same test |
| 3.4 rule 2 — the same for a series entry whose zone is not known | 1: `A_series_whose_stored_zone_is_not_known_is_written_in_utc_and_ends_its_length_after_its_start` |
| 3.4 rule 2 — the same for the end of a series' live occurrence | 1: `A_series_is_projected_from_the_end_of_its_live_occurrence_by_that_occurrences_own_length` |
| 3.4 rule 2 — the same at the event availability route | 1: `The_window_ends_the_events_own_length_after_its_start` |

What no test pins:

- 3.4 rule 2 against the sum it replaced. With ordinary dates `EventTimes.End` and that sum agree,
  so the tests above show each of the six places handing on the event's own length, and
  `EventTimesTests` shows where the two differ.
- 3.3 rule 3. The database gave the series in id order before the code asked for it, so no test
  fails without the line; `A_series_that_cannot_be_advanced_is_logged_and_passed_over` relies on
  the order.
- The zone in which converting a poll judges the winning time (3.1 rule 8): a year either side of
  the limit cannot tell one zone from another. `IsWithinAheadLimit`'s own test pins the zone.
- "Once the sweep is saved" in 3.3 rule 2. The tests' sweeps all save, so none tells the line
  written after the save from the same line written before it.

### Review round 1 (2026-11-16)

Both agents read the change before any push, each in a copy of its own in which it built and ran
it.

- Security found no way left for a date typed in words to be stored past the limit. It asked for
  3.1 rule 8 and suggested 3.3 rule 2; both are in.
- Quality found the code to do what section 3 says for every caller, and nothing that section 3
  leaves out. Its findings were in the tests and the records. The scheduler test depended on the
  order in which the two series were read: hence 3.3 rule 3, and a test that gives the same
  result every time. Not pinned: the step back from a Feb 29, the scheduler's log line, and
  `EventTimes.End` at its six call sites. The first two have a test now, and so has each call
  site for the event's own length; that no test tells `EventTimes.End` there from the sum it
  replaced is as it was, and is listed above under what no test pins. Without a case of their own: an RSVP cutoff on edit and a slot
  added to a poll; and without a case inside the limit: an edited start, a series edit and a
  poll's closing time and slots; all added. Results expected years ahead were written as UTC
  times and are now worked out from the zone. Several statements in this plan and in the audit
  entries were not exact, and are corrected.

### Review round 2 (2026-11-16)

Both agents read the commit that answered round 1.

- Security found nothing in the new code that should hold up a push. Two things from its read are
  changed. `IsWithinAheadLimit` read the end of the last day in a way that fails for a day a zone
  skips whole; it reads it leniently now, with a test (3.1 rule 8). And the log line for a series
  that ends was written before the sweep was saved; it is written after (3.3 rule 2).
- Quality found nothing in the code. It ran the scheduler test sixteen times with `break` in
  place of `continue`, and it failed every time; ran the four test classes whose tests share a
  database in other orders; checked the id order against the database; and confirmed the counts and the table above. From
  its list: round 1's record above now says which part of its first finding is as it was; the
  refusal's sentence is written in one place (`TooFarAheadError`); the two log assertions match
  the server's id as the line writes it; the feed test for the length of a live occurrence uses
  two and a half days, so that no slot falls on the moment the occurrence ends; and smaller
  corrections to this plan.

### `master` taken in (2026-11-16 to 2026-11-18)

`master` had moved from 56b61ab to `4af1305` (pull request #179). It is taken in, and the change
is one commit on top of it. Both sides add to `aidlc-docs/audit.md` and
`aidlc-docs/aidlc-state.md`: the audit entries are in the order of their timestamps, and both
state sections are kept. Nothing under `src` or in the three older test projects differs between
56b61ab and `4af1305`. Step 5.4 has what that pull request asks of this change.

`master` then moved to `ab32e96` (pull request #181: the release workflow's permissions, tests
for them in the release-notes tool's project, and their records). It is taken in the same way,
and the change is again one commit on top of it: the audit entries of both sides in the order
of their timestamps, and the state file's new line kept beside this change's section. Nothing
under `src` or in the three older test projects differs between `4af1305` and `ab32e96`. Step
5.5 has the result.

Another pull request that is open, #180, adds `docs/release-notes/0.37.1.md` as well, and both
are `fix:` changes. Whichever of the two is merged second takes `master` in again. If a release
has gone out in between, it moves its entry to the next version's file, since a summary is read
at the release's tag; if none has, the two share 0.37.1 and it keeps both entries in the one
file, both bullets under `### What's new`. Since Joshua's answer at the completion gate this
change shares its release with a second change (section 2), whose branch stands on this one
and adds a line of its own to the same file. Which pull request is merged when is put to Joshua
with the question about the merge.

On 2026-11-17 `master` moved to `916ecd1`, by four pull requests: #180 (`sat` and `sun` in date
text), #183 (a job that lints the workflow files) and #182 (weekly day sets on the public
calendar), each with its tests and records, and #186 (records only). It is taken in a third
time, after the completion gate, and the change is again one commit on top of it. No release
has gone out in between, so `docs/release-notes/0.37.1.md` holds every entry: this change's
bullet after the two `master` has. The audit entries of both sides are in the order of their
timestamps, and the state file keeps every section.

This time code under `src` differs. #180 changes `NaturalDateTimeParser`, where the limit lives:
`TryResolve` hands the recognizer the text with `sat` and `sun` spelled out, at the head of the
loop in which this change judges each date read. Git merges the two without a conflict. The
limit stands behind the new call as it stood before it: every date read is judged against the
last day allowed, and the refusal quotes the text as it was typed, which is #180's rule for a
refusal as well. Two test cases are added where the two meet,
`Refuses_a_date_past_the_limit_written_with_a_short_weekend_day` in `DateAheadLimitTests`. #182
changes `PublicCalendarBuilder`, which this change does not touch. Step 5.6 has the result.

The nine changes of the table above that are made in the parser's file were made again on the
merged code, one at a time, with the same eight test classes and `master`'s
`NaturalDateTimeParserTests` beside them (281 tests, all green with nothing taken out).
Each failed the tests the table names for it. The changes of the first and the fifth row failed
the two new cases as well, and none failed a test of `NaturalDateTimeParserTests`.

Later that day `master` moved to `723696d` by pull request #184 (date text that is missing or
cannot be read), with its tests and records. It is taken in a fourth time, in the same way.
#184 adds refusals to `TryResolve` and `TryResolveDay` that come before the recognizer reads
the text, and so before this change's lines, and changes `RsvpPolicy`, which this change does
not touch. Git merges the parser without a conflict, and this change's lines in it are as they
were. The nine changes were made once more on that code, with the same classes (307 tests,
all green with nothing taken out), and each failed the same tests as on `916ecd1`. Step 5.7
has the result.

0.37.1 was then released without this change (pull request #185), and #188 (records and two
lines of `CONTRIBUTING.md`) and #187 (CI, a paragraph and a line of `CONTRIBUTING.md`, and
the release-notes tool's own tests) followed; `master` is taken in a fifth time at `162fd42`.
Of the two cases the note of 2026-11-16 above names, the first came about: a release went out
in between, so the change's entry moves to the next version's file. It starts
`docs/release-notes/0.37.2.md`, and `0.37.1.md` stays as 0.37.1 shipped it. No code under
`src` differs from the take-in before. Step 5.8 has the result.

After the push, on 2026-11-18, `master` moved to `04beae8` by pull request #191 (records), and
GitHub reported that #189 conflicts with it, in `aidlc-docs/audit.md`. With the branch on GitHub,
`master` is merged in, in a merge commit, rather than put under the change again; the squash merge
makes one commit of the pull request either way. The audit entries of both sides are in the order of
their timestamps, and git merges the state file by itself. Nothing outside `aidlc-docs` differs.
Step 5.9 has it.
