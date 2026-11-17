# Weekly day sets on the public calendar — functional note and code-generation plan

**Task**: the public month calendar should show a weekly series that repeats on several weekdays
(a day set, for example Mon, Wed, Fri) on every one of those weekdays. Handed over on 2026-11-16
from another session, which had read the code and suspected that the calendar shows such a series
on one weekday only; this session was to confirm or refute that with a failing test first.
**Baseline**: `master` 56b61ab — the code of v0.37.0, which the hosted instance runs.
**Branch**: `fix/public-calendar-day-sets` → PR #182, `fix: show every weekday of a weekly day set on the public calendar`.
**Status**: written 2026-11-16 with the suspicion confirmed by a run (section 1); approved by
Joshua the same day as written, with Q1 and Q2 both answered A (section 5). Generated the same
day and reviewed by two agents, whose findings are answered (section 8). Approved at the
completion gate on 2026-11-17 ("B) Continue"), pushed as one commit, and opened as pull request
#182, which was merged the same day as `4fdfa60` on Joshua's "Merge when green". The release and
the rollout are next, each on its own go-ahead.

A small fix, so the workflow runs at minimal depth: the task is the requirement; no user stories,
application design, units, NFR or infrastructure stages; one unit, and this file is both its
functional note (sections 1–5) and its code-generation plan (sections 6–8), approved together.

## 1. What happens today

Confirmed. The public calendar (`/c/{slug}` in the web app, `GET /public/calendars/{slug}` in the
API) shows two kinds of entry: occurrences that have been posted to Discord, and — dashed —
upcoming occurrences that have not been posted yet, worked out from the series' schedule. For a
weekly series with a day set, the dashed entries are wrong: they fall on the weekday of the
series' first occurrence only, once per week the series is on, instead of on every selected
weekday. Posted occurrences are shown correctly, whatever their weekday.

Run on the released code, with fixed dates (`PublicCalendarBuilderTests`). In each case the first
occurrence is posted and still ahead; the columns list the dashed entries by day of the month.

| Series | Month shown | Shown today | Should be shown |
|---|---|---|---|
| Weekly on Mon, Wed, Fri, from Mon 2 Nov 2026 | Nov 2026 | 9, 16, 23, 30 | 4, 6, 9, 11, 13, 16, 18, 20, 23, 25, 27, 30 |
| The same, until Wed 18 Nov | Nov 2026 | 9, 16 | 4, 6, 9, 11, 13, 16, 18 |
| The same, 6 times in all | Nov 2026 | 9, 16, 23, 30 | 4, 6, 9, 11, 13 |
| The same, 20 times in all | Dec 2026 | 7, 14, 21, 28 | 2, 4, 7, 9, 11, 14, 16 |
| Weekly on Tue, Thu, first occurrence on Sun 1 Nov | Nov 2026 | 8, 15, 22, 29 | 3, 5, 10, 12, 17, 19, 24, 26 |
| Every 2 weeks on Tue, Thu, from Tue 3 Nov | Nov 2026 | 17 | 5, 17, 19 |
| Every weekday, from Mon 26 Oct (America/Chicago) | Oct 2026 | none | 27, 28, 29, 30 |

So a visitor sees three different things go wrong, depending on the series:

- **Days are missing.** A Mon, Wed, Fri series that began on a Monday shows its upcoming Mondays.
  Each Wednesday and Friday appears only once it has been posted, which is after the occurrence
  before it has ended.
- **Days are shown on which nothing happens.** A series may begin on a day outside its set — an
  event created on a Sunday to repeat on Tuesdays and Thursdays. The calendar then shows every
  upcoming Sunday and no upcoming Tuesday or Thursday.
- **A series with a count runs on too long.** Each dashed entry uses up one of the count, so "6
  times" on Mon, Wed, Fri — which ends on its second Friday, the 13th — is shown on the Mondays
  that follow instead: of the four in the November view, three fall after the series has ended.
  With an until date nothing is shown after that date, but the selected days before it are still
  missing.

The two other places that work out a series' future are right, and the route-level tests check
them beside the calendar on the same series: the ICS feed (`/feeds/{token}.ics`, read back with
Ical.Net and its repeat rule expanded) and what the series really posts next (each occurrence
skipped in turn). For a Mon, Wed, Fri series with 7 occurrences from a Monday, both give Mon,
Wed, Fri, Mon, Wed, Fri, Mon; the calendar gives seven Mondays. The same with an until date
sixteen days on: both give eight occurrences, the calendar three.

