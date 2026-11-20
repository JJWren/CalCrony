# A repeating event whose length reaches its next date, and the feed on the shared helper — report and code-generation plan

**Task**: two follow-ups of the fix for a moved or long posted occurrence (pull request #196),
which Joshua approved on that fix's plan
(`public-calendar-moved-occurrence-code-generation-plan.md`) and which touch the same lines, so
they are taken together. His answer Q2 = A there: the ICS feed lists a series' upcoming dates
through the helper that fix added, `SeriesProjection`, in a small change of its own. His answer
Q4 = A: a series whose own length reaches its next date gets a task of its own, beginning with a
report and a plan — whether such a series should be refused, or warned about, at creation and on
edit, or be posted differently, and what the feed should list for it. Handed over on 2026-11-18.
**Baseline**: `master` 4242144, with pull requests #196, #189, #190 and #204 merged and none of
them released yet (release 0.37.2 is open as pull request #200). The hosted instance runs
v0.37.1. While the code was written, 0.37.2 was released (pull request #200) and pull request
#197 merged; `master` (`f05e1d0`) is taken in. Release 0.38.0 (pull request #207, with #197
alone) went out during the review, and pull request #209 (tests and records) merged before the
push; the branch stands on that `master` (`dab6a31`).
**Branch**: `fix/feed-long-series`, cut from `master` → one pull request with a `fix:` title.
**Status**: written 2026-11-18, with what happens today confirmed by runs at both levels
(section 1), and approved by Joshua the same day as written, with Q1–Q3 all answered A
("Approve, all A"), and Q4, asked after the checks, answered A ("Yes, A"). Code generated and
checked, `master` taken in; section 8 is the "As built". Joshua answered the completion gate on
2026-11-19 ("B) Continue") and gave the go-ahead to push the branch and open its pull request
("Yes, push"); `master` (`dab6a31`, then `11afb47`) is taken in first, and both agents read the
result before the push.

A contained change, so the workflow runs at minimal depth: the task is the requirement; no user
stories, application design, units, NFR or infrastructure stages; one unit, and this file is both
its report (sections 1 and 2) and its code-generation plan (sections 3 to 7), approved together.

## 1. What happens today

A repeating event — a series — has one posted occurrence at a time. The scheduler posts the next
one only once that one has ended, on the first of the series' dates that begins after that
moment. So when an occurrence lasts as long as the gap to the series' next date, or longer, that
date is never posted: the series goes on from the first date that begins after the occurrence has
ended. A daily event that lasts 24 hours is posted on every second day. Lengths from a minute to
four weeks are accepted, on a one-off event and on a series alike.

Confirmed by runs at both levels, in scratch tests kept outside the repository, on `master` at
5b30e5b and again at 4242144, with the same results:

- **Builder level**, on fixed dates, with no database: what the series posts, from the
  scheduler's own `SeriesMaterializer`, asked at the moment each occurrence ends; what the public
  calendar shows, from `PublicCalendarBuilder.Build` for each month; what the feed lists, from the
  feed's own lines for the first upcoming date and its `AddSeriesEvent` for the entry that carries
  the repeat rule, written out and read back with Ical.Net as a calendar app reads it; and the
  repeat line the bot and the web show, from `RecurrenceCalculator.Describe`.
- **Route level**, through the routes, on dates ahead of the run: the series created by the create
  route; the public calendar's month views; the ICS feed, read back with Ical.Net; and what the
  series posts, found by running the scheduler's own pass at the end of each occurrence in turn,
  as `PublicCalendarApiTests` does.

On fixed dates. Every series starts at 18:00 UTC on Monday 2 November 2026 unless the row says
otherwise:

| # | Series, and the length of every occurrence | The schedule names | The series posts | The feed lists |
|---|---|---|---|---|
| 1 | Daily, 4 times, 23 h | Mon 2, Tue 3, Wed 4, Thu 5 | the same | the same |
| 2 | Daily, 4 times, 23 h 59 min | Mon 2, Tue 3, Wed 4, Thu 5 | the same | the same |
| 3 | Daily, 4 times, 24 h | Mon 2, Tue 3, Wed 4, Thu 5 | Mon 2, Wed 4, Fri 6, Sun 8 | Mon 2, Wed 4, Thu 5, Fri 6 |
| 4 | Daily, 4 times, 36 h | as row 3 | as row 3 | as row 3 |
| 5 | Daily, 4 times, 48 h | Mon 2, Tue 3, Wed 4, Thu 5 | Mon 2, Thu 5, Sun 8, Wed 11 | Mon 2, Thu 5, Fri 6, Sat 7 |
| 6 | Mon, Wed, Fri, 6 times, 60 h | Mon 2, Wed 4, Fri 6, Mon 9, Wed 11, Fri 13 | Mon 2, Fri 6, Mon 9, Fri 13, Mon 16, Fri 20 | Mon 2, Fri 6, Mon 9, Wed 11, Fri 13, Mon 16 |
| 7 | Weekly, 4 times, 7 days | Mon 2, 9, 16 and 23 Nov | Mon 2 Nov, 16 Nov, 30 Nov, 14 Dec | Mon 2, 16, 23 and 30 Nov |
| 8 | Monthly on the 1st, 4 times from 1 Jan 2027, 4 weeks (the longest length there is) | 1 Jan, 1 Feb, 1 Mar, 1 Apr | 1 Jan, 1 Feb, 1 Apr, 1 May | 1 Jan, 1 Feb, 1 Mar, 1 Apr |
| 9 | Daily at 09:00 New York time, 5 times from Thu 11 Mar 2027, 23 h 59 min; the clocks go forward in the night to Sun 14 | Thu 11 to Mon 15 | Thu 11, Fri 12, Sat 13, Mon 15, Tue 16 | Thu 11, Fri 12, Sat 13, Sun 14, Mon 15 |
| 10 | Daily at 09:00 New York time, 5 times from Thu 4 Nov 2027, 24 h; the clocks go back in the night to Sun 7 | Thu 4 to Mon 8 | Thu 4, Sat 6, Sun 7, Tue 9, Thu 11 | Thu 4, Sat 6, Sun 7, Mon 8, Tue 9 |
| 11 | Daily without an end, 24 h | every day | every second day, as far as the run looked (61 occurrences, to Tue 2 Mar 2027) | Mon 2, Wed 4, then every day |
| 12 | Control: daily, 4 times, no length set (an hour) | Mon 2, Tue 3, Wed 4, Thu 5 | the same | the same |

In every row the public calendar shows exactly what the series posts. The repeat line is the
schedule's in every row: "Repeats daily · 1 of 4", "Repeats weekly on Mon, Wed, Fri · 1 of 6",
"Repeats weekly on Monday · 1 of 4", "Repeats monthly on day 1 · 1 of 4", and so on.

Rows 9 and 10 need no length of a day. Where the clocks go forward the night is an hour short, so
Saturday's occurrence of 23 h 59 min ends at 09:59 on Sunday, after Sunday's date has begun;
where they go back the night is an hour long, so Saturday's occurrence of 24 h ends at 08:00 on
Sunday, before Sunday's date begins, and two dates in a row are posted.

Through the routes, from a Monday one to two weeks after the run (30 November 2026 on this run),
at 18:00 UTC:

| Series | The create route answers | The series posts | The feed lists |
|---|---|---|---|
| Daily, 4 times, 23 h | 201 Created; "Repeats daily · 1 of 4" | Mon 30, Tue 1, Wed 2, Thu 3 | the same |
| Daily, 4 times, 24 h | 201 Created; "Repeats daily · 1 of 4" | Mon 30, Wed 2, Fri 4, Sun 6 | Mon 30, Wed 2, Thu 3, Fri 4 |
| Daily, 4 times, 36 h | 201 Created; "Repeats daily · 1 of 4" | Mon 30, Wed 2, Fri 4, Sun 6 | Mon 30, Wed 2, Thu 3, Fri 4 |
| Mon, Wed, Fri, 6 times, 60 h | 201 Created; "Repeats weekly on Mon, Wed, Fri · 1 of 6" | Mon 30, Fri 4, Mon 7, Fri 11, Mon 14, Fri 18 | Mon 30, Fri 4, Mon 7, Wed 9, Fri 11, Mon 14 |
| Weekly, 3 times, 7 days | 201 Created; "Repeats weekly on Monday · 1 of 3" | Mon 30 Nov, Mon 14 Dec, Mon 28 Dec | Mon 30 Nov, Mon 14 Dec, Mon 21 Dec |

In every row the calendar's month views show exactly what the series posts, and the feed has two
entries for the series: the posted occurrence, and the entry with the repeat rule (for the daily
ones, `RRULE:FREQ=DAILY;COUNT=3`). Two changes were made through the routes as well: the whole
series of a daily event of an hour made 24 hours long, through the edit route — 200 OK, with the
repeat line "Repeats daily · 1 of 4" — and a weekly event 36 hours long given a daily schedule,
through the series route — 200 OK, "Repeats daily".

So, for each of the three:

- **The creator is told nothing.** Such a series is accepted at creation, from a template or not,
  and so is a change of the whole series' length, or of its schedule, that makes one. In Discord
  the reply to `/create` carries the repeat line ("✅ … created … · 🔁 Repeats daily · 1 of 4"),
  as do the event's message and `/series info`; on the web, the event page shows the same line.
  The web form's length field goes up to 1,440 minutes — a day exactly, which on a daily event is
  such a length.
- **A calendar visitor sees what the series posts.** Since pull request #196 the public calendar
  holds each date back until the occurrence before it has ended, and in every run above it agrees
  with the series.
- **A feed subscriber sees the first upcoming date right, and then every date of the schedule.**
  The feed starts after the posted occurrence has ended, as the scheduler does, and then writes
  one entry with a repeat rule, which names every date of the schedule. It lists dates that are
  never posted, and a series with a count ends early in the feed, its count spent on them: the
  daily ones above end on Friday in the feed and on Sunday in Discord. The Docs page tells
  subscribers: "Repeating events carry their full schedule (RRULE), so subscribers see every
  future occurrence."

No other place works out a series' upcoming dates: the scheduler (and the skip route, through the
same `SeriesMaterializer`), the calendar and the feed are the only users of the schedule's
arithmetic (`RecurrenceCalculator.NextOccurrence`), besides the series route's check that a
changed schedule still has a date ahead. Pull request #197 ("feat:
free slots count this server's CalCrony events"), open as this is written, would count a
series' later occurrences in free-slot lookups by the same rule as the calendar. How many series
of this kind run on the hosted instance was not looked at: that needs a read of its database,
which is Joshua's to allow.

### The cause

The scheduler (`DeliveryScheduler`) gives a series that has no posted occurrence left its next
one through `SeriesMaterializer.MaterializeNext`, which asks `RecurrenceCalculator.NextOccurrence`
for the first date that begins after the moment of the pass — so after the occurrence before has
ended. (The pass runs every 15 seconds by default; with lengths in whole minutes, that matters
only when a pass is late.) The calendar follows this rule through `SeriesProjection.Upcoming`
since #196. The feed (`FeedEndpoints.GetFeed`) takes the first upcoming date with lines of its
own, from the end of the posted occurrence, and writes the rest as one repeat rule
(`IcsRecurrence.BuildPattern`), which can only name every date of the schedule. The create route,
the edit route and the series route check a length against its range alone
(`Validation.BadDuration`: a minute to four weeks), not against the gap between the dates.

## 2. What a subscriber and a creator should see

Described for the answers recommended in section 5.

- **A feed subscriber** sees the occurrences the series will post, as a calendar visitor does:
  the first upcoming date, and every one after it.
- **A creator** cannot give an occurrence the length of the gap between two dates of its
  schedule, or more. The refusal says how long an occurrence of that schedule may last — "at most
  23 h 59 min" for a daily event — in Discord and on the web, at creation and on a change of the
  whole series' length or of its schedule. A length close to the gap can still hold back the date
  after a night the clocks go forward (row 9); the calendar and the feed show that day as it will
  be.
- Series that already run are left as they are, and the calendar and the feed show what they
  post.

## 3. The change

Described for the answers recommended in section 5.

**The feed onto the helper.** `GetFeed` takes the moment a series can post from
(`SeriesProjection.FreeFrom`) and its first upcoming date (`SeriesProjection.Upcoming`) from the
helper, in place of its own lines; the count it checks today is the helper's. The end of the
posted occurrence is still worked out by `EventTimes.End`, and so is the end of every entry. For
a series whose dates are never held back, the feed writes, byte for byte, what it writes today.

**One place for an event's end.** The dates after the first come from the helper too, which works
out each occurrence's end with `SeriesProjection.EndOf`. `EndOf` calls `EventTimes.End`, which the
scheduler and the feed already use, so that the API has one place for an event's end, and
`SeriesProjection`'s own copy of the hour a length defaults to goes. That is the edit of a
separate change: if it has landed by then it is taken in; if not, this change makes the same edit
in the same way, and whichever of the two is merged second takes the other in. Pull request #197 adds to `SeriesProjection.cs` as well, and is
taken in the same way.

**The shortest gap of a schedule.** One new function beside the schedule's other arithmetic in
`RecurrenceCalculator`: the fewest days between two dates of a schedule as the calendar shows
them — the interval for a daily one; seven days times the interval for a weekly one, or with a
day set the nearest two of its days, across the end of the week too; at least 28 days times the
interval for a monthly one (February's length, and four weeks for a weekday of the month); 365
days times the interval for a yearly one. A length is at most four weeks, so of the monthly and
yearly schedules only a monthly one that repeats every month can be reached, and only by the
longest length.

**The refusal (Q2 = A, Q3 = A).** A length that reaches the shortest gap of the schedule — that
many days of 24 hours, or more — is refused:

- by the create route, for an event with a repeat rule, its own or its template's, against the
  length the event ends up with;
- by the edit route, for a change of the whole series' length (one occurrence alone may still be
  made longer: the calendar and the feed already show the dates it holds back);
- by the series route, for a change of the schedule, against the series' length (an hour when it
  has none).

