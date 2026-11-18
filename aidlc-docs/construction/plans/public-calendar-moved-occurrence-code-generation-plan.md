# A posted occurrence moved later, or running long, on the public calendar — functional note and code-generation plan

**Task**: the public month calendar should show, for a repeating series, the occurrences the
series will go on to post — also when its posted occurrence has been moved later by an edit of
that occurrence alone, or runs long. Found beside the day-set fix (pull request #182; its plan,
section 4) and decided by Joshua on 2026-11-16 as a fix of its own, after that one, with its own
plan. Handed over on 2026-11-17 with the task to confirm it first with failing tests.
**Baseline**: `master` 4fdfa60 (pull request #182 merged). The hosted instance runs v0.37.0, whose
calendar has the same gap.
**Branch**: `fix/public-calendar-moved-occurrence` → a pull request titled
`fix: drop the dates a moved or long occurrence passes from the public calendar`.
**Status**: written 2026-11-17 with the gap confirmed by runs at both levels (section 1), and
approved by Joshua the same day as written, with Q1–Q4 all answered A ("Approve, all A"). Code
generated and checked the same day (sections 8 and 9); read by two review agents until neither
had a finding (section 9); `master` taken in. Approved at the completion gate on 2026-11-18
("B) Continue"), and the push and the pull request given their go-ahead the same day
("Push and open"), and the merge the same day ("Merge when green"); the release and the
rollout each wait for their own.

A contained fix, so the workflow runs at minimal depth: the task is the requirement; no user
stories, application design, units, NFR or infrastructure stages; one unit, and this file is both
its functional note (sections 1–6) and its code-generation plan (sections 7–8), approved together.

## 1. What happens today

Confirmed. The public calendar (`/c/{slug}` in the web app, `GET /public/calendars/{slug}` in the
API) shows the occurrences that have been posted to Discord and — dashed — the upcoming
occurrences of each repeating series, worked out from the series' schedule.

A series has one posted occurrence at a time. It posts the next one only once that one has ended,
and then takes the first of its dates that starts after that moment. The calendar does not know
this. It lists every date of the schedule after the series' place in it that is still ahead of
now. The two agree as long as the posted occurrence sits on its own date and is over before the
next date begins. They part when someone edits the posted occurrence alone — moves it to a later
day, or makes it longer — because such an edit leaves the series' place in its schedule where it
was. The dates the occurrence then passes are never posted, and the calendar shows them all the
same.

Run on `master`, with fixed dates. Every series starts at 18:00 UTC; unless the row says otherwise
it began on Monday 2 November 2026 and is seen on 31 October. The columns list the dashed entries
by day of the month; where a day also has the moved occurrence, the dashed entry's time is given.
The last column is what the tests of section 7 assert (`PublicCalendarBuilderTests`); the one
before it is from a scratch run of the same cases, kept outside the repository.

| # | Series, and the edit of its posted occurrence | Month shown | Dashed today | Dashed as it should be |
|---|---|---|---|---|
| 1 | Daily; Monday's moved to Tue 3, 20:00 | Nov | 3 (18:00), 4 … 30 | 4 … 30 |
| 2 | Daily; Monday's moved to Thu 5, 20:00 | Nov | 3, 4, 5 (18:00), 6 … 30 | 6 … 30 |
| 3 | Mon, Wed, Fri; Monday's moved to Fri 6, 20:00 | Nov | 4, 6 (18:00), 9, 11 … 30 | 9, 11 … 30 |
| 4 | Mon, Wed, Fri, 6 times; Monday's moved onto Wed 4, 18:00 | Nov | 6, 9, 11, 13 | 6, 9, 11, 13, 16 |
| 5 | Daily, 4 times; Monday's moved to Tue 3, 20:00 | Nov | 3 (18:00), 4, 5 | 4, 5, 6 |
| 6 | Daily until Fri 6; Monday's moved to Thu 5, 20:00 | Nov | 3, 4, 5 (18:00), 6 | 6 |
| 7 | Daily until Thu 5; the same move | Nov | 3, 4, 5 (18:00) | none |
| 8 | Mon, Wed, Fri until Wed 11; Monday's moved to Fri 6, 20:00 | Nov | 4, 6 (18:00), 9, 11 | 9, 11 |
| 9 | Daily; Monday's made 60 hours long (it ends Thu 5, 06:00) | Nov | 3 … 30 | 5 … 30 |
| 10 | Daily; Monday's made 72 hours long (it ends Thu 5, 18:00) | Nov | 3 … 30 | 6 … 30 |
| 11 | Daily from Fri 30 Oct; that one moved to Tue 3 Nov, 20:00; seen 29 Oct | Oct | 31 | none |
| 12 | The same | Nov | 1, 2, 3 (18:00), 4 … 30 | 4 … 30 |
| 13 | Daily; Monday's moved to 15 Dec, 20:00 | Nov | 3 … 30 | none |
| 14 | The same | Dec | 1 … 31 | 16 … 31 |
| 15 | Daily, 40 times; Monday's moved to Tue 3 Nov, 20:00 | Dec | 1 … 11 | 1 … 12 |
| 16 | Daily from Fri 30 Oct; that one is 72 hours long (it ends Mon 2 Nov, 18:00) and under way | Nov | 1 … 30 | 3 … 30 |
| 17 | Daily, 4 times, every occurrence 36 hours long (no edit) | Nov | 3, 4, 5 | 4, 6, 8 |

So a visitor sees four things go wrong:

- **Dates that will not happen.** After a move to a later day, every date the occurrence has
  passed is still dashed in as upcoming — beside the moved occurrence itself when it lands on one
  of them at another time (rows 1–3). The same when the occurrence was made long enough to run
  past dates (rows 9, 10): a date that begins while it is still running is not posted, nor one
  that begins at the very moment it ends.
- **A series with a count ends too early.** A passed date uses up one of the count on the
  calendar and none in the series, so the calendar stops one occurrence early for every date the
  occurrence passes or lands on (rows 4, 5, 15). With an until date nothing is shown after that date, but the passed
  dates before it are (rows 6–8).
- **The month does not have to be the one the occurrence is in.** The view of the month the
  occurrence left shows a date that will not be posted (row 11); the view of a month it was moved
  beyond is full of them (row 13); and a later month gets the count wrong (row 15). The route
  reads only the events of the month shown, so in these views nothing even tells the calendar
  where the posted occurrence is.
- **A series whose own length reaches its next date** is posted on every second date, and shown
  on every date (row 17). This needs no edit at all; it is put as Q3.

Through the routes (`PublicCalendarApiTests`), on dates ahead of the run — from a Monday one to
two weeks on, or around the end of the month after the current one — each series is edited by
the edit route with "this occurrence" as its scope, and three things are
compared: what the calendar shows over ten weeks, what the ICS feed lists (read back with Ical.Net
and its repeat rule expanded), and what the series really posts when the scheduler runs at the end
of each occurrence in turn. In every row the feed and the series agree, and the calendar differs:

| Series, and the edit | The feed lists and the series posts | The calendar shows today |
|---|---|---|
| Daily, 4 times; Monday's moved to Tuesday 20:00 | Tue 20:00, Wed, Thu, Fri | Tue 18:00, Tue 20:00, Wed, Thu |
| Mon, Wed, Fri, 6 times; Monday's moved onto Wednesday 18:00 | Wed, Fri, Mon, Wed, Fri, Mon | Wed, Fri, Mon, Wed, Fri |
| Mon, Wed, Fri until a Wednesday 16 days on; Monday's moved to the next Monday 20:00 | that Monday 20:00, Wed, Fri, Mon, Wed | begins with Wed, Fri and that Monday at 18:00, none of which is posted |
| Daily until Sunday; Monday's made three days long | Mon, Fri, Sat, Sun | Mon, Tue, Wed, Thu, Fri and on |
| Daily, 5 times, from a month's last day but one; moved to the 2nd of the next month, 20:00 | the 2nd 20:00, then the 3rd to the 6th | the month's last day; then the 1st, the 2nd twice, the 3rd |
| Daily, 6 times, from three days before a month's end; moved a day on, 20:00 | the moved one, the month's last two days, the 1st, 2nd, 3rd | a dashed 18:00 beside the moved one, then the last two days, the 1st and the 2nd; the next month's view on its own also stops at the 2nd |

It takes an edit of one occurrence that moves it past a date of its own series, or a length that
reaches the next date, so it is rare; and only servers that have turned the public calendar on
are affected. Any kind of series can be: the tests use daily series and weekly ones with a day
set, and weekly series without a set, monthly and yearly ones take the same path. A posted
occurrence moved earlier, or moved later but over before the next date begins, is shown correctly
today, and so is one whose time has run out but which has not been marked as ended yet; three
tests hold these in place.

### The cause

`PublicCalendarBuilder.ProjectOccurrences` steps the schedule from the series' slot cursor
(`EventSeries.CurrentOccurrenceDate`) and asks for dates after `now`. The scheduler
(`DeliveryScheduler`, through `SeriesMaterializer.MaterializeNext`) asks the same calculator the
same question, but only when the series has no live occurrence — so in effect after the end of
the live one. The ICS feed knows this: it starts from the end of the live occurrence when that is
later than now (`FeedEndpoints.GetFeed`, the comment on the projection floor), and
`FeedTests.Occurrence_scoped_move_of_the_live_row_neither_doubles_it_nor_projects_slots_before_it`
pins it. The calendar has no such rule. Its one guard leaves out a dashed entry that starts at the
very instant of a posted occurrence of the same series, which hides the doubled entry in row 4 but
still uses up one of the count.

The route (`PublicCalendarEndpoints.GetCalendar`) loads the events that start in the month shown
and the server's running series. A live occurrence outside that month never reaches the builder.

## 2. What a visitor should see

The dashed entries of a series are the occurrences it will go on to post if every occurrence runs
its length: the first of its dates that starts after its posted occurrence has ended, and from
there on as before, up to and including the until date or until the count is used up — where only
occurrences that are posted count. In whatever month the posted occurrence lies.

The calendar shows things as they stand. If the posted occurrence is skipped, cancelled or moved
again, the series posts accordingly and the next view shows it; the route is never cached.

## 3. The change

Described for the answers recommended in section 6.

**The rule, in one place.** A new static class in `Services`, `SeriesProjection`, with no I/O:

- the end of an occurrence — its start plus its own length, or plus 60 minutes when it has none,
  the scheduler's default, named there once;
- the moment from which a series can post its next occurrence — now, or the end of its live
  occurrence when that is still to come;
- the occurrences a series goes on to post, in order: the first date after the slot cursor that
  starts after that moment (the calculator's `NextOccurrence`, unchanged), then the first that
  starts after that occurrence has ended at the series' own length, and so on, with the until date
  and the remaining count applied. For a series whose length is shorter than the gap between its
  dates — nearly every series — the dates after the first are the ones listed today.

**The calendar.** `PublicCalendarBuilder.Build` takes one more argument: the live occurrence of
each running series, as series, start and length. It is a required argument, not an optional one:
the day-set fault came from an optional argument that one call could leave out.
`ProjectOccurrences` starts from the end of that occurrence, when that is later than now, and
lists what `SeriesProjection` gives, inside the month and under the same step limit as today
(`MaxStepsPerSeries`, 1,000). A series whose live occurrence starts at or after the end of the
month shown has nothing to show in that month and is passed over before anything is worked out
for it. The guard for an entry at the very instant of a posted occurrence stays.

**The route.** `GetCalendar` makes one more query, and only when the server has a running series:
the live (scheduled or started) occurrence of each of those series — series, start and length,
nothing else — in whatever month it lies. The database keeps a series to one live occurrence
(`IX_Events_SeriesId_Live`), so the result has at most one row for each running series, which the
route already loads.

**What it costs**, since the route is anonymous:

- One more query for each month view. Asked on a table of 404,000 events — 200 servers with 2,000
  past events and 20 running series each — PostgreSQL reads it through that index: 20 rows in 0.2
  milliseconds. The four queries the route makes today are unchanged.
- No more stepping than today, and often less: the dates listed are among the dates stepped
  today, the calculator jumps straight to a later starting moment, and a series whose live
  occurrence starts after the month is left out before any stepping. Each step costs a little
  more than before, since it goes through the helper. Measured side by side after the review,
  for 200 daily series, the dates took 1.1 to 1.8 times as long to work out as before — for the
  furthest month, at most about 15 milliseconds for all 200 — and with occurrences four weeks
  long, a fifth of the time or less. The step limit is still what bounds it. (Written before the
  change as "about 0.4 milliseconds with the change and 0.7 without it", from runs made apart;
  the review found that comparison did not hold up side by side.)
- The response is the same shape and carries no new field, and nothing that the view of the
  posted occurrence's own month does not already show: its start and its length.

**Tried once in the working tree and taken out again**, so that this plan rests on a run: with the
change, the twenty-one tests of section 7 pass and so do the other 827 tests of the API project
(848 in all). With the last part of the rule left out — later occurrences not holding each other
back — exactly two of the twenty-one fail, the two of Q3. The trial is kept outside the
repository as a diff.

**The release summary.** One bullet under `### What's new` in the summary file of the release
this lands in — `docs/release-notes/0.37.1.md` while pull request #185 is open, the next version's
file if 0.37.1 has gone out by then: when one occurrence of a repeating event is moved to a later
day or runs long, the public calendar no longer shows the dates it passes as upcoming, and an
event with a set number of repeats shows its real last date.

## 4. What does not change

- Posted occurrences, one-off events, and every series whose posted occurrence sits on its own
  date and is over before the next one begins: the same entries as today.
- The ICS feed: its output is the same under any answer to Q2.
- What the bot posts and when; the scheduler; the calculator; every route's permissions.
- The data: no migration, nothing stored.
- The page, the response's fields, the README and the Docs page, which says that the calendar
  shows upcoming occurrences of repeating events.

## 5. Found beside it, not part of this change

**A series whose own length reaches its next date.** A repeating event may be created with a
length as long as the gap between its dates, or longer: a daily event that lasts 24 hours. The
scheduler then posts every second date, because a date that begins before the occurrence before
it has ended — or at the moment it ends — is never posted. Run through the routes, daily, 4 times:

| Length of every occurrence | The series posts | The feed lists | The calendar shows today |
|---|---|---|---|
| 23 hours | Mon, Tue, Wed, Thu | the same | the same |
| 24 hours | Mon, Wed, Fri, Sun | Mon, Wed, Thu, Fri | Mon, Tue, Wed, Thu |
| 36 hours | Mon, Wed, Fri, Sun | Mon, Wed, Thu, Fri | Mon, Tue, Wed, Thu |

With Q3 answered A the calendar shows what such a series posts. Two things are left, and neither
belongs to a fix of the calendar: the feed gets the first upcoming date right and lists every date
after it, since one repeat rule cannot say "every second one"; and nothing tells the person who
creates such an event that it will be posted on every second date. Put as Q4.

## 6. Questions

**Q1. Where does the calendar get the end of a series' posted occurrence from?**

- A) One more query in the calendar's route: the posted occurrence of each running series —
  series, start and length — in whatever month it lies, read through the index that keeps a
  series to one (recommended: nothing stored, and at most one row for each running series).
- B) A new column on the series holding that end, written wherever an occurrence is posted,
  moved, lengthened, skipped, cancelled or ended. No extra query, but a migration, and several
  places that must be kept in step from then on.
