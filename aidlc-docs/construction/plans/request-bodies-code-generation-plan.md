# Request bodies the API cannot read, snapshot lists with an entry missing, and what a refusal quotes back — functional note and code-generation plan

**Task**: three refusals in the API. A JSON request body in a character set the API cannot read is
refused with a sentence; a snapshot list from the bot with an entry missing altogether is refused
with a sentence; and what a refusal quotes back of a request is kept short, with a size limit of
the API's own for a request body. Handed over on 2026-11-18 from another session.
**Baseline**: `master` 3e35071 — the application code of v0.37.1 with pull requests #198 and #189.
Run as step 0.1; first run on `162fd42`, before the approval. The branch stands on `master`
`1fce915` since the first reviews, and on `c97c15e`, which adds records only, since just before
the push (section 7).
**Branch**: `fix/request-bodies-and-snapshot-lists` (local) → PR `fix: refuse unreadable or
oversized request bodies and snapshot lists with an entry missing`.
**Status**: written 2026-11-18 and approved by Joshua the same day, with Q1–Q4 answered A
("Approve, all A"). Built, verified and reviewed the same day, and the completion gate answered
the same day: B, continue ("B").

A small fix, so the workflow runs at minimal depth: the task is the requirement; no user stories,
application design, units, NFR or infrastructure stages; one unit, and this file is both its
functional note (sections 1–3) and its code-generation plan (sections 4–6), approved together.

## 1. What this change ships

1. **A body in a character set the API cannot read.** On every route that reads a JSON body, a
   request whose content type names a `charset` the API cannot read is refused with a 415 (Q1) and
   `The request body is in a character set that can't be read — send it as UTF-8.` Every body that
   is read today is read as before.
2. **Snapshot lists with an entry missing.** Three routes only the bot may use refuse a list with
   an entry that is missing altogether, with a 400 and a sentence, before anything is changed; and
   the bot's availability check without `userIds` gets the sentence that route already has.
3. **What a refusal quotes back, and the size of a body.** Five refusals that quote back a value
   that was sent quote at most 64 characters of it (Q2), and a request body of more than 1 MB from
   any caller but the bot is refused with a 413 (Q3).

The change is in the API. No migration, nothing new stored, no configuration. Contracts, the bot
and the web app are not changed: they show the API's sentence as they show its other refusals,
and what they send is read as before.

## 2. Rules

### 2.1 A body in a character set the API cannot read (as written here: Q1 answered A)

1. New: on a route that reads a JSON body, a request that has a body, a JSON content type
   (`application/json`, or a type ending in `+json`) and a `charset` the API cannot read is refused
   with a 415 and `The request body is in a character set that can't be read — send it as UTF-8.`,
   for the bot and for a web member alike, before the route runs.
2. Which character sets can be read is decided as the framework decides it when it reads a body:
   `utf-8` in any case, or no `charset` at all; otherwise the name is looked up among the encodings
   .NET has built in — UTF-8, UTF-16, UTF-32, US-ASCII and ISO-8859-1, under the names .NET knows
   them by — and a name that is not found there, or whose encoding .NET does not allow, cannot be
   read.
3. The check sits behind authentication and authorization: a request without credentials is
   answered 401 as before, and a web caller of a route only the bot may use 403 as before. It comes
   before the route's own checks.
4. As before: a body in a character set that can be read is read exactly as it was; a content type
   that is not JSON is answered 415 by the framework, without a sentence, as today; and a request
   without a body, or to a route that reads none, is answered as today whatever its content type
   names.

### 2.2 Snapshot lists with an entry missing (routes only the bot may use)

1. New: `PUT /channels/sync` with an entry of `channels` missing altogether is refused with a 400
   and `Channels can't have an entry missing.` No channel name is changed.
2. New: `PUT /guilds/{id}/roles/sync` with an entry of `roles` or of `members` missing altogether
   is refused with a 400 and `Roles and Members can't have an entry missing.` The server's role
   snapshot is left exactly as it was.
3. New: `PUT /guilds/presence/sync` with an entry of `guilds` missing altogether is refused with a
   400 and `Guilds can't have an entry missing.` No server is marked present or absent.
4. New: `POST /calendar/availability` without `userIds`, or with `userIds` set to null, is refused
   with a 400 and the sentence the route gives an empty list, `userIds must have between 1 and 50
   distinct entries.` — as `POST /calendar/free-slots` already answers.
5. As before: a list that is missing altogether keeps its own sentence; every complete list is read
   exactly as it was; and a web caller of these routes is answered 403.

### 2.3 What a refusal quotes back (as written here: Q2 answered A)

