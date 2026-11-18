# Text that cannot be stored, and lists with an entry missing — functional note and code-generation plan

**Task**: request text that cannot be stored is refused with a sentence, and an RSVP option list
with an entry missing is refused with the sentence the options already have. Handed over on
2026-11-17 from another session.
**Baseline**: `master` eb6afb8 — the application code of v0.37.1. Run as step 0.1. After the
review the branch was rebuilt on `master` `162fd42` (section 7), and `master` `47209ab` was
merged into it before the push (step 6.1).
**Branch**: `fix/unstorable-text` (local) → PR `fix: refuse text that cannot be stored and lists
with an entry missing`.
**Status**: written 2026-11-17 and approved by Joshua the same day, with Q1, Q2 and Q3 answered A
(section 3). Generated on 2026-11-17, reviewed twice and confirmed; the completion gate was
answered B (continue) on 2026-11-18, and delivery is under way (section 6).

A small fix, so the workflow runs at minimal depth: the task is the requirement; no user stories,
application design, units, NFR or infrastructure stages; one unit, and this file is both its
functional note (sections 1–3) and its code-generation plan (sections 4–6), approved together.

## 1. What this change ships

1. **Text that cannot be stored.** Request text that carries a character the database cannot
   store is refused with a 400 and one sentence, `The text contains a character that can't be
   saved.` Where it is refused is Q1: once, where request text enters the API (recommended), or
   field by field.
2. **An RSVP option list with an entry missing.** A new or edited event whose `rsvpOptions` has
   an entry that is missing altogether is refused with `Every RSVP option needs an emoji and a
   label.` — the sentence an entry without an emoji or a label already gets.
3. **Three refusals of the same kinds, beside those two** — only with Q2 answered A: a template's
   notification list with an entry missing (2.3); a return address on the sign-in route that is
   longer than can be stored, or with a space or a character outside printable ASCII (2.4); and
   the title of an event made from a poll, where it is cut to length (2.5).

Text that can be stored is read, stored and sent back exactly as before, and every list that is
accepted today is accepted as before. The change is in the API. No migration, no configuration,
nothing new stored. Contracts, the bot and the web app are not changed: they show the API's
sentence as they show its other refusals.

Not in this change, whichever way the questions are answered: the date readers
(`NaturalDateTimeParser`, `RsvpPolicy.TryParseClose`), which pull request #184 changed.

## 2. Rules

### 2.1 Text that cannot be stored (as written here: Q1 answered A)

1. New: text in a JSON request body that the database cannot store is refused while the body is
   read, in every text field of every route, for the bot and for a web member alike. The answer is
   a 400 with `The text contains a character that can't be saved.`
2. New (with Q2 answered A): the same holds for text in the query string of an address, on every
   route, with the same answer. It is checked before the route runs.
3. The refusal comes before the route's own checks, as the refusal of a body the API cannot read
   does today, and nothing is stored or changed: a caller the route itself would answer with a
   403 or a 404 — a member of another server, say, or an event that does not exist — is answered
   with this 400. A request without credentials is answered 401 as before, and a web caller of a
   route only the bot may use 403 as before: the check sits behind authentication and
   authorization. *(Reworded after approval: section 7.)*
4. As before: all other text is read exactly as it was, in every field, and what the API sends
   back is written exactly as it was.