- C) The month's own query is widened to bring those occurrences along, and the builder tells
  the two kinds of row apart.
- D) Other (please describe).

[Answer]: A

**Q2. Should the calendar and the feed share one helper for "what the series will post next"?**

- A) Yes, in two steps: this change adds the helper and the calendar uses it; the feed, which is
  right today, keeps its own lines for now and moves onto the helper in a small change of its own
  afterwards (recommended: this change then touches the calendar's files only, and nothing that
  works today is rewritten in it).
- B) Yes, in this change: the feed's lines for the projection floor and the next date are
  replaced by calls of the helper. Its output does not change; `FeedTests` holds it.
- C) No: the calendar gets its own copy of the rule, and the feed stays as it is for good.
- D) Other (please describe).

[Answer]: A

**Q3. How far does the rule go for a series whose own length reaches its next date (row 17)?**

- A) All the way: every upcoming occurrence holds the next one back until it has ended, as the
  scheduler does, so the calendar shows the dates such a series will post — Wed, Fri, Sun in the
  run above (recommended: the dashed entries then mean one thing for every series; it costs
  nothing more).
- B) Only the posted occurrence is allowed for, as in the feed: the first upcoming date is right
  and the ones after it are listed on every date — Wed, Thu, Fri where the series posts Wed, Fri,
  Sun. The two tests of section 7 marked for Q3 are dropped.