1. Five refusals quote back a value that was sent: an unknown time zone on `POST
   /tools/parse-datetime`, `PUT /guilds/{id}/settings` and `PUT /users/{id}/settings`; an unknown
   theme on `PUT /users/{id}/settings`; and an unknown action on `GET /guilds/{id}/actions`.
2. New: a value of more than 64 characters is quoted as its first 64, followed by `…`. Where the
   64th and 65th places hold the two halves of one character, the quote stops after the 63rd.
3. As before: a value of 64 characters or fewer is quoted whole, in the same sentence; every value
   that is accepted today is accepted as before.

### 2.4 The size of a request body (as written here: Q3 answered A)

1. New: on a route that reads a JSON body, a JSON body of more than 1 MB (1,048,576 bytes) from
   any caller but the bot is refused with a 413; a body that isn't JSON is answered as rule 4 of
   2.1 says, whatever its size. A request that states its length is refused before anything of it
   is read, with `The request body must be at most 1 MB.`; one that does not is read until what has
   been sent of it passes 1 MB, and then the server answers 413 itself, without a sentence. The
   server measures such a body as it is sent, the framing of its chunks included, so it is answered
   a few bytes before 1 MB of content *(amended after the approval: section 7)*.
2. As before: the bot's requests keep the server's own limit (30,000,000 bytes), since its
   snapshot of a large server's members can be bigger; a body of 1 MB or less is read as before;
   and a request to a route that reads no body is answered as today.

## 3. Questions

Q1 — The status of the refusal in 2.1.

- A) 415 Unsupported Media Type (2.1 as written). **(Recommended. It is the status HTTP has for a
  body in a form the server does not take, and the API already answers it, without a sentence,
  for a content type that is not JSON.)**
- B) 400 Bad Request, as the API's other refusals.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-18: "Approve, all A".

Q2 — What the five refusals of 2.3 quote back.

- A) At most 64 characters, then `…` (2.3 as written). **(Recommended. The sentence stays as it is
  for every value of up to 64 characters — more than the longest time zone, theme or action — and
  no new refusal is needed. 64 is the length of the column a time zone is stored in.)**
- B) A value of more than 64 characters is refused before it is looked up, with a sentence of its
  own (`The time zone must be at most 64 characters.`, and the same for the theme and the action);
  shorter values are quoted whole, as today.
- C) Nothing is quoted back: the five refusals say what is wrong without the value.
- D) Other (describe).

[Answer]: A — Joshua, 2026-11-18: "Approve, all A".

Q3 — A size limit of the API's own for a request body. Today there is only the server's, of
30,000,000 bytes.

- A) 1 MB for every caller but the bot (2.4 as written). **(Recommended. The largest body the web
  app sends is a few kilobytes — an event or a template with its description — so 1 MB leaves room
  to spare; the bot's snapshot of a large server's members can run to several megabytes, and the
  bot is the API's trusted caller.)**
- B) One limit for every caller, the bot included, high enough for the bot's largest snapshot:
  8 MB.
- C) No limit of the API's own: the server's stays.
- D) Other (describe).

[Answer]: A — Joshua, 2026-11-18: "Approve, all A".

Q4 — Order with other open work. Another open change adds one line at the same place in
`Program.cs`, right after `UseAuthorization()`, and both changes write the records.

- A) Build now, on `master`, independent of it; whichever merges second takes the other in.
  **(Recommended. The two changes share no other file; the overlap is two adjacent lines and the
  records, merged by hand as usual.)**
- B) Wait until it has merged, and build on it.
- C) Other (describe).

[Answer]: A — Joshua, 2026-11-18: "Approve, all A". The other change merged the same day, as pull
request #198, before anything was built here; this change takes it in (section 7).

## 4. What changes, file by file (with Q1–Q4 answered A)