5. A consequence of refusing it where text enters: it is refused wherever a request carries it,
   whether or not anything would store it — in date text (an event's start, RSVP cutoff and repeat
   end, a series' repeat end, a poll's close, a reminder's time, the parse route, free slots),
   where today most of the readers read past such a character; in a reminder's text, which today
   keeps it; in a role name the bot reports for a role the API does not keep; in a field a body
   gives twice; and in any query string, whatever the route does with it, a route that does not
   exist included. *(Reworded after approval: section 7.)*
6. New: the same holds for the path of an address, checked with its query string before the
   route runs. *(Added after approval: section 7.)*

With Q1 answered B the same sentence is given field by field instead: `Validation` gets the check
and each route applies it to the text it stores, beside its length check. Rules 2, 3 and 5 then
fall away — text that is read and not stored is left as it is — and section 4 lists more files.

### 2.2 An RSVP option list with an entry missing

1. New: on a new event and on an edited one, for the bot and for a web member, an `rsvpOptions`
   list with an entry that is missing altogether is refused with a 400 and `Every RSVP option
   needs an emoji and a label.`
2. It is the first thing checked about the options: such a list gets this sentence whatever else
   the options carry, and on a new event whatever the shorthands beside them carry, since a new
   event checks those with the options. Everything a route checks before it reaches the options
   keeps its place: the caller and the start time on a new event, and on an edit the shorthands
   too, which an edit checks first. *(Reworded after approval: section 7.)*
3. `RsvpPolicy.TryBuildOptions` gives the same answer itself, after its check of how many entries
   the list has, so every caller of it is covered.
4. As before: a list without such an entry is read exactly as it was — the defaults when no list
   is sent, and every existing refusal with its own sentence.

### 2.3 A template's notification list with an entry missing (only with Q2 answered A)

1. New: on an edited template, a `notifications` list with an entry that is missing altogether is
   refused with a 400 and `Every notification needs minutesBefore.`
2. As before: a list of more than five entries is refused first, with its own sentence; then
   each entry's lead time and text are checked as they are today.

### 2.4 The return address of the sign-in route (only with Q2 answered A)

1. As before: `GET /auth/discord/start` keeps a `returnUrl` that is a path of the web app, and
   drops any other; sign-in then ends on the default page.
2. New: a `returnUrl` of more than 256 characters, the length that can be stored, is dropped in
   the same way. One of 256 characters or fewer is kept as before.
3. New: a `returnUrl` with a space or a character outside printable ASCII — a tab or another
   control character, or any character beyond ASCII — is dropped in the same way. The web app
   sends its addresses percent-encoded, so none of them is dropped. *(Added after approval:
   section 7.)*

### 2.5 The title of an event made from a poll (only with Q2 answered A)

1. As before: `POST /polls/{id}/convert` takes the title sent, or the poll's question, and cuts it
   to 128 characters when it is longer.
2. New: the cut keeps whole characters. Where the 128th and 129th places hold the two halves of
   one character, the title is cut to 127.

## 3. Questions

Q1 — Where text that cannot be stored is refused.

- A) Once, where request text enters the API: a converter on the API's JSON options refuses it
  while a body is read, and one check does the same for the query string (2.1 as written).
  **(Recommended. One place covers every text field of every route, the ones that exist and the
  ones added later. It is one new file and two lines in `Program.cs`, away from the files other
  open work is editing. Its price is rule 5 of 2.1: such text is refused in fields that are only
  read as well, where it is passed over today. Nobody can type that character, in Discord or in a
  browser.)** *(One clause shorter than when it was approved: section 7.)*
- B) Field by field: a check in `Validation`, applied by each route to each text it stores.
  Nothing that is accepted today is refused, and the sentence can name the field. The price is a
  check and a test for every stored text on every route, in ten files, and one to remember for
  each text field added later.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-17: "Approve, all A".

Q2 — How far this change goes. Rule 2 of 2.1 (the query string) and sections 2.3, 2.4 and 2.5
are beyond the event and poll text and the RSVP option lists the task names.

- A) They are part of this change. **(Recommended. They are refusals of the same kinds, a few
  lines each with their tests, and they then go out with this release, not in one of their own.)**
- B) They are left for a change of their own. This one stays with rule 1 of 2.1 and section 2.2.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-17: "Approve, all A".

Q3 — The time zone on the parse route. The task also puts forward, as optional, a limit on the
length of the `timeZone` that `POST /tools/parse-datetime` takes. Does it belong in this change?

- A) No: a change of its own. **(Recommended. The route already refuses a time zone it does not
  know, so nothing here depends on it, and it is better settled once, together with the other
  routes that take a time zone or a name to look up.)**