Only weekly series with a day set are affected, and only on servers that have turned the public
calendar on. Weekly series without a day set, and daily, monthly and yearly series, are shown as
before (`A_weekly_series_without_a_day_set_still_shows_its_one_weekday` passes on the released
code). The web page draws what the API sends and works nothing out itself.

### The cause

`RecurrenceCalculator.NextOccurrence` takes the day set as its last argument, `days`, which is
optional and defaults to "none" — the weekday of the first occurrence. Four places call it:

| Caller | Passes the day set |
|---|---|
| `SeriesMaterializer.MaterializeNext` (what is posted next) | yes |
| `FeedEndpoints.GetFeed` (the ICS feed) | yes |
| `SeriesEndpoints.UpdateSeries` (the "no upcoming occurrences" check on a schedule edit) | yes, the edited set |
| `PublicCalendarBuilder.ProjectOccurrences` (the public calendar) | **no** |

`RecurrenceCalculator.NextDate` is called from `NextOccurrence` alone, which hands the set on.
Nothing in the bot or the web app steps a schedule. So the public calendar is the one place.

The public calendar (#130) and day sets (#135) were merged 44 minutes apart (2026-09-01, 00:14Z
and 00:58Z) from branches written side by side. The day-set change added the argument at the calls
that existed on its branch; the calendar's call, new on the other branch, compiled without it
because the argument is optional. The calendar has shown day sets this way since day sets were
released (v0.29.0).

## 2. The change

`PublicCalendarBuilder.ProjectOccurrences` passes `series.DaysOfWeek` to
`RecurrenceCalculator.NextOccurrence`, as the materializer and the feed do, and its summary says
so. That is the whole change under `src`: one argument.

With it the dashed entries are the occurrences the series will post: every selected weekday, in
the weeks the series is on (counted Monday-first from the week of the first occurrence, as
everywhere else), at the series' local time, up to and including the until date or until the
count is used up — each day-instance counting as one, as it does when the series posts.

The limit on how far the calendar steps a schedule (`MaxStepsPerSeries`, 1,000) needs no change: a
day set is at most seven days a week, which is a daily series, the case the limit was sized for.
To reach the last day of the furthest month served it has to step 763 dates at most, in the runs
the review made (section 8).

Tried once in the working tree and taken out again, so that this plan rests on a run: with the
argument in place the fourteen tests of section 6 pass, and so do the eight calendar tests that
were there before.

## 3. What does not change

- Posted occurrences, one-off events, and series without a day set.
- The ICS feed, what the bot posts, the web app's own pages, and every route's permissions.
- The data: no migration, nothing stored.
- The README and the Docs page: the Docs page already says the calendar shows upcoming
  occurrences of repeating events, and the README says nothing this changes.

## 4. Found beside it, not part of this change

**A moved occurrence.** When the posted occurrence of a series has been moved later by an edit of
that occurrence alone, past one or more of the series' own later dates, the calendar still shows
those dates as upcoming, although they will not be posted: the next occurrence is posted only once
the moved one has ended. Run on the released code as a scratch test, not committed: a daily series
at 18:00 whose Monday occurrence was moved to Tuesday 20:00 shows a dashed Tuesday 18:00 beside
it; the next occurrence the series posts is Wednesday's. The feed has a rule for this — it starts
from the end of the posted occurrence — and a test (`FeedTests`); the calendar does not. It needs
that kind of edit, or an occurrence long enough to run past the next date, so it is rarer than
the day-set fault. It affects every kind of series; a day set shown in full has dates only days
apart, which makes it easier to reach there. Its fix is a different rule in a different place (the
calendar route would have to load the series' posted occurrence even when it lies outside the
month shown), so it is put as Q2 rather than folded in.

The same edit has a second effect on a series with a count, found on review by a run: a date the
moved occurrence has passed, or sits on, still uses up one of the count on the calendar, so the
calendar ends one occurrence early. Mon, Wed, Fri, 6 times, with Monday's occurrence moved to
Wednesday: the calendar shows the 4th, 6th, 9th, 11th and 13th, and the series goes on to post the
16th as well. A daily series does the same, so this is not new with the change either; it belongs
to the fix of Q2.

**The optional argument.** The cause above can recur at any new call of `NextOccurrence`. Making
the argument required would have the compiler catch it. That touches the calculator and the tests
that call it, where this fix touches one line; it is put as Q1.

## 5. Questions

**Q1. Should the day set also become a required argument of `NextOccurrence`?**