| File | Change |
|---|---|
| `src/CalCrony.Api/Endpoints/RequestBodyChecks.cs` | New. `UseRequestBodyChecks()`: for a route that reads a JSON body, the size rule of 2.4 and the character-set rule of 2.1, each with its answer and its sentence. |
| `src/CalCrony.Api/Program.cs` | One line: `app.UseRequestBodyChecks()` after `app.UseAuthorization()`, right after the `app.UseStorableText()` that pull request #198 put there. |
| `src/CalCrony.Api/Endpoints/ChannelEndpoints.cs` | `SyncChannels`: rule 1 of 2.2, after the check for a missing list. |
| `src/CalCrony.Api/Endpoints/RoleSnapshotEndpoints.cs` | `SyncGuild`: rule 2 of 2.2, after the check for missing lists and before the transaction. |
| `src/CalCrony.Api/Endpoints/GuildPresenceEndpoints.cs` | `SyncPresence`: rule 3 of 2.2, after the check for a missing list. |
| `src/CalCrony.Api/Endpoints/CalendarEndpoints.cs` | `CheckAvailability`: rule 4 of 2.2, the way `GetFreeSlots` reads a missing list. |
| `src/CalCrony.Api/Endpoints/Validation.cs` | `Quotable(value)`: the part of a value a refusal quotes back (2.3). |
| `src/CalCrony.Api/Endpoints/EventEndpoints.cs`, `SettingsEndpoints.cs`, `ActionLogEndpoints.cs` | The five refusals of 2.3 quote `Quotable(value)`. |
| `tests/CalCrony.Api.Tests/RequestBodyApiTests.cs` | New: step 1.1. |
| `tests/CalCrony.Api.Tests/RequestBodyServerTests.cs` | New: step 1.2. |
| `tests/CalCrony.Api.Tests/SnapshotListApiTests.cs` | New: step 1.3. |
| `tests/CalCrony.Api.Tests/QuotedValueApiTests.cs` | New: step 1.4. |
| `aidlc-docs/aidlc-state.md`, `aidlc-docs/audit.md` | The records. |

Not changed: Contracts, the bot, the web app, the database, every route's shape. No documentation
describes these rules, so none is edited.

Order with other work: Q4. `master` is taken in before the push, and the two records files are
merged by hand, entries by timestamp.

## 5. Execution plan

A box is ticked in the turn its step completes.

**0. Baseline**
- [x] 0.1 On `master`, before the first change: `dotnet build CalCrony.slnx -c Release` and
      `dotnet test CalCrony.slnx -c Release` green; counts recorded. *(Run on 2026-11-18 on
      `162fd42` before approval, because it changes nothing: 0 errors and 22 warnings, the known
      baseline; 1,784 tests passed and none failed — 865 API, 418 bot, 153 web, 348 release
      notes — with 17 more of the release-notes project skipped by their own conditions on this
      machine.)*

**1. Tests first** — written and run against the unchanged code
- [x] 1.1 `RequestBodyApiTests`, as the bot and as a signed-in web member. 2.1: a body whose
      `charset` cannot be read is refused with a 415 and the exact sentence, for each such name,
      on a route that reads a JSON body, with `application/json` and with a type ending in
      `+json`. Pinned beside it: a body in each character set that can be read is read as before;
      a content type that is not JSON, a request without a body, a route that reads no body, a
      request without credentials (401) and a web caller of a route only the bot may use (403)
      are answered as before. 2.4: a web caller's body that states a length over 1 MB is refused
      with a 413 and the exact sentence, and nothing is changed; one of exactly 1 MB is read; the
      bot's of more than 1 MB is read; a route that reads no body is answered as before.
- [x] 1.2 `RequestBodyServerTests`, on the real server: a web caller's body that states no length
      and runs past 1 MB is answered 413, and one that stays under it is read; one that states a
      length over 1 MB is refused with the sentence; the bot's of more than 1 MB is read.
- [x] 1.3 `SnapshotListApiTests`. 2.2: each of the three lists with an entry missing — first, last
      or alone — is refused with a 400 and the exact sentence, and nothing is changed: the
      channel names, the server's role snapshot and every server's presence are as they were.
      An availability check without `userIds`, or with it set to null, gets the route's sentence.
      Pinned beside it: complete lists are read as before, a list that is missing altogether
      keeps its own sentence, and a web caller is answered 403.
- [x] 1.4 `QuotedValueApiTests`. 2.3, for each of the five refusals: a value of 64 characters is
      quoted whole; one of 65, and a much longer one, are quoted as their first 64 and `…`; a cut
      that would split a character stops after the 63rd. Pinned beside it: a known time zone,
      theme and action are accepted as before.
- [x] 1.5 The run on the unchanged code: every new refusal test fails, and every test that pins
      what is accepted today passes.

**2. The change**
- [x] 2.1 `RequestBodyChecks.cs` and `Program.cs`: sections 2.1 and 2.4.
- [x] 2.2 `ChannelEndpoints.SyncChannels`, `RoleSnapshotEndpoints.SyncGuild`,
      `GuildPresenceEndpoints.SyncPresence` and `CalendarEndpoints.CheckAvailability`: section 2.2.
- [x] 2.3 `Validation.Quotable` and the five refusals: section 2.3.