- B) Yes, in this change, on the parse route and on the two settings routes that take a time
  zone.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-17: "Approve, all A".

## 4. What changes, file by file (with Q1, Q2 and Q3 answered A)

| File | Change |
|---|---|
| `src/CalCrony.Api/Endpoints/StorableText.cs` | New. The rule, its sentence, the converter that applies it while a JSON body is read, and `UseStorableText`, which checks the path and the query string and gives the answer of 2.1. |
| `src/CalCrony.Api/Program.cs` | Two lines: the converter on the HTTP JSON options, and `app.UseStorableText()` after `UseAuthorization()`. |
| `src/CalCrony.Api/Services/RsvpPolicy.cs` | `TryBuildOptions`: rule 3 of 2.2; the sentence becomes one constant, used by the existing check and the new one. |
| `src/CalCrony.Api/Endpoints/EventEndpoints.cs` | `CreateEvent` and `UpdateEvent`: rules 1 and 2 of 2.2, where each first reads the options. |
| `src/CalCrony.Api/Endpoints/TemplateEndpoints.cs` | `UpdateTemplate`: 2.3, after the check of how many notifications there are. |
| `src/CalCrony.Api/Endpoints/AuthEndpoints.cs` | `Start`: rules 2 and 3 of 2.4, in the condition that drops a return address. |
| `src/CalCrony.Api/Endpoints/PollEndpoints.cs` | `ConvertPoll`: rule 2 of 2.5, where the title is cut. |
| `tests/CalCrony.Api.Tests/StorableTextApiTests.cs` | New: step 1.1. |
| `tests/CalCrony.Api.Tests/MissingListEntryApiTests.cs` | New: step 1.2. |
| `tests/CalCrony.Api.Tests/WebAuthTests.cs`, `PollApiTests.cs` | Steps 1.3 and 1.4. |
| `aidlc-docs/aidlc-state.md`, `aidlc-docs/audit.md` | The records. |

Not changed: Contracts, the bot, the web app, the database, every route's shape. No documentation
example becomes wrong, so none is edited.

Order with other work: pull request #184 has merged; it edited the date readers
(`NaturalDateTimeParser`, `RsvpPolicy.TryParseClose`) and three test files. This change edits
`RsvpPolicy.TryBuildOptions` and adds test files of its own, so no conflict in the code is
expected; `master` is taken in before the push, and the two records files are merged by hand,
entries by timestamp.

## 5. Execution plan

A box is ticked in the turn its step completes.

**0. Baseline**
- [x] 0.1 On `master`, before the first change: `dotnet build CalCrony.slnx -c Release` and
      `dotnet test CalCrony.slnx -c Release` green; counts recorded. *(Run on 2026-11-17 on
      `47c8a15` before approval, because it changes nothing, in a checkout of its own: 0 errors and
      22 warnings, the known baseline; 1,729 tests passed and none failed — 813 API, 418 bot, 153
      web, 345 release notes — with 17 more of the release-notes project skipped by their own
      conditions on this machine. Run again on `eb6afb8` once the branch was rebuilt there:
      section 7.)*

**1. Tests first** — written and run against the unchanged code
- [x] 1.1 `StorableTextApiTests`, as the bot and as a signed-in web member. Text that cannot be
      stored is refused with a 400 and the exact sentence in every text field a request body has:
      a row for each field of each route. The same for the query string. Pinned beside it: each
      of those requests is accepted when its text can be stored; other unusual text — control
      characters, line breaks, emoji — is stored and sent back as before; what the API writes for
      a set of texts is, byte for byte, what it writes today; a refused request stores nothing;
      and a request without credentials is still answered 401.
- [x] 1.2 `MissingListEntryApiTests`. An `rsvpOptions` list with an entry missing is refused with
      the exact sentence on create and on edit, for both callers, wherever in the list the entry
      is, and with the clear flags for the attendee role and for the signup restriction;
      `RsvpPolicy.TryBuildOptions` answers the same when called directly. *(Reworded after
      approval: section 7.)* With Q2 answered A: the same for a template's `notifications`.
      Pinned beside it: complete lists are accepted as before, and the lists that are refused
      today keep their own sentences.