The answer is 400 with a message that names the longest length the schedule allows, for
example: "An occurrence would still be running when the next date begins, so that date would
never be posted: with this schedule an occurrence can last at most 23 h 59 min." The bot and the
web show it as they show every refusal (`❌ …` in Discord, the form's error on the web). Nothing
is stored or changed on a refusal.

**The feed (Q1 = A).** A series whose length can reach the shortest gap of its schedule, once the
clocks of its zone are allowed for, is written as entries of its own: one for each occurrence
`SeriesProjection.Upcoming` lists, from its first upcoming date to a year ahead, under the
calendar's step limit (`PublicCalendarBuilder.MaxStepsPerSeries`, 1,000), with no repeat rule.
Each entry has the series' title, place and description, with the link to the server's events
page, as the series' entry has today; it starts at the occurrence's start, written in UTC as a
posted occurrence is, and ends at `EventTimes.End`; and its UID is the series' with the
occurrence's date (`{series}-{yyyyMMdd}@calcrony`), so that a calendar app updates it in place
from one read to the next. Every other series keeps its one entry with the repeat rule.

"Once the clocks of its zone are allowed for" means: a length that falls short of the gap by less
than the largest step forward of the zone's clocks within that year counts as reaching it. For a
zone whose clocks do not change, such as UTC, that step is nothing. So the series written entry
by entry are those made before the refusal, and those whose length is within a clock change of
the gap (row 9) — the second kind the whole year round, not only near the night the clocks
change, so that its entries do not come and go. *Changed by Q4 = A:* only a series as long as its
gap is written entry by entry. One that only a clock change brings to its gap keeps its one entry
with the repeat rule, and the dates it holds back within the year are written into it as
exceptions (EXDATE), in the form of its start; when its count is used up within the year, the rule
ends on the last date it posts (UNTIL), and otherwise its count is raised by the dates left out.
Past the year, the next night the clocks go forward is not looked at yet: there the feed lists the
date after it, and a count that runs that far ends a date early, until the year ahead reaches that
night and the feed, read again, leaves the date out.

**What it costs**, since the feed is read without a login (its token is its key):

- No new query. A series that is not written entry by entry costs what it costs today, one step
  of its schedule, and a look at its zone's clock changes over the year ahead. *As changed by
  Q4:* one that a clock change brings to its gap costs a step for each date of that year as well
  (`HeldBackDates`), about a tenth of a millisecond for a daily series, and its entry gains an
  exception or two.
- A series written entry by entry costs one step for each occurrence within the year: at most
  366, and about 183 for a daily series that holds every second date back. Listing two years of
  dates took 0.01 to 0.4 milliseconds in the six schedules tried on fixed dates (one run each, on
  this machine).
- The feed grows by one entry for each such occurrence, about 183 a year for a daily series that
  holds every second date back. *As changed by Q4 and by the review:* only a series as long as its
  gap is written so, and no route can make one now.

**Found in the checks (2026-11-18), and put to Joshua as Q4.** The size above holds for a series
as long as its gap, which holds every second date back. A series that only a clock change brings
to its gap — a daily one of 23 h 59 min in New York, the length the refusal names for a daily
event — holds back one date a year and posts all its other dates, so written entry by entry it
adds about 350 entries a year, and the refusal does not stop anyone making it. Measured through the
routes, for a server with 40 daily series: with 20 of them 23 h 59 min long in New York, 7,104
entries, 2.1 MB and about 850 milliseconds a read; with none, 80 entries, 28 KB and about 66
milliseconds in that run (34 in a later one; the times vary from run to run, the sizes do not). With 20 series a day long, made before the refusal: 3,600 entries, 1.1 MB, 0.2 to 0.5
seconds a read. Q4 asks what the feed should do instead for the series a clock change brings to
their gap; its answer, A, keeps them on their repeat rule (section 8).

**The Docs page.** Two sentences: under "Recurring events", that an occurrence has to end before
the next date begins, so a length that reaches the gap between two of the dates it repeats on is
refused, with the longest the schedule allows; and under "ICS subscribe feed", that within the
year ahead a date an event will not post is left out, and an event as long as the gap between its
dates (made before that was refused) is listed date by date. (As written after Q4 and the
review.)

**The release summary.** One bullet under `### What's new` in the summary file of the release
this lands in, as CONTRIBUTING's "Release summary" says: a repeating event can no longer last as
long as the gap between its dates, which made it post only every second date, and the calendar
feed lists, for the year ahead, only the dates a repeating event will post. That file is
`docs/release-notes/0.38.1.md`: 0.38.0 went out without this change, no release is open, and
after 0.38.0 a `fix:` makes 0.38.1.

## 4. What does not change

- The scheduler and what is posted, and when.
- The public calendar: it already shows what a series posts.
- One-off events: any length from a minute to four weeks, as today.
- One occurrence made longer on its own: accepted, as today.
- A first date on a day outside a day set, which the web form allows ("the first occurrence stays
  on its day"): the gap from it to the set's first day is not measured, as it is one occurrence,
  like one made longer on its own; the calendar and the feed show the date it holds back. (Found by
  the review.)
- On the web form, a change to the whole series cannot give it the length one of its occurrences
  already has: the form sends a length only when it was changed, so that a change of the title
  alone does not give the whole series that one occurrence's length. `/edit` in Discord can. (Found
  by the review.)
- Series that already run, whatever their length: they go on as they do, and the calendar and the
  feed list what they post. A change to them that touches neither their length nor their
  schedule is not refused.
- Templates are not checked when they are saved; one with such a length and schedule is refused
  when it is used, with the same sentence.
- The feed's entries for posted occurrences, and for every series that is not written entry by
  entry.
- The data: no migration, nothing stored.
- The bot and the web: no change; the refusal reaches them as every refusal does.

## 5. Questions

**Q1. What should the feed list for a series whose occurrences can hold its dates back?**

- A) The occurrences it will post, as entries of their own, one for each, from its first upcoming
  date to a year ahead, with no repeat rule; every other series keeps its one entry with the
  repeat rule, as today (section 3) (recommended: subscribers see exactly what will be posted,
  every calendar app reads plain entries alike, and a year keeps the feed small — about 183
  entries for a daily series that holds every second date back).