- C) Other (please describe).

[Answer]: A

**Q4. What about such a series in the feed, and at its creation (section 5)?**

- A) Its own task after this one, beginning with a report and a plan: what a series whose length
  reaches its next date should do — be refused, or warned about, at creation and on edit, or be
  posted differently — and what the feed should list for it (recommended).
- B) Leave it: such series are rare, and with this change the calendar shows what is posted.
- C) Other (please describe).

[Answer]: A

## 7. Tests

Written first, as the task asks, and run on `master`: eighteen fail, each at its assertion on the
calendar, and three — the controls — pass.

In `PublicCalendarBuilderTests` (fixed dates, no database; the rows of the first table):

1. `A_posted_occurrence_moved_past_the_next_date_holds_that_date_back` — row 1
2. `A_posted_occurrence_moved_past_several_dates_holds_them_all_back` — row 2
3. `A_day_set_occurrence_moved_past_two_of_its_days_goes_on_from_the_day_after` — row 3
4. `Dates_a_moved_occurrence_passed_use_up_none_of_the_count` — rows 4 and 5: a move onto a date,
   and a move past one
5. `An_until_date_ends_a_series_whose_occurrence_was_moved_where_it_ends_the_series` — rows 6–8
6. `A_posted_occurrence_that_runs_long_holds_back_the_dates_it_runs_past` — rows 9 and 10: a
   date that begins after it has ended, and one that begins at the moment it ends