- A) Not in this change — pass the day set at the one call, with the tests of section 6
  (recommended: the smallest change that fixes what a visitor sees).
- B) Yes, in this change — `days` loses its default, and the seven tests that call
  `NextOccurrence` without it say "none" explicitly. (Corrected on review, after the answer: four
  tests call it without the argument; the three other calls pass a set.)
- C) Not at all.
- D) Other (please describe).

[Answer]: A — Joshua, 2026-11-16 ("Approve with A, A").

**Q2. What about the moved occurrence (section 4)?**

- A) Its own fix, after this one, with its own plan (recommended).
- B) In this change.
- C) Leave it as it is.
- D) Other (please describe).

[Answer]: A — Joshua, 2026-11-16 ("Approve with A, A").

## 6. Tests

Written first, as the task asks, and run on the released code: thirteen fail, each at its assertion
on the calendar, and the fourteenth — the series without a day set — passes.

In `PublicCalendarBuilderTests` (new; fixed dates, no database):

1. `A_day_set_shows_every_selected_weekday_of_the_month`
2. `A_day_set_with_an_until_date_stops_on_that_date` — an until date that is one of the days, and
   one that falls between two of them
3. `A_day_set_with_a_count_shows_that_many_day_instances_and_no_more`
4. `A_count_is_used_up_by_the_day_instances_before_the_month_shown`
5. `A_first_occurrence_outside_the_day_set_is_not_repeated_on_its_weekday`
6. `A_day_set_every_second_week_shows_the_on_weeks_only`
7. `On_weeks_run_monday_to_sunday_from_the_week_of_the_first_occurrence` — a set with a Sunday,
   and a first occurrence in the middle of its week
8. `A_series_under_way_goes_on_from_its_posted_occurrence` — one occurrence ended, the next
   posted
9. `A_posted_occurrence_moved_onto_the_next_date_is_shown_once` — the entry the calendar already
   leaves out when a posted occurrence starts at the same moment, which a day set now reaches
10. `A_seven_day_set_reaches_the_furthest_month_the_calendar_serves` — all seven days in a
    demanding case for the step limit: seen from the first instant of a month, with that day's
    occurrence still to come, and a month of 31 days 24 months on. That is 762 dates to step,
    under the limit of 1,000 (made harder on review; as approved it was a month of 30 days). A
    clock change between the server's zone and the series' can add one date
11. `A_day_set_keeps_its_local_time_across_a_clock_change`
12. `A_weekly_series_without_a_day_set_still_shows_its_one_weekday`

In `PublicCalendarApiTests` (through the routes, on a Monday one to two weeks ahead of the run, so
the weekdays are the same whatever day the tests run on):

13. `A_day_set_with_a_count_shows_what_the_feed_lists_and_the_series_goes_on_to_post`
14. `A_day_set_with_an_until_date_shows_what_the_feed_lists_and_the_series_goes_on_to_post`

Each of the two compares three things with the dates written out in the test: the feed's
occurrences, the occurrences the series posts when each is skipped in turn, and the calendar's
entries over ten weeks. The feed and the posted occurrences are asserted first, so a failure names
the place that disagrees.

## 7. Steps

### 0. Baseline
- [x] 0.1 The whole suite on this branch before the change to `src`: every test green except the
  thirteen of section 6, and the Release build with no error and the known warnings. Run on
  2026-11-16: the Release build has no error and the baseline's 22 warnings; 1,293 tests pass
  (722 API, 418 bot, 153 web) and thirteen fail, the thirteen of section 6 (1,292 tests before,
  plus fourteen).

### 1. Tests
- [x] 1.1 `PublicCalendarBuilderTests` — the twelve tests above; eleven fail on the released code.
- [x] 1.2 `PublicCalendarApiTests` — the two route tests above and their helpers; both fail on the
  released code at the assertion on the calendar, after the feed and the posted occurrences have
  passed.

### 2. Code
- [x] 2.1 `PublicCalendarBuilder.ProjectOccurrences` passes `series.DaysOfWeek`; its summary names
  the day set.
- [x] 2.2 If Q1 is B: `days` required on `NextOccurrence`, and the tests that call it updated. —
  Q1 is A, so nothing to do.

### 3. Checks
- [x] 3.1 The fourteen tests pass; the whole suite is green; the Release build has no new warning.
  Run on 2026-11-16 with `master` (`ab32e96`) merged in: the Release build has no error and the
  same 22 warnings; 1,651 tests pass (735 API, 418 bot, 153 web, 345 for the release-notes tool)
  and none fails. Seventeen of the release-notes tool's tests are skipped on this Windows machine
  and run in CI: fourteen need a Linux or macOS shell, three the right to create symbolic links.
  The same again after the review's answers.