- B) Its one entry with the repeat rule, with the dates it will not post written as exceptions
  (EXDATE) for a year ahead, and a count turned into the date of its last occurrence. Beyond the
  year, every date of the schedule again.
- C) As today: the first upcoming date right, then every date of the schedule. The feed only moves
  onto the helper.
- D) Only the next occurrence it will post, as an entry of its own, and nothing after it.
- E) Other (please describe).

[Answer]: A

**Q2. What should happen when someone makes such a series?**

- A) Refuse it: creating a repeating event whose length reaches the gap between two of its dates,
  and changing a whole series' length or its schedule so that it does, is answered with a refusal
  that says how long an occurrence of that schedule may last; the bot and the web show it as they
  show every refusal. Series that already run are left as they are (recommended: such a length
  never does what the schedule says, the creator decides at the moment of choosing it, and no
  reply, command or page needs a new field).
- B) Accept it and say so: the reply to the creation or the change, in Discord and on the web,
  says which dates will not be posted. A new field in the replies of three routes, and new lines
  in the bot's replies, `/series info` and the web's event page.
- C) Accept it and say nothing at the time; the Docs page explains the rule.
- D) Post such a series differently: an occurrence that begins at the moment the one before it
  ends is posted too, so that a daily event a day long is posted every day. A change to the
  scheduler, with a plan of its own: it would have to post an occurrence whose start passed a
  few seconds before, and tell that apart from one missed while the scheduler was down. Longer
  lengths would still hold dates back.