7. `A_posted_occurrence_in_another_month_still_holds_dates_back` — rows 11 and 12
8. `A_posted_occurrence_moved_beyond_the_month_shown_leaves_that_month_empty` — rows 13 and 14
9. `A_count_seen_from_a_later_month_allows_for_the_dates_that_were_passed` — row 15
10. `A_long_occurrence_begun_in_the_month_before_holds_back_this_months_first_dates` — row 16
11. `A_series_whose_own_length_reaches_its_next_date_shows_the_dates_it_will_post` — row 17;
    **stands with Q3 = A**
12. `A_moved_occurrence_that_ends_before_the_next_date_begins_leaves_it_in_place` — control:
    moved to the next day's morning, so that day has two entries, as today
13. `An_occurrence_moved_earlier_changes_nothing_after_it` — control
14. `An_occurrence_whose_time_has_run_out_holds_nothing_back` — control: over by its length, not
    yet marked as ended; seen half an hour after its end, and (added on review) a day later, when
    the dates start from now and not from its end
22. `An_occurrence_with_no_length_of_its_own_runs_an_hour` — added on review: Monday's occurrence
    moved to Tuesday 17:00 with no length of its own runs the default hour, to 18:00, and holds
    Tuesday's 18:00 back; moved to 16:59 instead, it does not
23. `SeriesProjectionTests.Upcoming_lists_no_more_occurrences_than_the_steps_it_is_given` — added
    on review: the helper itself, for a series without an end, lists three occurrences when it is
    given three steps and none when it is given none