**3. Verification**
- [x] 3.1 `dotnet build CalCrony.slnx -c Release` with no new warning; `dotnet test CalCrony.slnx
      -c Release` green; counts recorded against 0.1.
- [x] 3.2 Each rule taken out in a scratch copy fails a test meant to pin it.
- [x] 3.3 The whole diff read against section 2, rule by rule.
- [x] 3.4 Two review agents read the local commits before anything is pushed, one for correctness
      and one for security and the public text. Their findings are answered, and section 7 says
      what changed.
- [x] 3.5 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

## 6. Delivery

Approving this plan covers the changes above and committing them locally. Everything that leaves
this machine waits for its own go-ahead.

- [ ] 6.1 *Go-ahead (the completion gate):* take `master` in if it has moved — the audit entries by
      timestamp, every section of the state file kept, the suite again and the reviewers' read of
      the result — then push the branch and open the pull request.
- [ ] 6.2 Checks green; a review if one starts. *Go-ahead:* squash merge. The test stack then takes
      `:main`.
- [ ] 6.3 *Go-ahead:* the release — the patch release that release-please opens — once it is
      settled with Joshua whether this change gets a line in the release summary
      (`docs/release-notes/<version>.md`).
- [ ] 6.4 *Go-ahead:* prod rollout, by the usual steps: backup, the pinned tag, app services only.
      No migration.

## 7. Notes from generation

- **Rebuilt on `master` `3e35071`.** `master` moved after the plan was written: pull requests #191
  and #194 (records), #198 — the other change that adds a line after `UseAuthorization()` — #193
  (a code of conduct) and #195 (records), and then #189 (a limit on how far ahead a date can be
  set). Nothing had been pushed, so the plan's one commit was put back on top of `3e35071` after
  the approval, its two audit entries in their places by timestamp and its state section after
  `master`'s last. The baseline line names `3e35071`, and section 4's row for `Program.cs` names the
  line #198 put there; nothing else in the plan changed then.
- **Baseline (0.1).** On `162fd42`, before the approval: 1,784 tests passed (865 API). On
  `3e35071`, 2026-11-18 from 19:23Z to 19:29Z: 0 errors and 22 warnings; 2,102 tests passed and
  none failed — 1,182 API, 418 bot, 153 web, 349 release notes — with 17 release-notes tests
  skipped by their own conditions on this machine.
- **Tests first (1.1–1.5).** 56 new test cases: 15 in `RequestBodyApiTests`, 4 in
  `RequestBodyServerTests`, 17 in `SnapshotListApiTests` and 20 in `QuotedValueApiTests`. Run
  against the unchanged code (`3e35071`) on 2026-11-18 at 19:36Z: all 32 refusal tests failed,
  and all 24 tests that pin what is accepted today passed. One pin was changed after that run, as
  the amendment below says; the run was repeated with it, on the API's source as on `3e35071`
  under the tests as committed, and the same 32 tests failed and the same 24 passed.
- **The change (2.1–2.3).** As section 4 says. `UseRequestBodyChecks` sits right after the
  `UseStorableText` that #198 put after `UseAuthorization()`, so the check of the path and the
  query string keeps answering first. Which routes read a JSON body is read from the route itself
  — such a route declares that it accepts `application/json` — so a route added later is covered
  as it is. The size rule is for a body the route would read as JSON: one without a content type,
  or with one that isn't JSON, is never read, and the framework answers it 415 whatever its size,
  as before.
- **Amended after the approval: how the server measures a body that does not state its length.**
  Rule 1 of 2.4 said that such a body is read up to 1 MB. The server measures it as it is sent,
  the framing of its chunks included — 14 bytes for a body of about 1 MB sent in one chunk — so it
  is answered 413 a few bytes before 1 MB of content: in one chunk, a body of 1,048,562 bytes is
  read and one of 1,048,563 is not. A body that states its length is measured exactly: one of
  1,048,576 bytes is read, and one of 1,048,577 is refused with the sentence. Rule 1 of 2.4 now
  says so, and the test of a body read without a stated length sends 1,000,000 bytes. Put to
  Joshua at the completion gate.