- E) Other (please describe).

[Answer]: A

**Q3. If Q2 is A: which gap does the refusal measure?**

- A) The gap between the dates as the calendar shows them: a day for a daily event, a week for a
  weekly one, the nearest two days of a day set, 28 days for a monthly one. Where the clocks go
  forward, a length that falls short of that gap by less than the hour they move can still hold
  back the date after that night; the calendar and the feed show that day as it will be
  (recommended: a limit people can read off their own schedule, the same in every zone).
- B) The gap in hours, with the clock changes of the series' zone allowed for: in a zone that
  changes its clocks, a daily event may last at most 22 h 59 min, and so on.
- C) Other (please describe).

[Answer]: A

**Q4. Asked after the checks: how should the feed list a series that only a clock change brings
to its gap (section 3, "Found in the checks")?**

- A) As one entry with the repeat rule, with the date it holds back — the date after a night the
  clocks go forward — written into it as an exception (EXDATE), a year ahead, and its count or its
  end set to match; entries of their own stay for series as long as their gap, which can no longer
  be made (recommended: the feed stays exact for the year ahead, and what a server can still
  create no longer makes it grow).
- B) As one entry with the repeat rule, as before this change: the feed then lists that one date a
  year, which is not posted. Entries of their own only for series as long as their gap.
- C) As built: entry by entry, a year ahead, like a series as long as its gap.
- D) Refuse those lengths as well (Q3 = B after all): in a zone that changes its clocks, a daily
  event may last at most 22 h 59 min, and the feed lists entry by entry only series made before.
- E) Other (please describe).

[Answer]: A

## 6. Tests

Written first, for the answers recommended in section 5, and run on `master` before any change to
`src`: the new ones fail at their assertions, and the controls pass.

In `FeedTests` (through the routes):

1. A series whose occurrences hold every second date back is listed date by date: daily, 4 times,
   24 hours, made as a series made before the refusal is (its length set in the database) — the
   feed lists Monday, Wednesday, Friday and Sunday, each an entry of its own with no repeat rule,
   each ending 24 hours after it starts.
2. A day set: Mon, Wed, Fri, 6 times, 60 hours — the feed lists what the series posts.
3. Weekly, 3 times, 7 days.
4. Daily without an end, 24 hours: entries up to a year ahead, and none after.
5. A length within a clock change of the gap: daily, 23 h 59 min, across the next night the
   clocks go forward, created through the route (accepted under Q3 = A): the Sunday after that
   night is left out. *As changed by Q4 = A:* the series keeps its one entry with the repeat rule,
   with that Sunday as an exception and the rule ending on its last date (UNTIL), and the dates a
   calendar app reads from it are the ones the series posts. Two more, for Q4: the same length
   without an end leaves out that one date in the year and no other, and with a count of 500,
   which runs past the year, the rule keeps a count raised by the date left out. The night is
   whichever of New York's (March) and Sydney's (October) comes first after a week from the day the
   tests run, so it lies inside the year the feed looks ahead whatever that day is.
6. Control: daily, 23 hours, in UTC — one entry with the repeat rule, as today.
7. `A_series_is_projected_from_the_end_of_its_live_occurrence_by_that_occurrences_own_length`
   (it exists): its series, daily with occurrences of 60 hours, would be refused, so it is made
   as a series made before the refusal; its first upcoming date stays the third day, and it is now
   listed entry by entry.

In `PublicCalendarApiTests` (through the routes):

8. `A_series_whose_own_length_reaches_its_next_date_shows_what_the_series_goes_on_to_post` (it
   exists): made as a series made before the refusal, and the feed is compared now as well — the
   calendar, the feed and the series agree.

The refusal, through the routes, in a new class `SeriesLengthApiTests`:

9. Create, daily: 24 hours → 400, the sentence naming 23 h 59 min; 23 h 59 min → 201. Every 2
   days: 48 hours → 400, naming 1 day 23 h 59 min; 47 h 59 min → 201.
10. Create, weekly: 7 days → 400; Mon and Tue with 24 hours → 400; Fri and Mon with 72 hours →
    400 (three days, across the end of the week); Mon and Thu with 71 h 59 min → 201.