The helper that builds a month view in these tests does it the way the route does: the posted
occurrence is among the month's events only when it starts in that month. With the change it also
hands over the live occurrence, as the route's new query does.

In `PublicCalendarApiTests` (through the routes; the rows of the second table):

15. `A_posted_occurrence_moved_past_a_date_shows_what_the_feed_lists_and_the_series_goes_on_to_post`
16. `A_day_set_occurrence_moved_onto_its_next_day_shows_what_the_feed_lists_and_the_series_goes_on_to_post`
17. `A_day_set_occurrence_moved_past_several_days_stops_on_its_until_date_as_the_feed_and_the_series_do`
18. `A_posted_occurrence_that_runs_long_shows_what_the_feed_lists_and_the_series_goes_on_to_post`
19. `A_posted_occurrence_moved_into_the_next_month_leaves_no_upcoming_date_behind_it` — the month
    the occurrence left is the first of the month views it reads
20. `A_count_seen_from_the_month_after_the_posted_occurrence_allows_for_the_date_it_passed` — the
    month after is one of the views it reads, without the posted occurrence in it
21. `A_series_whose_own_length_reaches_its_next_date_shows_what_the_series_goes_on_to_post` — the
    calendar beside what the series posts; the feed is not compared (section 5). **Stands with
    Q3 = A**

Tests 15 to 20 compare three things with the dates written out in the test, and assert the feed
and the series first, so a failure names the place that disagrees. What the series posts is found
by a new helper, `PostedByRunningAsync`: it runs the scheduler's own pass as of the moment the
posted occurrence ends — twice, since one pass starts an occurrence and the next ends it and posts
the one that follows — and reads the series' new occurrence, until the series ends. The two
route tests of the day-set fix find it by skipping; that does not serve here, because a skipped
occurrence no longer holds anything back. Tests 19 and 20 take their dates from the end of the
month after the one they run in, so that every date is ahead on any day of any month.

## 8. Steps

### 0. Baseline
- [x] 0.1 The whole suite on this branch before any change to `src`: every test green except the
  eighteen of section 7, and the Release build with no error and the known warnings. Run on
  2026-11-17: the Release build has no error and `master`'s 22 warnings; 1,746 tests pass (830
  API, 418 bot, 153 web, 345 for the release-notes tool) and eighteen fail, the eighteen of
  section 7 — `master`'s 827 API tests plus twenty-one. Seventeen of the release-notes tool's
  tests are skipped on this Windows machine and run in CI.

### 1. Tests
- [x] 1.1 `PublicCalendarBuilderTests` — the fourteen tests above; eleven fail on `master`.
- [x] 1.2 `PublicCalendarApiTests` — the seven route tests above and their helpers; all seven fail
  on `master` at the assertion on the calendar, after the feed and the series have agreed.

### 2. Code
- [x] 2.1 `Services/SeriesProjection.cs` (new) — the live occurrence as series, start and length;
  the end of an occurrence; the moment a series can post from; the occurrences it goes on to post.
- [x] 2.2 `PublicCalendarBuilder` — `Build` takes the live occurrences (required);
  `ProjectOccurrences` starts from the live occurrence's end and lists what `SeriesProjection`
  gives, under the step limit; a series whose live occurrence starts at or after the month's end
  is left out; the summaries say so.
- [x] 2.3 `PublicCalendarEndpoints.GetCalendar` — the one more query, only when the server has a
  running series; the summary names it.
- [x] 2.4 `PublicCalendarBuilderTests` — its helper hands the live occurrence over as the route
  does; the four direct calls of `Build` pass the new argument. (Five calls in four tests, as the
  review counted: the test across a clock change makes two. The furthest-month test passes an
  empty list, its series having no live occurrence.)
- [x] 2.5 If Q2 is B: `FeedEndpoints.GetFeed` calls the helper for the floor and the next date.
  Not done: Q2 is A, so the feed is unchanged.
- [x] 2.6 If Q3 is B: later occurrences do not hold each other back, and tests 11 and 21 go.
  Not done: Q3 is A, so both tests stay.

### 3. Checks
- [x] 3.1 The tests of section 7 pass; the whole suite is green; the Release build has no new
  warning. Run on 2026-11-17: 1,764 tests pass (848 API, 418 bot, 153 web, 345 for the
  release-notes tool) and none fails; seventeen of the tool's tests are skipped on this machine,
  as before. The Release build has no error and the same 22 warnings as `master`.