- **Verification (3.1–3.3).** On `cc40686`, 2026-11-18 from 19:44Z to 19:48Z: `dotnet build
  CalCrony.slnx -c Release --no-incremental` with 0 errors and the baseline's 22 warnings, the same
  ones; `dotnet test CalCrony.slnx -c Release` with 2,158 tests passed and none failed — 1,238 API
  (`master`'s 1,182 and the 56 added), 418 bot, 153 web, 349 release notes — and the same 17
  skipped. Twenty-three changes that each take one rule out, made one at a time in a scratch copy
  of `cc40686` from 19:48Z to 19:57Z, each failed tests written for it: the character-set rule
  taken out, made to refuse every named character set, and left without its check for a body or
  for a route that reads JSON; the size rule without its check of a stated length, with the bot not
  exempt, one byte short, and without the server's limit lowered; the check placed before
  authentication; each list's check taken out, and the server list's missing entries skipped
  instead of refused; and the quote left uncut, cut through a character, cut one short, and each of
  the five refusals quoting the whole value. The whole diff was read against section 2, rule by
  rule.
- **Review (3.4), first reads.** Two agents read the local commits up to `cc40686`, each in a
  checkout of its own: one for correctness, one for security and the public text. Both built it
  and ran the new tests, and sent requests to the server itself; the check of the character set was
  compared with the framework's own reading over 74 forms of the header, and they gave the same
  answer for each. Answered:
  - A body that states a length of more than 1 MB is refused with the 413 only when the route
    would read it as JSON; one without a content type, or with one that isn't JSON, is answered 415
    by the framework whatever its size, as before. Rule 1 of 2.4 now says "a JSON body", and a test
    pins the rest; taking the condition out fails that test.
  - The check takes any failure of the lookup of a character set to mean one it can't read, as the
    framework does.
  - The summary of `RequestBodyChecks` and its comment on a body that does not state its length
    now say what the server does.
  - Wording in the records, in the commits as they stand: rule 3 of 2.1, which is one clause
    shorter than when it was approved, and the first audit entry of this change, by a clause.
  - Left as they are: the 415's sentence is the same for every character set that can't be read,
    however the header writes it; after the 413 for a stated length the server reads and discards
    the rest of the body, as after any answer given before a body is read, which is what lets a
    caller that sends its whole body before reading see the sentence; the test of a body that does
    not state its length waits up to 30 seconds for its answer; and a roles sync with an entry
    missing, for a server the bot has left, gets this 400 where it got a 409, as one with a list
    missing altogether already does.
- **Rebuilt on `master` `1fce915`.** By then `master` had moved to `1fce915`: pull requests #202
  (records), #199 (date text) and #196 (the public calendar), none of which touches this change's
  code. Nothing had been pushed, so the three commits were rebuilt there: the plan's with the two
  wording changes above, the code's with the review's answers, and the records in their places by
  timestamp.
- **Review (3.4), second reads.** Both agents read the rebuilt commits up to `06b565d`. The
  security read confirmed every answer, found the public text within the rules, and found that the
  condition on a JSON body opens nothing: the route reads exactly the bodies the condition names.
  The correctness read confirmed the answers but one: a body that states no length and isn't JSON
  still had the server's limit lowered, so a web caller's such body of more than 1 MB could have
  its connection closed before the framework's 415 reached it, where today it gets the 415. The
  whole size rule now applies only to a body the route would read as JSON, and a test on the server
  itself pins the rest; with the rule as it stood, that test fails. Recorded as left as they are:
  the raw test ends with an error rather than a failed assertion if its connection is reset, which
  fails it just the same; and the rows of the test of the role and member lists name the list they
  leave an entry out of.
- **Verification on `1fce915`.** On the tree of `81d3910`, the fix commit as amended, 2026-11-18
  from 20:27Z to 20:29Z, before the commit's message was reworded: `dotnet build CalCrony.slnx -c
  Release --no-incremental` with 0 errors and the
  baseline's 22 warnings, the same ones, and `dotnet test CalCrony.slnx -c Release` with 2,487
  tests passed and none failed — 1,567 API, 418 bot, 153 web, 349 release notes: `master`'s own
  2,429 and the 58 added, the 56 written first and the 2 the reviews asked for — with the same 17
  skipped. The 58 run on the API's source as on `1fce915`: the same 32 refusal tests failed, and
  the 26 that pin what is accepted today passed.
- **Review (3.4), final reads.** Both agents read the amended fix commit and the records as they
  stood then, and confirmed them; nothing is open. On the security read's suggestion the fix
  commit's message now says "a JSON body of more than 1 MB", as rule 1 of 2.4 does; its tree is the
  one both agents read and the suite ran on. Before the push both agents read the reworded commit
  and the records as they stand, and confirmed them.
- **`master` taken in before the push.** After the completion gate `master` moved to `c97c15e`
  (pull request #201, records only). Nothing had been pushed, so the three commits were put back on
  top of it, the audit entries in their places by timestamp and the state section after
  `master`'s last. Nothing outside `aidlc-docs` differs from the tree the suite ran on, so its
  result stands; both agents read the result before the push.