- [x] 1.3 With Q2 answered A, `WebAuthTests`: a return address of 256 characters is kept and one
      of 257 is dropped, sign-in completing either way. *(Added after approval: one with a space
      or a character outside printable ASCII is dropped too.)*
- [x] 1.4 With Q2 answered A, `PollApiTests`: a title is cut to 128 characters as before, and to
      127 where the cut would otherwise split a character.
- [x] 1.5 The run on the unchanged code: every new refusal test fails, and every test that pins
      what is accepted today passes. *(Section 7 has the counts.)*

**2. The change**
- [x] 2.1 `StorableText.cs` and `Program.cs`: section 2.1.
- [x] 2.2 `RsvpPolicy.TryBuildOptions`, `EventEndpoints.CreateEvent` and `UpdateEvent`: section 2.2.
- [x] 2.3 With Q2 answered A: `TemplateEndpoints.UpdateTemplate` (2.3), `AuthEndpoints.Start`
      (2.4) and `PollEndpoints.ConvertPoll` (2.5).

**3. Verification**
- [x] 3.1 `dotnet build CalCrony.slnx -c Release` with no new warning; `dotnet test CalCrony.slnx
      -c Release` green; counts recorded against 0.1. *(Section 7 has the counts.)*
- [x] 3.2 Each rule taken out in a scratch copy fails a test meant to pin it. *(Nineteen changes in
      the end, each caught: the table is in section 7.)*
- [x] 3.3 The whole diff read against section 2, rule by rule.
- [x] 3.4 Two review agents read the local commits before anything is pushed, one for correctness
      and one for security and the public text. Their findings are answered, and section 7 says
      what changed.
- [x] 3.5 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 6. Delivery

Approving this plan covers the changes above and committing them locally. Everything that leaves
this machine waits for its own go-ahead.

- [ ] 6.1 *Go-ahead (the completion gate):* take `master` in if it has moved — the audit entries
      by timestamp, every section of the state file kept, the suite again and the reviewers' read
      of the result — then push the branch and open the pull request.
- [ ] 6.2 Checks green; a review if one starts. *Go-ahead:* squash merge. The test stack then
      takes `:main`.
- [ ] 6.3 *Go-ahead:* the release — the patch release that release-please opens — once it is
      settled with Joshua whether this change gets a line in the release summary
      (`docs/release-notes/<version>.md`).
- [ ] 6.4 *Go-ahead:* prod rollout, by the usual steps: backup, the pinned tag, app services only.
      No migration.

## 7. Notes from generation

- **Rebuilt on `master` `eb6afb8`.** `master` moved after the plan was written: pull requests
  #182, #186 and #184 merged, and release 0.37.1 (#185). Nothing had been pushed, so the branch was
  rebuilt there before generation: its one commit put back on top, the audit entries in their
  places by timestamp. The baseline line names `eb6afb8`, and the two mentions of pull request #184
  say that it merged; nothing else in the plan changed then.
- **Baseline (0.1).** On `47c8a15`, before approval: 1,729 tests passed (813 API). On `eb6afb8`,
  2026-11-17 from 23:31Z to 23:33Z: 0 errors and 22 warnings; 1,781 tests passed and none failed —
  865 API, 418 bot, 153 web, 345 release notes — with 17 release-notes tests skipped by their own
  conditions on this machine.
- **Tests first (1.1–1.5).** As first built, 239 new test cases: 198 in `StorableTextApiTests` — a
  row for each of 39 text fields that the bot and a member both send, for each of them, and for 9
  fields only the bot sends; 8 addresses; and the pins — 37 in `MissingListEntryApiTests`, 2 in
  `WebAuthTests` and 2 in `PollApiTests`. The server list (`PUT /guilds/presence/sync`) has a row in
  the refusal test and none in the test that sends the same request with valid text: a list it
  accepts marks every server it leaves out absent. Run against the unchanged code (`eb6afb8`): all
  132 refusal tests failed, and all 107 tests that pin what is accepted today passed. One pin was
  corrected before that run was counted: the test of what the API writes first compared it with the
  serializer's defaults, and the API writes with an encoder of its own, so it now compares with that
  encoder.