- [x] 3.2 Each part of the rule taken out in turn in a scratch copy — the live occurrence's end,
  the later occurrences' ends, the count, the query's reach beyond the month — and the tests that
  fail for each written down. Run on a copy of the tree, each time with the calendar's 43 tests
  (26 on fixed dates, 17 through the routes), and each part put back before the next:
  - the live occurrence's end left out, so that the dates start from now: 18 fail — every test
    of section 7 but the three controls;
  - later occurrences not held back by the one before them (Q3 = B): 2 fail — tests 11 and 21;
  - the count left out: 12 fail — tests 4, 9, 11, 15, 16, 19, 20 and 21, and four older tests of
    counts;
  - the route's query kept to the month shown: 4 fail — tests 19 and 20, which cross a month's
    end whatever day they run on, and tests 18 and 21, which crossed one on this run, its Monday
    being 30 November.

  Run again after the review's answers, with the calendar's 44 tests (27 on fixed dates): the
  same, but that leaving out the live occurrence's end fails test 22 as well (19 in all), and two
  more parts were taken out — letting a live occurrence's end that has already passed win over
  the current time fails test 14, and a default length of 0 fails test 22, each alone. After the
  review's second read: a default length of 59 or of 61 minutes fails test 22, and taking the step
  limit out of the helper fails test 23, each alone.
- [x] 3.3 The step limit: the furthest-month test still needs 762 dates and fails at a limit of
  761, as on `master`. Run in the same copy: with `MaxStepsPerSeries` at 761 that test fails, and
  it alone of the 26 on fixed dates; at 762 all 26 pass. The limit stays at 1,000. Run again after
  the review's answers, which moved the limit into the helper: the same, with 27 tests. The time
  this takes is in section 3, "What it costs".
- [x] 3.4 The route's queries counted for a month view with and without a running series; the new
  one's plan asked of PostgreSQL once more on the built code. Counted in a scratch test, not
  committed: a month view sends five queries when the server has a running series and four when
  it has none — the four of today, unchanged. The new one, as EF Core sends it:
  `SELECT e."SeriesId", e."StartsAt", e."DurationMinutes" FROM "Events" AS e WHERE e."GuildId" =
  @guild_Id AND e."SeriesId" IS NOT NULL AND e."SeriesId" = ANY (@seriesIds) AND e."Status" IN
  (0, 1)`. Asked with its own parameters on the table of 404,000 events, PostgreSQL reads it
  through `IX_Events_SeriesId_Live`: 20 rows, 0.14 milliseconds. Over repeated views it took 2 to
  4 milliseconds from the route, as the route's other queries do; the first view after the API
  started took longer for every query.
- [x] 3.5 The route tests build their dates from the day they run on: the text they hand to the
  date reader tried in a scratch test, not committed, for every day of several years at several
  times of day, the ends of months and of years among them. Every day from 2026-11-17 to
  2032-12-31 at 00:00, 12:00, 18:00 and 23:59:59 UTC — 8,948 moments and 89,480 texts, the
  starts and edits of tests 15 to 21 and the until dates of tests 17 and 18: each is read as
  written, the starts to the minute and the until dates to the day. Run again after `master` was
  taken in, with the date reader of pull request #184: the same result.
- [x] 3.6 New source scanned for invisible characters: none among the lines the branch adds.

### 4. Records
- [x] 4.1 The release summary's bullet, placed under `### What's new`, and the repository's check
  on it; this plan's status and an "As built" section; `aidlc-state.md`; `audit.md`. The bullet
  was first put in `docs/release-notes/0.37.1.md`, the version of the then open release pull
  request #185. 0.37.1 was released minutes later, on 2026-11-17 at 23:27 UTC, without this fix,
  and no release pull request has been open since; so, as section 3 provides and as the review
  asked, `0.37.1.md` is back to `master`'s text and the bullet is in a new file,
  `docs/release-notes/0.37.2.md` — the version a `fix:` makes after 0.37.1. `dotnet run
  --project tools/ReleaseNotes -- check` reports no problem in the folder's three files, and
  `-- preview --version 0.37.2` shows the summary as the announcement and the release notes
  would carry it, 387 of its 1,800 characters. Pull requests #189 and #190, open as this is
  written, create the same file with a bullet of their own: whichever of the three is merged
  later takes `master` in and puts its bullet under the one `### What's new` heading by hand, as
  the day-set fix did with pull request #180 — the check passes either way. Section 9 is the
  "As built".

### 5. Review before any push
- [x] 5.1 Two review agents read the local commits — one for security and privacy, one for code
  quality and accuracy; findings answered in a commit they read again. Read at `234c701`, then
  the answers at `d9f9abb`, `67a5629` and `2f05400`, until neither had a finding; section 9 has
  each finding and its answer.

Then the completion gate — answered B on 2026-11-18 ("B) Continue") — and after it, each on
Joshua's own go-ahead, the push and the pull request, the merge, the release and the rollout.
Before the push `master` is merged in, with the entries of `audit.md` kept in order of their
timestamps and master's text unchanged.

## 9. As built

Generated on 2026-11-17, after the approval; changed on 2026-11-18 by the answers to the
review's two reads.

**The change** is the one of section 3, for the answers given:

- `Services/SeriesProjection.cs` (new): `LiveOccurrence`, a series' live occurrence as series,
  start and length; `EndOf`, an occurrence's end, with an hour when it has no length of its own,
  as the scheduler and the feed count it; `FreeFrom`, now or the live occurrence's end, whichever
  is later; and `Upcoming`, the starts of the occurrences a series goes on to post, each holding
  the next back until it has ended, under a step limit its caller must give.