11. Create, monthly: four weeks → 400; four weeks less a minute → 201.
12. Create from a template whose schedule is daily and whose length is 24 hours → 400.
13. Edit: the whole series of a daily event made 24 hours long → 400, and nothing changed; that
    occurrence alone made 24 hours long → 200.
14. Series route: a weekly series of 36 hours given a daily schedule → 400, and nothing changed;
    given a schedule of every 2 days → 200.
15. Control: a one-off event of four weeks → 201 (`ValidationApiTests` already holds it).

Written with the code, since they call what it adds:

16. `RecurrenceCalculatorTests`: the shortest gap of each kind of schedule — daily every 1 and 3
    days; weekly every 1 and 2 weeks; day sets Mon and Tue, Mon and Thu, Mon and Sun (one day,
    across the end of the week), a day set every 2 weeks; monthly by date and by weekday; yearly.
17. The feed's choice of a series to write entry by entry, on its own: by length, schedule and
    zone, with a zone that changes its clocks and one that does not. For Q4 as well: which series
    last their gap (`SeriesProjection.LastsItsGap`), and the dates a series holds back within a
    time (`SeriesProjection.HeldBackDates`) — after a night the clocks go forward, with and
    without a count, every second date of a series a day long, and none past the time looked at.
18. `DocsPageTests`: the Docs page's two sentences.

Added by the review (section 8): in `SeriesLengthApiTests`, a new schedule and a new length for
the whole series sent together twenty times, one accepted each time; an ended series as long as
its gap revived only with dates further apart; a monthly series on the 30th four weeks long,
accepted. In `FeedTests`, an entry listed on its own in Chicago at 19:00 — the date in its UID is
Chicago's, its start is written in UTC, and it carries the series' description, place and link;
and a series whose rule cannot be stepped, left out while the feed still answers. In
`RecurrenceCalculatorTests`, the gap of monthly dates on the 15th, 28th, 29th, 30th and 31st and by
weekday, and every day of the month stepped for eight years. In `SeriesHoldBackTests`, every date
the schedule names is either listed by `Upcoming` or held back, never both. In a new
`EventFormLengthTests` (web), the edit form sends the length only when it was changed.

## 7. Steps

### 0. Baseline
- [x] 0.1 The whole suite on this branch before any change to `src`: green, and the Release build
  with no error and the known warnings. Run on 2026-11-18 on `master` 4242144: the Release build
  has no error and 22 warnings; 2,591 tests pass (1,598 API, 490 bot, 154 web, 349 for the
  release-notes tool) and none fails; seventeen of the tool's tests are skipped on this Windows
  machine and run in CI.

### 1. Tests
- [x] 1.1 Tests 1 to 15 of section 6, run on `master`: the new ones fail at their assertions, and
  the controls pass. Run on 2026-11-18: 22 fail, each at its assertion — the refusals answer 201
  or 200 where 400 is expected (eleven schedules at creation, the edit route, the series route, the
  series made before and the template), the feed tests find a repeat rule where none should be,
  and the calendar's test 8 finds the feed listing a date the series does not post — while test 6,
  test 15 and the fifteen feed tests that were there before pass.

### 2. Code
- [x] 2.1 `SeriesProjection.EndOf` calls `EventTimes.End`, and the helper's own copy of the
  default length goes: taken in from `master` if the separate change has landed, or else made here
  in the same way. It had not landed: the edit is made here, to the letter of that change's own,
  so that whichever of the two is merged second meets the same lines. That change is now open as
  pull request #210; a trial merge of the two leaves `SeriesProjection.cs` as this branch has it,
  with conflicts only in the records.
- [x] 2.2 `RecurrenceCalculator`: the shortest gap of a schedule (`ShortestGapDays`).
- [x] 2.3 The refusal, in `Validation` (`LengthReachesNextDate` where a length is chosen,
  `ScheduleReachesLength` where a schedule is), called by the create route, the edit route (the
  whole series, when its length changes) and the series route (when its unit, interval or day set
  changes).
- [x] 2.4 `FeedEndpoints.GetFeed`: the first upcoming date from `SeriesProjection`; a series whose
  length can reach its gap (`SeriesProjection.CanHoldDatesBack`) written entry by entry for a year
  ahead (`FeedEndpoints.EntryByEntryDays`, 366); the summaries say so.
- [x] 2.5 Tests 16 and 17; tests 7 and 8 changed as section 6 says. Test 16 has a second part
  that steps every day set, every one to three weeks, through the calculator and finds the
  shortest gap to be the smallest step it takes; test 17 has a part that compares, for daily and
  Mon + Wed series of every length from three hours short of their gap to a day and a half past
  it, in UTC and in New York, the helper's dates with the schedule's: wherever a date is left out,
  the choice says a date can be held back.