- **The change (2.1–2.3).** As section 4 says, with one addition: `RsvpPolicy.HasMissingEntry`, the
  one check the option builder and both event routes use, beside the constant
  `RsvpPolicy.IncompleteOptionError` that both of the builder's refusals now give. In `Program.cs`
  the JSON options' lambda becomes a block, to take the converter beside the NodaTime setup, and
  `UseStorableText` sits right after `UseAuthorization()`, as 2.1 rule 3 says. Added after the
  approval: it checks the path too (2.1 rule 6), and the sign-in route drops a return address
  with a space or a character outside printable ASCII (2.4 rule 3).
- **Verification (3.1–3.3).** As built, on `eb6afb8`: `dotnet build CalCrony.slnx -c Release`
  without incremental state with 0 errors and the baseline's 22 warnings, and `dotnet test
  CalCrony.slnx -c Release` with 2,020 tests passed and none failed (1,104 API). On the server
  itself, in Development and in Production, each refusal is answered with its sentence. The whole
  diff was read against section 2, rule by rule. The figures for the branch as it stands are at the
  end of this section.
- **Review (3.4), first reads.** Two agents read the three local commits up to `1ee6b5d`, each in a
  checkout of its own: one for correctness, one for security and the public text. They sent the
  requests in memory and to the server itself, compared the converter with the serializer's own
  handling of text over 241 read cases and 940 written values (the same, but for the text the rule
  refuses), and read every added line against the public-text rules. Answered:
  - Rule 3 of 2.1 now says that a caller a route itself would answer with a 403 or a 404 gets this
    400 instead, and that a route only the bot may use keeps its 403 for a web caller; tests pin
    both.
  - Rule 5 of 2.1 now names each place the refusal reaches where nothing would store the text.
  - Rule 2 of 2.2 now says that an edit checks its shorthands before its options, as the code and
    its test do; a test pins the same rule on a new event: an unreadable start time, and a member
    of another server, keep their answers.
  - A test pins rule 2 of 2.3: how many notifications there are is checked first.
  - `StorableTextApiTests` gains rows for the RSVP option emoji on an edit and for a series' repeat
    end, so that it has a row for each text field of each route; the refused request that stores
    nothing is now a reminder, whose text would otherwise be stored.
  - Records: step 1.2 names the clear flags it covers; pull request #184 edited the date readers;
    the state's workspace line names the `master` of that time.
  - Wording in the tests, in the summary of `StorableText` and in two code comments; and Q1's
    recommendation, which is one clause shorter than when it was approved.
  - Left as they are: the return address is measured in UTF-16 units, which can drop an address
    that would just fit and keeps none that would not; a refusal writes nothing to the log.
- **Added after the approval.** The path of an address is checked with its query string (2.1 rule
  6), and a sign-in return address with a space or a character outside printable ASCII is dropped
  (2.4 rule 3), each with a test. Both are changes of substance to the plan as approved, put to
  Joshua at the completion gate.
- **Review (3.4), second reads.** Both agents read the branch rebuilt on `162fd42`. Answered: a test
  that could not fail now can — the 403 of a route only the bot may use, with the text in the query
  string — and a missing pin is added: an event that does not exist, a 404 before and this 400 now;
  rule 3 of 2.4 says that a space is dropped too, as the code always did; every rule worded
  otherwise than when it was approved carries a marker; two more rule removals; and wording in three
  comments in two files, a test name, the test class's summary and the records.
- **As it stands.** `master` had moved to `162fd42` (pull requests #188 and #187); nothing had
  been pushed, so the branch is rebuilt there as two commits: `0c56280` (the source and the tests)
  and the records on top. On it: `dotnet build CalCrony.slnx -c Release` without incremental
  state, with 0 errors and the baseline's 22 warnings; `dotnet test CalCrony.slnx -c Release`,
  2026-11-18 from 01:13Z to 01:15Z, 2,037 tests passed and none failed — 1,118 API (the 865 of
  `master` and the 253 added), 418 bot, 153 web, 348 release notes — with 17 release-notes
  tests skipped on this machine. Run against the unchanged code, the 253 added test cases give
  139 failing refusal tests and 114 passing tests that pin what is accepted today. Nineteen
  changes that each take one rule out, made one at a time in a scratch copy of that commit and run
  against the four test classes, each failed tests written for that rule:

| Rule taken out | Tests that failed |
|---|---|
| The converter not added to the JSON options | 93: `A refused request stores nothing`; `Text that cannot be stored is refused` (91); `The refusal comes before a route checks its caller` |
| The query string not checked | 8: `Text that cannot be stored is refused in an address` (8) |
| The body's refusal not answered by the check | 93: `A refused request stores nothing`; `Text that cannot be stored is refused` (91); `The refusal comes before a route checks its caller` |
| The check placed before authentication | 2: `A request without credentials is still answered 401`; `A route only the bot may call still answers a member 403` |
| Text written with the serializer's default encoder | 1: `Response text is written as before` |
| Another character refused in its place | 103: `A refused request stores nothing`; `Other unusual text is stored and sent back as before`; `Text that cannot be stored is refused` (91); `Text that cannot be stored is refused in an address` (8); `Text that cannot be stored is refused in the path of an address`; `The refusal comes before a route checks its caller` |
| New event: no check in the route | 4: `A missing entry is answered first among the options`; `An rsvp option list with an entry missing is refused` (3) |
| Edited event: no check in the route | 16: `A missing entry is answered first among the options`; `An rsvp option list with an entry missing is refused` (15) |
| Option builder: no check | 2: `A stored option list with an entry missing gives the default options`; `The option builder refuses a list with an entry missing` |
| Option builder: the check before the count | 1: `The option builder refuses a list with an entry missing` |
| Template edit: no check | 7: `A missing notification is answered before each entry is checked`; `A template notification list with an entry missing is refused` (6) |
| Template edit: the check made with each entry | 1: `A missing notification is answered before each entry is checked` |
| Sign-in: no length rule for the return address | 1: `A longer return address is dropped` |
| Sign-in: the length rule one character short | 1: `A return address as long as can be stored is kept` |
| Poll to event: the title cut as before | 1: `Convert keeps whole characters where it cuts a title` |
| The path not checked | 1: `Text that cannot be stored is refused in the path of an address` |
| Sign-in: no rule for the return address's characters | 1: `A return address with a space or a character outside printable ascii is dropped` |
| Template edit: the check before the count | 1: `How many notifications there are is still checked first` |
| New event: the check before the caller and the start time | 1: `Checks a new event makes before it reaches the options keep their place` |

- **Review (3.4), confirmation.** Both agents read the two commits as they stand and confirmed
  them, with nothing left to fix; their last notes, on the wording of these records, are taken in.
- **`master` taken in before the push (6.1).** After the completion gate, pull requests #191 and
  #194 were merged, both records only. `master` `47209ab` is merged into the branch (`843be27`), so
  `0c56280`, named above for the source and the tests, stays in its history: the audit entries of
  both sides are kept whole, in timestamp order, and the state file merged by itself, every
  section kept. Nothing outside `aidlc-docs` changes, so the source and the tests are those of
  `0c56280`. On the merge, `dotnet build CalCrony.slnx -c Release` without incremental state gives
  0 errors and the baseline's 22 warnings, and `dotnet test CalCrony.slnx -c Release`, 2026-11-18
  from 03:59Z to 04:01Z, passes 2,037 tests and fails none (1,118 API, 418 bot, 153 web, 348
  release notes), with 17 release-notes tests skipped on this machine.