- [x] 3.2 The argument taken out again in a scratch copy: the thirteen tests fail, and only they.
  Run on a copy of the merged tree: the thirteen fail; the other 722 API tests, the bot's 418,
  the web's 153 and the release-notes tool's 344 pass — 344 because that copy was made before the
  release summary's file was added, which is one more case of one of the tool's tests. Run again
  on a copy of the tree after the review's answers: the thirteen fail, and 722, 418, 153 and 345
  pass.
- [x] 3.3 New source scanned for invisible characters: none in the lines this branch adds. The
  scan first covered 663 lines, before the last records of that day were written; it was run
  again on the whole of what the branch adds after the review's answers, with the same result.

### 4. Records
- [x] 4.1 This plan's status and section 8; `aidlc-state.md`; `audit.md`.

### 5. Review before any push
- [x] 5.1 Two review agents read the local commits — one for security and privacy, one for code
  quality and accuracy; findings answered in a commit they read again. Section 8 has the
  findings and the answers.

Then the completion gate, and after it — each on Joshua's own go-ahead — the push and the pull
request, the merge, the release and the rollout. Before the push `master` is merged in, with the
entries of `audit.md` kept in order of their timestamps.

## 8. As built

Generated on 2026-11-16, after the approval.

**The change** is the one of section 2: `PublicCalendarBuilder.ProjectOccurrences` passes
`series.DaysOfWeek`, and its summary names the day set and says what leaving it out does. Nothing
else under `src` changed. **The tests** are the fourteen of section 6, as they stood at the
approval but for what the review changed: the case of test 10, one helper and four comments.

**What differs from the plan as approved**