- [x] 2.6 The Docs page's two sentences, and the release summary's bullet. The Docs page is done,
  with a test in `DocsPageTests` that holds both sentences. The bullet waited for the file it goes
  in: the open release, 0.37.2 (pull request #200), had 55 of its 1,800 characters left. 0.37.2
  was then released, and the bullet is in `docs/release-notes/0.38.0.md`, the file of the release
  now open (pull request #207): 1,142 of the 1,800 characters, and the repository's check reports
  no problem in the folder's four files. 0.38.0 was released in turn, during the review, without
  this change; no release is open, so the bullet is in a file of its own,
  `docs/release-notes/0.38.1.md`, the version a `fix:` makes after 0.38.0, as CONTRIBUTING says:
  343 of the 1,800 characters, and the check reports no problem in the folder's five files.
- [x] 2.7 Q4 = A: `SeriesProjection.LastsItsGap` and `HeldBackDates`; the feed writes entry by
  entry only a series that lasts its gap, and gives any other that can hold a date back its
  exceptions, with its count raised or its rule ended on its last date; the Docs page's feed
  sentence says so. Its tests were written first and failed on the code before it (the clock-change
  series came back as four entries, and as 252, where one was expected).

### 3. Checks
- [x] 3.1 The tests of section 6 pass; the whole suite is green; the Release build has no new
  warning. On `fce8c75`: 2,650 tests pass and none fails, with the same 22 warnings. With Q4's
  change, a 23rd warning, a possibly null UID read in a new feed test, was put right before the
  commit was kept. On the tree with `master` (`f05e1d0`) taken in: 2,728 tests pass and none fails
  (1,724 API, 499 bot, 155 web, 350 for the release-notes tool; seventeen of the tool's tests run
  in CI only), and the Release build has no error and the same 22 warnings. With the review's
  answers, on the tree of the one commit the branch is rebuilt into: 2,744 tests pass and none
  fails (1,738 API, 499 bot, 157 web, 350 for the release-notes tool), and the Release build has no
  error and the same 22 warnings as `master`. With the answers to the third read: 2,745 tests pass
  and none fails (1,739 API, 499 bot, 157 web, 350 for the release-notes tool), and the Release
  build has no error and the same 22 warnings. On `master` `f6152d7` (release 0.38.0), with the
  bullet in 0.38.1's file: 2,746 tests pass and none fails (1,739 API, 499 bot, 157 web, 351 for
  the release-notes tool, whose check of every summary file has one file more to read), and the
  Release build has no error and the same 22 warnings. On `master` `dab6a31` (pull request #209
  taken in, with tests of its own): 2,762 tests pass and none fails (1,755 API, 499 bot, 157 web,
  351 for the release-notes tool), and the Release build has no error and the same 22 warnings.
  Pull request #211 (`11afb47`), taken in after that, changes only records, so the result stands.
- [x] 3.2 Each part taken out in turn, in a scratch copy — the refusal in each of its three
  routes, the entry-by-entry listing, the allowance for the clocks, the year's reach — and the
  tests that fail for each written down. Fifteen parts, those of Q4 among them; section 8 lists
  each with the number of tests that fail without it. Every one fails the tests written for it and
  no others.
- [x] 3.3 The rows of section 1 run again in the scratch tests: the feed, the calendar and the
  series agree in every row; and the feed of every existing `FeedTests` and `DaySetApiTests` case
  is as it was. Run through the routes on dates ahead (the fixed dates of section 1 moved to a
  Monday ahead where they had passed), with Q4's change: in all twelve rows the dates the feed
  lists, read back with Ical.Net, the calendar's month views and the series' posts are the same.
  Series as long as their gap are written entry by entry; the New York series of 23 h 59 min is one
  entry with the repeat rule and its exception; the others are one entry with the repeat rule, as
  before. The existing feed and day-set tests pass unchanged.
- [x] 3.4 The time and the size of a feed with series written entry by entry, measured. Through
  the routes, for a server with 40 daily series: with none of them long, 80 entries and 26 to 28
  KB; with 20 of them a day long, made before the refusal, 3,600 entries, 1.1 MB and 0.2 to 0.5
  seconds a read, as section 3 says; with 20 of them 23 h 59 min long in New York, 7,104 entries,
  2.1 MB and about 850 milliseconds a read before Q4's change, and 80 entries, 29 KB and about 28
  milliseconds after it, as without them.
- [x] 3.5 The route tests build their dates from the day they run on: the texts they hand to the
  date reader tried for every day to the end of 2032, as the moved-occurrence fix did. The new
  feed tests' texts are those of `PublicCalendarApiTests` (a Monday ahead, already tried by that
  fix) and those of the clock-change tests, three days before the chosen night at 09:00 and 18:00:
  for every day from 2026-11-18 to 2032-12-31 at 00:00, 12:00, 18:00 and 23:59:59 UTC, 17,888
  texts, each read as written; the chosen night is never more than 216 days ahead. The refusal
  tests use `in 3 days`.
- [x] 3.6 New source scanned for invisible characters: none among the lines the branch adds.