- `PublicCalendarBuilder.Build` takes the live occurrences as a required argument.
  `ProjectOccurrences` takes the live occurrence's end and lists, inside the month, what
  `Upcoming` gives, under `MaxStepsPerSeries`. A series whose live occurrence starts at or after
  the month's end is passed over.
- `PublicCalendarEndpoints.GetCalendar` reads the live occurrences of the server's running
  series with one more query, made only when there is a running series; its summary says so.
- Nothing else under `src`: the feed, the scheduler, the calculator and every other route are as
  they were.

**The tests** are the twenty-one of section 7 as they stood at the approval, with what the review
changed: tests 22 and 23 are new, test 14 looks at a second moment, tests 19 and 20 no longer
read a second time a month view they had already read, and two comments are plainer. Under
`master`'s rule test 22 fails too, as the other eighteen do, and the extended control passes;
test 23 asks the new helper itself. The builder tests' helper hands the live occurrence over as
the route does (`LiveOf`), and the direct calls of `Build` pass it.

**What differs from the plan as approved**

1. *Two live occurrences of one series.* The database keeps a series to one. Were the builder ever
   handed two, it takes the later end, as the feed does; the plan did not say, and the trial took
   the later start.
2. *`master` taken in before the review, again after it, before the push, and after it.* It had
   moved to `916ecd1` (pull request #186, records only) before the approval, and to `723696d`
   (pull request #184, the date reader) while the code was being written; both were merged in
   before the review. 0.37.1 was released (pull request #185) minutes before the commit the
   reviewers first read was made, and while they read `master` moved on to `162fd42` (pull
   requests #188 and #187: records, a code owners file and the release workflow), so it was
   merged in again, and the reviewers' second read is of that tree. After the completion gate it
   moved on with records only, to `04beae8` (pull request #191) and then to `47209ab` (pull
   request #194), and each was merged in before the push. After the push, while the merge waited
   for its go-ahead, it moved to `67baa24` (pull request #198, which changes code outside this
   fix's files), to `db488b3` (pull request #193, documentation) and to `5fc9239` (pull
   request #195, records), and once the merge had its go-ahead, to `3e35071` (pull request #189,
   which also changes code outside this fix's files), to `6b85491` (pull request #202, records)
   and to `6cb41c8` (pull request #199, which changes code outside this fix's files too). The
   pull request's records conflicted with each, and with #189 and #199 its release summary as
   well (point 3); each was merged in the same way, and the whole suite passes on the tree with
   `6cb41c8`. The audit file keeps master's text as it is, with each of this branch's entries
   placed among master's in the order of their timestamps; the state file keeps master's
   sections, with this one after them, in the order they started.
3. *The release summary's file* is `docs/release-notes/0.37.2.md`, not `0.37.1.md`: 0.37.1 went
   out without this fix (step 4.1). Pull request #189 created the same file first, so this fix's
   bullet is added to it, at the end of the list under its one `### What's new` heading.
4. *What the review changed*, listed under "Review" below: the step limit is now an argument of
   the helper, the helper gives start instants alone, two tests more and one control extended,
   and the records corrected where they said more than the runs showed.

**Checks** (section 8 has each in full)

- With `master` (`162fd42`) merged in and the answers to both reads: the Release build has no
  error and the same 22 warnings; 1,808 tests pass and none fails (888 API, 418 bot, 153 web, 349
  for the release-notes tool; seventeen of the tool's tests run in CI only). Before, 1,764 passed
  on `4fdfa60`, 1,802 on `723696d` and 1,807 after the first read's answers, each with none
  failing.
- Each part of the rule taken out in turn: the tests named in step 3.2 fail, and no others of
  the calendar's 44.
- The step limit holds the furthest-month test to 762 dates, as before.
- The route sends five queries for a month view when the server has a running series, four when
  it has none; the new one is read through `IX_Events_SeriesId_Live`.
- The route tests' date texts are read as written on every day to the end of 2032.
- The release summary passes the repository's check, and its preview is as above.
- No invisible characters in what the branch adds.

**Review**

Two review agents read the branch before any push, both at the local commit `234c701` (with
`master` at `723696d` merged in): one for security and privacy, one for code quality and
accuracy. Neither found a fault in what the fix does.

*Security and privacy.* No critical, high or medium finding. Confirmed by reading the code and by
its own runs: the new
query is kept to the server twice over — its own server and its running series — reads at most
one row for each running series, through `IX_Events_SeriesId_Live`, and adds nothing to the
response; it runs only once the slug has matched, so it is no way to test slugs; and the schedule
is stepped no more than before, under the same limit. Its findings:

- S1 (low). `Upcoming` was public and, for a series without an end, went on without end, leaving
  its limit to each caller; a later caller that forgot it could step without bound on an
  anonymous route. *Answered:* the step limit is a required argument of `Upcoming`, and the
  builder passes `MaxStepsPerSeries`.
- S2 (nit). Section 3's figure for the furthest month — 0.4 milliseconds against 0.7 — did not
  hold up side by side: the change takes no more steps, but each costs a little more. *Answered:*
  section 3 now gives figures measured side by side.
- S3 (nit). The first merge commit's message kept git's list of conflicts. *Answered:* the branch
  is rebuilt as one commit on `master` before the push.

*Code quality and accuracy.* It found the fix correct. It compared the calendar with a scheduler
of its own over 48,000 random cases — every unit of repetition, intervals, day sets, counts and
until dates, ten zones, eight of them with clock changes, occurrences moved earlier or later or
made longer, and series from a minute to four weeks long — and found no difference, where
`master`'s rule differs in about a quarter of them. One high finding, one medium, five low and ten
nits:

- Q1 (high). The release summary's bullet was in the file of 0.37.1, released minutes before the
  commit it read was made. *Answered* in step 4.1.
- Q2 (medium). `audit.md` had no entry for the generation of the code and its checks.
  *Answered:* an entry of 2026-11-18 records them, with the review and its answers.
- Q3 (low). No test held that a live occurrence's end, once past, gives way to now. *Answered:*
  test 14 looks again a day after that end; with the rule turned the other way it fails.
- Q4 (low). The default hour was held by no test, and its summary called it the length the
  scheduler gives, though nothing ties it to the scheduler's own copy. *Answered:* test 22, and
  the summary says that the scheduler and the feed each keep their own copy of the value. Its
  suggestion that the scheduler use the constant is not taken: the plan leaves the scheduler as
  it is.
- Q5 (low). Passing over a series whose live occurrence starts after the month repeats the early
  exit of `ProjectOccurrences`. *Kept*, as section 3 describes it: such a series is passed over
  before anything is worked out for it.
- Q6 (low). The timing figures, as S2. *Answered* in section 3.
- Q7 (low). The status said the reviewers had read the branch before they had, and this section
  said they read the tree that would be pushed, which `master` moving on made untrue.
  *Answered:* both corrected (point 2 above).
- Nits, all answered in the code or the records: the count of direct calls of `Build` (step
  2.4); a block comment of the route tests that did not fit the last of them; tests 19 and 20
  read a second time month views already read, so those asserts could never fail first (removed,
  and section 7 says what the views show); the guard's comment, which no longer said when the
  guard can matter; a variable's name; `Upcoming` gave slot dates that no caller used
  (it now gives start instants); a length written as `60 * 60` for 60 hours; a local named
  `today` that held a full time; a summary that used "from" in two senses; and the merge commit's
  message, as S3.

*Second read.* Both agents then read the answers at `d9f9abb` — the branch rebuilt as one commit
on `master` `162fd42` — and, between them, confirmed them by reading and by runs of their own:
the step limit inside the helper; the timing, which their own side-by-side runs put at 1.14 to
1.31 times as long for daily series and well under that with long occurrences; the release summary's new
file; the audit entries in the order of their timestamps, with master's text unchanged; the
rule parts taken out in turn; the comparison of 48,000 cases, still without a difference; and
the whole suite green. They asked for:

- S4 (nit). This section said the security review's points were all confirmed by runs; some
  were confirmed by reading the code. *Answered* above.
- Q8 (low). Test 22 held only that the default length is at least half an hour. *Answered:* its
  first case now moves the occurrence to 17:00, so that Tuesday's 18:00 begins the moment it
  ends, and a default of 59 minutes fails it; a second case moves it to 16:59, where Tuesday's
  18:00 is posted, and a default of 61 minutes fails that one. (The audit entry of
  2026-11-18T00:39:57Z gave the 16:59 case as the reason for both; the entry after it corrects
  this.)
- Q9 (low). No test held the helper's new step limit. *Answered:* test 23, which fails with the
  limit taken out.
- Q10 (low). The records said 0.37.1 was released while the review ran; it was released minutes
  before the commit they read was made. *Answered* in point 2 and Q1 above; the audit entry of
  2026-11-18T00:16:42Z said the same, and the entry after it corrects it.
- Nits, all answered: places where this section did not match the first report (Q4, the guard's
  comment and the description of the comparison, corrected above); the audit entry of
  2026-11-17T17:41:11Z said the furthest month takes no longer than before, which the
  side-by-side figures contradict (corrected in the entry after 2026-11-18T00:16:42Z); the
  status said the gate came next before their second read; step 4.1 did not say that pull
  requests #189 and #190 create the same summary file; the commit's message said the feed and the
  series were compared in tests that compare only the calendar, or the calendar and the series;
  a wording in step 3.2; and a phrase in the documentation of the helper's `maxSteps`.

*Last read.* Both agents read that difference, at `67a5629`, and found the code and the tests
sound; they asked for words only, in this section, and those are in. Both then read that last
difference, at `2f05400`, and asked only that the audit entry Q8 points to be written: it is
the entry of the completion gate.