1. *`master` was taken in before the review, not just before the push.* It had moved to `4af1305`
   (#179) before the approval and to `ab32e96` (#181) a minute after it, so both were merged in
   and the reviewers read the tree that would be pushed. Only `aidlc-docs/audit.md` and
   `aidlc-docs/aidlc-state.md` conflicted. The audit file keeps master's text as it is, with this
   branch's entries placed in order of their timestamps; the state file keeps master's new
   section and this one, in the order they started.
2. *A release summary.* #179 brought the convention that a pull request which changes something
   users will notice adds to `docs/release-notes/<version>.md` (`CONTRIBUTING.md`, "Release
   summary"). With 0.37.0 the last release and no release pull request open, a `fix:` makes
   0.37.1, so this branch adds `docs/release-notes/0.37.1.md` with one bullet for the public
   calendar. The repository's check passes it. The open pull request #180 adds the same file with
   a bullet of its own and a `### How to use it` part. Whichever of the two is merged second takes
   `master` in and places its bullet by hand: one `### What's new` heading with both bullets under
   it, then #180's how-to (659 of the 1,800 characters). The check does not catch the wrong place
   — it also passes this bullet appended at the end of the file, where it would read as part of
   the how-to. #180 adds to the audit file and the state file as well, so the order of timestamps
   is kept there again.

**Checks**

- With `master` merged in: the Release build has no error and the same 22 warnings; 1,651 tests
  pass and none fails (step 3.1 has the split, and the seventeen that only CI runs).
- The argument taken out again in a copy of the tree: the thirteen tests of section 6 fail and
  nothing else does (step 3.2).
- The two route tests build their dates from the day they run on, so the text they hand to the
  date reader was tried in a scratch test, not committed, for every day from 2026-11-16 to
  2029-12-30 at 00:00, 12:00 and 23:59 UTC — 3,423 moments: each time the start is read as that
  Monday at 18:00 UTC and the until date as the day sixteen days later. The review ran the same
  check further, to the end of 2032 at four times of day and once a day to the end of 2045, with
  the same result.
- The release summary: `dotnet run --project tools/ReleaseNotes -- check` reports no problem in
  the two files of `docs/release-notes/`, and `-- preview --version 0.37.1` shows the bullet as
  the announcement and the release notes would carry it (269 of 1,800 characters).
- The step limit, after the review: with `MaxStepsPerSeries` lowered to 761 in a copy of the tree,
  the furthest-month test fails, and it alone of the twelve on fixed dates; at 762 all twelve
  pass. The limit itself stays at 1,000.

**Review**

Two review agents read the branch before any push, both at the local commit `61e788a` and against
`master` as merged: one for security and privacy, one for code quality and accuracy. Neither
found anything critical, high or medium, and neither asked for a change to the fix itself.

*Security and privacy.* Confirmed by its own runs: the anonymous route gains no unbounded loop and
no new order of cost, under the same step limit; only a valid day set can be stored, by any
route; the page shows no new field; and the added text holds nothing that must not be public.
Two nits:

- S1. The scan for invisible characters was recorded as covering 663 added lines; the branch had
  687 by its commit. Its own scan of all of them found none. *Answered* in step 3.3.
- S2. The wording of one test comment and of one sentence of this plan. *Answered:* both are
  plainer.

*Code quality and accuracy.* It checked every expected date against a calendar of its own, ran
the suite, and on a clock it set compared the calendar, the feed and the posted occurrences for
ten further rules — other intervals, a first occurrence outside the set, an until date and a
count, twenty occurrences across a year end, a zone across both of its clock changes, and
another late in the evening. All agreed.
Five low findings and six nits:

- Q1 (low). Point 1 above had `master` at `ab32e96` by the approval; #181 was merged a minute
  after it. *Answered* there.
- Q2 (low). The furthest-month test was not the worst case for the step limit: it still passed
  with the limit lowered to 761, where a furthest month of 31 days is cut short. *Answered:* the
  test now has 762 dates to step (section 6, test 10), and the step-limit check above shows that
  it holds the limit to that. On its second read the reviewer found one date more where a clock
  change falls inside the span — a server in UTC and a series in a zone that changes its clock:
  763, the most in all its runs. The test stays as it is, and so does the limit of 1,000.
- Q3 (low). Three numbers in these records: the 663 lines (S1); "to the end of 2029" for a check
  that ran to 2029-12-30; and 344 of the tool's tests beside 345, both true, for trees with and
  without the summary's file. *Answered* in steps 3.2 and 3.3 and in the checks above.
- Q4 (low). "Keeps both" was too thin for the second merge of the release summary's file.
  *Answered* in point 2 above.
- Q5 (low). The posted occurrence moved later has a second effect, on a series with a count.
  *Answered:* section 4 has it, for the fix of Q2.
- Q6 to Q8 (nits). Four tests, not seven, call `NextOccurrence` without the argument; the Docs
  page speaks of upcoming occurrences and the README does not; the second merge of 2026-09-01 was
  at 00:58Z. *Answered* in sections 5, 3 and 1.
- Q9 (nit). The first audit entry of this fix quotes the hand-over in part, and says so. Left as
  it is and put to Joshua.
- Q10 (nit). Comments: the on-weeks of December named in the test that rests on them; the
  helpers' summaries pointing at the constant where they said "ten weeks"; the month view's
  address built by the helper that was already there; and the builder's summary no longer calling
  the argument optional, which would have gone stale had Q1 been answered otherwise. *Answered.*
- Q11 (nit). The merge commit kept the "Conflicts" lines git writes into its message. *Answered:*
  before the push the branch is rebuilt as one commit on `master`, with one message for the whole
  change.

Earlier audit entries are left as written; the entry of the review names the numbers in them that
are corrected here. Both agents read the answers in the rebuilt commit, `504a86d`, before the
completion gate, and what they said is in the audit entry that follows it. The commit that is
pushed adds to the one they read only what that second read asked for — words in these records
and in one test comment — and the entries written after it.

**After the push**

Pull request #180 was merged five minutes after #182 was opened, so `master` (`b582c2b`) was
taken in again on 2026-11-17, as point 2 above says the second of the two has to: the audit
file with master's text unchanged and this branch's entries in order of their timestamps, the
state file with both sections, and `docs/release-notes/0.37.1.md` with both bullets under the one
`### What's new` heading, above #180's how-to — 659 of the 1,800 characters, passed by the check.
On that tree the Release build has no error and the same 22 warnings, and 1,743 tests pass and
none fails (827 API, of which #180 brought 92; 418 bot; 153 web; 345 for the release-notes tool).

Before that merge was pushed, #183 was merged too, so `master` (`47c8a15`) was taken in a third
time, on the same terms; on this branch it changed only the audit log and the state file, and
the suite gave the same totals. Both agents read the merge of #180 and the records up to it
before the push; the merge of #183 holds no new text of this fix and was checked by git. The
branch was pushed at `46ce2cb`, its checks passed — `build-test`, `pr-title`, the new
`lint-workflows`, both CodeQL analyses and the secret scan — and pull request #182 was
squash-merged on 2026-11-17 at 17:02:32Z as `4fdfa60`, whose tree is the tree of `46ce2cb`.
