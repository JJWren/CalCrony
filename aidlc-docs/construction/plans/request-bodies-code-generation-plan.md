# Request bodies the API cannot read, snapshot lists with an entry missing, and what a refusal quotes back — functional note and code-generation plan

**Task**: three refusals in the API. A JSON request body in a character set the API cannot read is
refused with a sentence; a snapshot list from the bot with an entry missing altogether is refused
with a sentence; and what a refusal quotes back of a request is kept short, with a size limit of
the API's own for a request body. Handed over on 2026-11-18 from another session.
**Baseline**: `master` 162fd42 — the application code of v0.37.1. Run as step 0.1.
**Branch**: `fix/request-bodies-and-snapshot-lists` (local) → PR `fix: refuse unreadable or
oversized request bodies and snapshot lists with an entry missing`.
**Status**: written 2026-11-18; waiting for Joshua's approval and his answers to Q1–Q4
(section 3).

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

1. New: on a route that reads a JSON body, a body of more than 1 MB (1,048,576 bytes) from any
   caller but the bot is refused with a 413. A request that states its length is refused before
   anything of it is read, with `The request body must be at most 1 MB.`; one that does not is read
   up to 1 MB, and then the server answers 413 itself, without a sentence.
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

[Answer]:

Q2 — What the five refusals of 2.3 quote back.

- A) At most 64 characters, then `…` (2.3 as written). **(Recommended. The sentence stays as it is
  for every value of up to 64 characters — more than the longest time zone, theme or action — and
  no new refusal is needed. 64 is the length of the column a time zone is stored in.)**
- B) A value of more than 64 characters is refused before it is looked up, with a sentence of its
  own (`The time zone must be at most 64 characters.`, and the same for the theme and the action);
  shorter values are quoted whole, as today.
- C) Nothing is quoted back: the five refusals say what is wrong without the value.
- D) Other (describe).

[Answer]:

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

[Answer]:

Q4 — Order with other open work. Another open change adds one line at the same place in
`Program.cs`, right after `UseAuthorization()`, and both changes write the records.

- A) Build now, on `master`, independent of it; whichever merges second takes the other in.
  **(Recommended. The two changes share no other file; the overlap is two adjacent lines and the
  records, merged by hand as usual.)**
- B) Wait until it has merged, and build on it.
- C) Other (describe).

[Answer]:

## 4. What changes, file by file (with Q1–Q4 answered A)

| File | Change |
|---|---|
| `src/CalCrony.Api/Endpoints/RequestBodyChecks.cs` | New. `UseRequestBodyChecks()`: for a route that reads a JSON body, the size rule of 2.4 and the character-set rule of 2.1, each with its answer and its sentence. |
| `src/CalCrony.Api/Program.cs` | One line: `app.UseRequestBodyChecks()` right after `app.UseAuthorization()`. |
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
- [ ] 1.1 `RequestBodyApiTests`, as the bot and as a signed-in web member. 2.1: a body whose
      `charset` cannot be read is refused with a 415 and the exact sentence, for each such name,
      on a route that reads a JSON body, with `application/json` and with a type ending in
      `+json`. Pinned beside it: a body in each character set that can be read is read as before;
      a content type that is not JSON, a request without a body, a route that reads no body, a
      request without credentials (401) and a web caller of a route only the bot may use (403)
      are answered as before. 2.4: a web caller's body that states a length over 1 MB is refused
      with a 413 and the exact sentence, and nothing is changed; one of exactly 1 MB is read; the
      bot's of more than 1 MB is read; a route that reads no body is answered as before.
- [ ] 1.2 `RequestBodyServerTests`, on the real server: a web caller's body that states no length
      and runs past 1 MB is answered 413; the bot's is read.
- [ ] 1.3 `SnapshotListApiTests`. 2.2: each of the three lists with an entry missing — first, last
      or alone — is refused with a 400 and the exact sentence, and nothing is changed: the
      channel names, the server's role snapshot and every server's presence are as they were.
      An availability check without `userIds`, or with it set to null, gets the route's sentence.
      Pinned beside it: complete lists are read as before, a list that is missing altogether
      keeps its own sentence, and a web caller is answered 403.
- [ ] 1.4 `QuotedValueApiTests`. 2.3, for each of the five refusals: a value of 64 characters is
      quoted whole; one of 65, and a much longer one, are quoted as their first 64 and `…`; a cut
      that would split a character stops after the 63rd. Pinned beside it: a known time zone,
      theme and action are accepted as before.
- [ ] 1.5 The run on the unchanged code: every new refusal test fails, and every test that pins
      what is accepted today passes.

**2. The change**
- [ ] 2.1 `RequestBodyChecks.cs` and `Program.cs`: sections 2.1 and 2.4.
- [ ] 2.2 `ChannelEndpoints.SyncChannels`, `RoleSnapshotEndpoints.SyncGuild`,
      `GuildPresenceEndpoints.SyncPresence` and `CalendarEndpoints.CheckAvailability`: section 2.2.
- [ ] 2.3 `Validation.Quotable` and the five refusals: section 2.3.

**3. Verification**
- [ ] 3.1 `dotnet build CalCrony.slnx -c Release` with no new warning; `dotnet test CalCrony.slnx
      -c Release` green; counts recorded against 0.1.
- [ ] 3.2 Each rule taken out in a scratch copy fails a test meant to pin it.
- [ ] 3.3 The whole diff read against section 2, rule by rule.
- [ ] 3.4 Two review agents read the local commits before anything is pushed, one for correctness
      and one for security and the public text. Their findings are answered, and section 7 says
      what changed.
- [ ] 3.5 `aidlc-state.md` and `audit.md` brought up to date; this plan's boxes ticked.

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

(Filled in as the work is done.)