### 4. Records
- [x] 4.1 The release summary's bullet and the repository's check on it; this plan's status and an
  "As built" section; `aidlc-state.md`; `audit.md`. The bullet is in
  `docs/release-notes/0.38.1.md` (343 of 1,800 characters; the repository's check finds no
  problem in the folder's five files).

### 5. Review before any push
- [x] 5.1 Two review agents read the local commits — one for security and privacy, one for code
  quality and accuracy — and their findings are answered in a commit they read again, until
  neither has a finding. First read at `853c78b`, second at `1b43098`, third at `b61ef7f`, fourth
  at `22fa64c`, and a last read at `aa6c98f`, in which neither found anything that should hold up
  a push; the answers to each are in section 8, "Review". Both read the records after it before
  the push.

Then the completion gate ("Code generation complete. Choose: A) Request Changes  B) Continue to
Next Stage"), and after it, each on Joshua's own go-ahead: the push and the pull request, and the
merge. Before the push `master` is merged in, with the entries of `audit.md` kept in the order of
their timestamps and master's text unchanged.

## 8. As built

Generated on 2026-11-18, after the approval, and changed the same evening by Joshua's answer to
Q4 ("Yes, A") and on 2026-11-19 by the answers to the review. `master` is taken in (`f05e1d0`:
release 0.37.2 and pull request #197; then `b1f5c58`: pull request #208; then `f6152d7`: release
0.38.0; then `dab6a31`: pull request #209; then `11afb47`: pull request #211, records), and the
branch is one commit on it.

**The change** is the one of section 3 for the answers given, Q4's included:

- `RecurrenceCalculator.ShortestGapDays`: the fewest days between two dates of a schedule, as
  section 3 describes it.
- `Validation.LengthReachesNextDate`, where a length is chosen, and
  `Validation.ScheduleReachesLength`, where a schedule is, each with its sentence; called by the
  create route, by the edit route for a new length of the whole series, and by the series route
  for a new unit, interval or day set. A length a series already has, or its schedule sent again
  unchanged, is no change and is not checked, so a series made before can still be changed in
  everything else.
- `SeriesProjection.EndOf` calls `EventTimes.End`, and the helper's own copy of the default
  length is gone: the edit of the separate change that does this, which had not landed, made here
  to the letter.
- `SeriesProjection.CanHoldDatesBack`, `LastsItsGap` and `HeldBackDates`, with the record
  `HeldBack`, placed after the helper's other members.
- `FeedEndpoints.GetFeed`: a series' first date from the helper. A series that lasts its gap is
  written entry by entry, `EntryByEntryDays` (366) ahead. Any other keeps its one entry with the
  repeat rule; one that can hold a date back within that time has the dates it holds back written
  into it as EXDATEs in the form of its DTSTART, with its COUNT raised by them, or its rule ended
  (UNTIL) on its last date when its count is used up within that time.
- The Docs page's two sentences; the release summary's bullet in `docs/release-notes/0.38.1.md`.
- Changed by the review (its findings and answers are under "Review" below): the edit and series
  routes check under a lock on the series row, taken before the event row's and after the
  caller's right to make the change is checked; a new start for the whole series is checked from
  its new date, unless the series reaches its gap already and keeps the length it has; reviving an
  ended series is checked like a new one; the feed works out each series on its own; a monthly
  date on the 29th or 30th has a gap of 29 days; and the web form sends a length only when it was
  changed.

**The tests** are those of section 6, with Q4's: test 5 expects the repeat rule with its
exception, two feed tests are added for a series without an end and for a count past the year —
where a calendar app reads 499 dates in all for the 499 occurrences left, the count counting the
date left out as well — and the builder tests hold `LastsItsGap` and `HeldBackDates`.

**What differs from the plan as approved**

1. *Q4*, asked after the checks and answered A: section 3 and section 5.
2. *`master` taken in, five times, and the branch rebuilt as one commit.* 0.37.2 was released
   (pull request #200) and pull request #197 merged while the code was written; after the
   review, pull request #208 (records) merged as well, and the branch is rebuilt as one commit on
   that `master` (`b1f5c58`) for the review's second read, and again after each later round;
   during the third, 0.38.0 was released (pull request #207, `f6152d7`), and after the completion
   gate pull requests #209 (tests of its own, and records) and #211 (records) merged, so the
   commit stands on that `master` (`11afb47`) now. #197 adds to `SeriesProjection.cs`: the file is `master`'s, with this
   change's additions after its own and the separate change's edits kept line for line, the one
   more doc line it has changed since included. The audit log is `master`'s entries with this
   change's placed among them by their timestamps; the state file is `master`'s, with this
   section after the one #197 added. The release summary's bullet went into the file of the
   release then open, 0.38.0, as CONTRIBUTING says, not into 0.37.2's; 0.38.0 went out without
   this change, so the bullet is in a file of its own, 0.38.1's.
3. *The clock-change tests' night.* A first version took New York's next night alone, which on a
   few days of each year lies past the year the feed looks ahead. They now take whichever of New
   York's and Sydney's nights comes first.
4. *Two expectations in the new tests were wrong and are corrected*, each found by its first run:
   Mon and Sun every third week are six days apart (Monday to Sunday), not one; and the dates
   `HeldBackDates` gives begin at the first date the helper lists, so the Tuesday held back by the
   posted occurrence before it is not among them.

**Checks** (section 7 has each in full)

- Baseline on `master` 4242144: 2,591 tests pass, and the Release build has 22 warnings.
- Tests first: 22 fail on `master` at their assertions and the controls pass; Q4's three fail on
  the code before it.
- On the merged tree: 2,728 tests pass and none fails (1,724 API, 499 bot, 155 web, 350 for the
  release-notes tool; seventeen of the tool's tests run in CI only), and the Release build has no
  error and the same 22 warnings. With the review's answers, as one commit on `master` `b1f5c58`:
  2,744 tests pass and none fails (1,738 API, 499 bot, 157 web, 350 for the release-notes tool),
  with the same 22 warnings as `master`; the release summary passes the repository's check. With
  the answers to the third read: 2,745 tests pass and none fails (1,739 API), with the same 22
  warnings. On `master` `f6152d7`, with the bullet in 0.38.1's file: 2,746 (351 for the
  release-notes tool, which reads one summary file more), with the same 22 warnings; the check
  finds no problem in the folder's five files. On `master` `dab6a31`, with pull request #209's
  tests: 2,762 (1,755 API), with the same 22 warnings; `11afb47` adds records only.
- Each part taken out in turn, in a copy of the change as it stood before `master` was taken in
  (`2d096ed`), each time with the 234 tests of the feed, the calendar, the series, the templates, the
  refusal, the helper and the calculator. Every part made the tests written for it fail, and no
  others: the refusal at creation (2), the template's length in it (1), the refusal of a whole
  series' new length (2), a length the series already has counted as a change (1), the refusal of a
  new schedule (2), the schedule sent again counted as a change (1), the entry-by-entry listing of
  a series as long as its gap (7), entry by entry for a series a clock change brings to its gap too,
  as before Q4 (3), the held-back dates handed to the series' entry (3), the exceptions written (3),
  the rule ended on its last date (1), the count raised (1), the allowance for the clocks (6), the
  year's reach made ten years (3), and the gap across the end of the week (3).
- The rows of section 1, through the routes: the feed, the calendar and the series agree in all
  twelve.
- A feed with 20 daily series of 23 h 59 min in New York among 40: 80 entries, 29 KB, about 28
  milliseconds a read, as without them.
- The tests' date texts: 17,888, each read as written, to the end of 2032.
- No invisible characters in what the branch adds.

**Review**

Two review agents read the branch before any push, at `853c78b`: one for security and privacy,
one for code quality and accuracy. Neither found a critical or high finding.

*Security and privacy.* Confirmed by reading and by runs of its own through the routes: nothing
the routes accept leads to a new error, nothing new is disclosed, and no one may do anything they
could not before. Its findings:

- S1 (medium). A new schedule and a new length for the whole series, sent at the same moment,
  could each pass its own check, and both changes were saved: in 29 of 30 tries a weekly series of
  two hours became daily and three days long. *Answered:* both routes take a lock on the series row
  before they read it for the check — the edit route after the event row it already locks, reading
  the series again; the series route in a transaction of its own. A test sends the two changes
  together twenty times; one is accepted each time, and taking either lock out makes it fail.
- S2 (low; medium while S1 stood). A series listed entry by entry costs far more a read than one
  with a repeat rule — with the longest description a series can have, about 0.85 MB a read for a
  daily series a day long — and an ended one could be revived with its schedule sent again, which
  was not checked. *Answered:* reviving an ended series is checked like a new one (a test). With S1
  answered no route can make a series as long as its gap, so there can only be fewer of them. How
  many the hosted instance has was not looked at: that needs a read of its database, Joshua's to
  allow, before the rollout. No limit on the feed is added.
- S3 (info). Working out the dates a clock change holds back costs about a tenth of a millisecond
  a read for a daily series. No change.
- S4 (nit). The account of the finding behind Q4 read like a way to make a feed large.
  *Answered:* reworded; Q4's answer closed it.
- From runs with values no route stores. A day set that names no real day made the gap function
  fail, and the whole feed with it: it is read as no set now (test 16). For a rule that cannot be
  stepped, the feed now works out each series on its own and leaves out, and logs, one it cannot
  step, as free-slot lookups do (a new feed test).
  A count raised by a date left out now stops at the largest count there is.

*Code quality and accuracy.* Confirmed by runs of its own: the feed of twenty series that hold
nothing back, in four zones, is the same as `master`'s; and in every case it tried, the dates a
calendar app reads from the feed are the ones the series posts. No critical, high or medium
finding. Its findings:

- Q1 (low). A first date on a day outside a day set is not measured against the set's first day:
  a weekly series on Mondays begun on a Sunday, six days long, is accepted, and its first Monday is
  never posted. *Answered as a limit:* that is one occurrence, as one made longer on its own is;
  the calendar and the feed show the date it holds back. Section 4 says so, and the Docs page now
  speaks of the gap between "the dates it repeats on".
- Q2 (low). The web form sent the length with every edit, so renaming a whole series after one of
  its occurrences was made three days long was refused with a length message — and before this
  change, gave the whole series that length. *Answered:* the form sends the length only when it
  was changed, as it already did the multiple-RSVP switch (a web test).
- Q3 (low). No test held the date in an entry's UID where it differs from the date in UTC, or what
  an entry listed on its own carries. *Answered:* a feed test in Chicago at 19:00.
- Q4 to Q7 (low). Two test comments out of date; the Docs page and the release summary saying more
  than the year ahead; step 4.1 not ticked. *Answered.*
- Nits, all answered. Section 3's cost and Docs paragraphs were from before Q4. Two timings did not
  agree; they were measured in different runs, and section 3 now says so. The series route's
  refusal is two sentences, not one. A monthly date on the 29th or 30th is never nearer than 29
  days to the next, so a length of four weeks there is accepted now (a test, and the gap test
  steps every day of the month for eight years). Comments that claimed too much are plainer, a
  test's name says what it asserts, and the helper's new code is tidier. `HeldBackDates` steps
  like `Upcoming` rather than sharing one stepper, which would change the calendar's step limit;
  a test now holds that every date the schedule names is listed by one or held back by the other,
  never both. The race it noted is S1.

*Second read.* Both agents read the branch rebuilt as one commit, `1b43098`, and confirmed the
answers above by reading and by runs of their own: the race is closed (thirty pairs of changes,
none both saved), each answer fails its test when taken out, and the feed is unchanged for every
series that holds nothing back. Neither found a critical, high or medium finding. They found:

- S5 / N1 (low, both). A new start for the whole series was never checked, and since a monthly
  date on the 29th or 30th has a gap of 29 days, moving it to another day could leave a series as
  long as its gap: four weeks on the 30th, moved to the 15th, was accepted, alone or with that
  length sent in the same change. *Answered:* the edit route checks a new start from its new date,
  with the length the series will have; a test makes both changes, and the move the other way,
  from the 15th to a 30th, is accepted. Taking the check out makes it fail.
- S6 (nit). The series route took its lock before its permission check. *Answered:* it locks after
  the check, and reads the series again.
- S7 (info). The edit route locked the event row, then the series row. *Answered:* it takes the
  series row's lock first.
- N2 (low). Refusing to revive an ended series advised making its occurrences shorter, which an
  ended series cannot be. *Answered:* a revival is asked only for dates further apart (a test).
- Nits, answered. The whole suite on the rebuilt commit is recorded (step 3.1), and the one warning
  more it built with — a new web test's base class, which bUnit has replaced — is gone. "The
  change" above points to these answers. The raised count's limit has a test. The web form's
  length is a limit now written in section 4. The helper working out an end only when it steps on
  from it makes no difference anyone can see, and no test holds it. The first audit entry's
  quotation of the hand-over was shortened when the branch was rebuilt; the audit entry of this
  round says so.

*Third read.* Both agents read the rebuilt commit, `b61ef7f`, and confirmed the second read's
answers by reading and by runs of their own: the move off the 30th is refused, alone and with that
length, and the move the other way is accepted; of thirty pairs of changes sent together, none had
both saved; each lock taken out, and the series route's second read of the series, fails the test
that sends two changes together; and the feed of twenty series is the same as `master`'s. Neither
found a critical, high or medium finding. They found:

- N3 (low; the security agent found the same). The check of a new start also refused moving a
  series made before the refusal, already as long as its gap, with the length it has: a daily
  series a day long, moved an hour later for the whole series, was refused. Section 4, "The
  change" above and the state file say such a series can still be changed in all but its length
  and schedule, and Q2's answer refuses a change that makes a series reach its gap. *Answered:* a
  series that reaches its gap already may be moved with the length it has; with a new length, or
  when the move itself brings it to its gap, it is checked as before. The test of a series made
  before now moves it, and the same move with a length that still reaches is refused; S5's test is
  as it was. Taking the exception out, or not counting a new length as a reason to check, makes
  that test fail and no other of the 21 length tests.
- S8 / N4 (nit, both). The edit route took its locks before its permission check, which the series
  route no longer does (S6). *Answered:* it reads the event's server, creator and series, checks
  the caller, and only then locks the series row and the event row and reads them. A test holds
  the series row while a member who did not make the event, and a user of another server, change
  the whole series: both are answered at once. With the check back after the locks, that test
  fails and no other of the 21 length tests.
- N5 (nit). A code comment on the order of the locks said more than the order. *Answered:* it says
  the order alone.

*Fourth read.* Both agents read `22fa64c`, the answers to the third read, and confirmed them by
reading and by runs of their own. The exception moves only a series that reaches its gap already:
a daily series a day long, made before, is moved alone or with 10 h or 23 h and refused the move
with 30 h; a monthly one on the 15th, four weeks long, made before, is moved to a 30th and then
refused the move back; and S5's moves stay refused. A caller who may not change the event is
answered at once while the series row is held, and one who may still waits for it; of thirty pairs
of changes sent together, none had both saved; each part taken out fails only the test written for
it; and the feed of twenty series is the same as `master`'s. Neither found anything in the code or
the tests. They found:

- R1 (low; the security agent noted the same). 0.38.0 was released (pull request #207, merged at
  2026-11-19T01:24:11Z as `f6152d7`) before `22fa64c` was made, and the release summary's bullet
  and the records still treated 0.38.0 as the open release. *Answered:* the commit is rebuilt on
  `f6152d7`; `docs/release-notes/0.38.0.md` is as released, and the bullet is in
  `docs/release-notes/0.38.1.md`, the version a `fix:` makes after 0.38.0, as CONTRIBUTING says;
  the plan, the state file and the commit message say so.

*Last read.* Both agents read `aa6c98f`, the commit rebuilt on `f6152d7`, and the pull request's
draft text; neither found anything that should hold up a push. The quality agent's four nits on
the draft text are answered in it, and the first of them in the commit message as well: a series
made before can be changed in all but its length and schedule while it runs.
