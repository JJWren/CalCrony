# Release summary — a hand-written summary and how-to with every release: code generation plan

**Request** (Joshua, 2026-11-13, right after v0.37.0 went out with a link-only announcement): "In
future updates, for the releases, I would like to provide a small bulleted summary of the major
changes (or minor if important or the only things) with the release notes so that they appear in
places like Discord when the update is pushed out. If possible, I would also like the notes to
include a small how summary for new or changed features." Handed to this session by the session that
released 0.37.0, with the instruction to compare the options, recommend one and ask before building.
**Depth**: minimal — one unit; the requirements, the design questions and the plan are this one
document, as on the docs readability pass. User stories, application design, units, NFR and
infrastructure stages are skipped: the change is release tooling, one stakeholder answers the
questions directly, and the one thing members will see — the announcement — is shown below as it
would read. **Baseline**: `master` f904e33 (v0.37.0). **Branch**: `ci/release-notes-summary` → PR
`ci: carry a hand-written summary into the release notes and the Discord announcement` (`ci:` cuts no
release; the change takes effect at the next one).
**Status**: approved by Joshua on 2026-11-14 (UTC), as written ("Approve as recommended"): Q1–Q3
answered A, A, A, and D1–D7 stand.

## 1. What exists today (verified 2026-11-13)

- **The workflow** (`.github/workflows/release-please.yml`): `release-please` runs
  `googleapis/release-please-action@v4` with `release-type: simple` and no config or manifest file;
  `publish-images` tests the tag and pushes the three images; `announce-release` (no checkout,
  `permissions: {}`) posts `{content, allowed_mentions: {roles: [<Release Ping>]}}` to the
  `DISCORD_RELEASE_WEBHOOK` secret, where `content` is one line: the role mention, "**CalCrony
  \<tag\>** just shipped — release notes: \<link\>". It skips when the secret is unset.
- **Release 0.37.0**: the notes are the two changelog lines release-please wrote from the PR titles,
  and the announcement was that one line. Its run (43515848010): `release-please`
  22:02:03Z–22:02:21Z, with the release published at 22:02:12Z; `publish-images`
  22:02:23Z–22:05:38Z; `announce-release` 22:05:40Z–22:05:43Z. So the announcement goes out about
  three and a half minutes after the release exists.
- **The release PR** (#175 for 0.37.0): written by release-please, branch
  `release-please--branches--master`, one file (`CHANGELOG.md`). Its body is the notes between
  release-please's header and footer.
- **Where the process is described**: `CONTRIBUTING.md` ("Versioning & releases", four steps), one
  sentence in the README pointing there, the PR template's checklist. None mentions a summary.
- **Checks**: `build-test` (`dotnet test CalCrony.slnx`) runs on every PR and is required;
  `publish-images` runs the same suite on the tag. The release workflow itself runs only on a push to
  `master` or by hand (`workflow_dispatch`), so a PR's checks never execute it.
- **A trap in `.gitignore`**: line 30, `[Rr]eleases/`, is a build-output pattern that would silently
  ignore a folder named `releases`. The folder here is `docs/release-notes/`.

## 2. What release-please can and cannot do (its config schema and source, read 2026-11-13)

- It has no setting that adds prose to the notes. The notes are built from commit titles;
  `pull-request-header` and `pull-request-footer` are fixed text for the release PR, not per release.
- It builds the GitHub release from the **body of the merged release PR**, and that body has to begin
  with the version heading: when it does not, release-please logs "Failed to find version in release
  notes" and creates the release with **empty** notes (`src/strategies/base.ts`,
  `src/util/pull-request-body.ts`).
- It rewrites the release PR whenever the body differs from what it would write
  (`src/manifest.ts`), so text added by hand, or by a job, is lost on the next push to `master`, and
  would make it rebuild the PR on every push.
- The action hands on `body` — the notes it wrote — and `version`, `tag_name`, `html_url`.

So the summary cannot ride in the release PR's body without putting the release notes themselves at
risk. This plan leaves release-please exactly as it is and adds the summary after it has done its
work.

## 3. Design questions

Answer with the letter; a recommendation is marked.

**Q1 — Where does the summary live, and who writes it?**

- A) One file per release in the repository, named for the version: `docs/release-notes/0.38.0.md`.
  Whoever opens the PR for a change members will notice writes or extends the file in that same PR,
  next to the README and Docs page edits; anything missed goes in through a small `docs:` PR before
  the release PR is merged. The release reads the file named for its own version; no file, no
  summary. **(Recommended — the text is reviewed and tested like any other change, there is nothing
  to clear afterwards, and the folder becomes a readable history of what each release was about. Its
  one weak spot: the file name must match the version release-please picks. The open release PR's
  title shows it, and Q3 A names any file left behind when a later `feat:` moves the number.)**
- B) One rolling file, `docs/release-notes/next.md`, used when it has changed since the last
  release. No version to guess. But the workflow cannot clear it (`master` takes no direct pushes),
  so last release's text stays in the file until someone replaces it: adding to it announces old
  news again, and any later edit, a typo fix included, makes old text count as new.
- C) A marked comment on the release PR, written just before merging. No file, no version to guess,
  and comments survive release-please's rewrites. But the text is not in git, gets no review and no
  check before the release is already running, cannot be written with the feature, and — the
  repository being public — the workflow would have to trust comments by their author.
- D) A "Release notes" section in every PR description, gathered by the workflow at release time.
  Written with the change, nothing to clear. But nobody looks over the release as a whole (which
  changes are the major ones?), PR descriptions are free text that the workflow would have to parse,
  and a dry run depends on what GitHub holds that day.
- E) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-14.

**Q2 — How does the summary show in Discord?**

- A) As text in the announcement itself, under today's line. **(Recommended — it reads like any
  announcement and the first line stays exactly what it is. Discord allows 2,000 characters for the
  message, so a summary is held to 1,800; the 0.37.0 sample in section 5 is 706.)**
- B) As an embed, a boxed card under today's line. Room for 4,096 characters, but it looks like a
  link preview rather than something a person wrote.
- C) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-14.

**Q3 — Should the open release PR show what will be announced?**

- A) Yes. On every push to `master` that does not cut a release, a job posts one comment on the open
  release PR and keeps it up to date: the announcement as it would go out; or that no summary has
  been written for that version yet, and how to add one; and any summary file left over for a
  version that was never released. It blocks nothing. **(Recommended — the summary only helps if it
  gets written, and this puts the reminder where the decision to release is made.)**
- B) No. The convention in `CONTRIBUTING.md` and a preview command to run locally are enough.
- C) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-14.

## 4. Decisions this plan takes (accepted unless Joshua says otherwise)

| # | Decision | Why |
|---|---|---|
| D1 | A summary opens with `### What's new` and a few bullets; `### How to use it` may follow, with short steps per new or changed feature. Plain words, for server members. | The request, in a form a check can hold to. A fix-only release is one bullet and no how-to. |
| D2 | The file is posted as written, in both places. A check holds it to what Discord and GitHub render the same way: those two headings and no others; no tables, images or HTML; no mention outside code (`@name` would notify a GitHub user of that name; a command example such as `who:@GM` goes in backticks); at most 1,800 characters. | What is written is what members see, and nothing is converted on the way. |
| D3 | On GitHub the summary goes on top, then a rule, then release-please's notes untouched. A job edits the release seconds after release-please creates it. | Section 2: the release PR's body is not a safe place. One consequence: an e-mail GitHub sends to people watching releases may go out with the notes as first created. |
| D4 | A new job, `release-summary`, is the only one that reads the repository for this. It hands the text to `announce-release` as a job output. `announce-release` keeps `permissions: {}`, the role mention and `allowed_mentions` as they are, and still builds the message itself. | The job that holds the webhook gets a piece of text and nothing else. |
| D5 | Nothing about a summary can hold a release back. No file: the notes and the announcement are exactly as today. The summary job failing: the same. A file over the limit that reached `master` anyway: cut at a line end for Discord, with a pointer to the notes. | "A release with no summary written must still go out and announce as it does today." |
| D6 | The tool is C#, `tools/ReleaseNotes`, with its tests in `tests/CalCrony.ReleaseNotes.Tests`, both in `CalCrony.slnx`. | The required `build-test` check then runs the tests with no change to CI, and a summary that would not fit or render fails the PR that adds it, not the release. |
| D7 | The first file is `docs/release-notes/0.37.0.md`, the sample from the hand-over checked against the commands. It is the history's first entry and the tests' sample. | It changes nothing published: 0.37.0 is out, and the workflow reads a file only for the version being released. Adding it to the published 0.37.0 notes is a separate decision (section 9). |

## 5. The summary for 0.37.0, and what it would have produced

The file (706 characters):

```markdown
### What's new

- **Free slots.** `/availability slots` lists the times a member, or everyone in a role, is free over the next 1 to 14 days.
- **Available hours.** Each member can save the hours they are usually free with `/settings available-hours`, and free slots stay inside them.

### How to use it

1. **GM:** link your calendar with `/calendar connect` and set your zone with `/settings timezone`.
2. **GM:** save your hours, for example `/settings available-hours set days:weekdays from:6pm to:11pm`.
3. **Players:** run `/availability slots who:@GM` to see when the GM is free.
4. Add `days:14`, `when:saturday` or `duration:180` to look further ahead, start on another day or ask for longer slots.
```

The Discord message: today's line, a blank line, then the file (838 of 2,000 characters). The role
mention and `allowed_mentions` are unchanged.

```text
<@&1543943300132769802> **CalCrony v0.37.0** just shipped — release notes: https://github.com/JJWren/CalCrony/releases/tag/v0.37.0

### What's new

- **Free slots.** `/availability slots` lists the times a member, or everyone in a role, …
(the rest of the file)
```

The GitHub release notes: the file, a rule, then what release-please wrote.

```text
### What's new
(the file)

---

## [0.37.0](https://github.com/JJWren/CalCrony/compare/v0.36.4...v0.37.0) (2026-11-13)

### Features
(release-please's two lines, unchanged)
```

The tests keep both texts as files and compare them character for character (step 2.2).

## 6. What gets built

The jobs of `.github/workflows/release-please.yml` afterwards:

| Job | Runs | Permissions | What it does |
|---|---|---|---|
| `release-please` | every push to `master` | as today | Unchanged. |
| `publish-images` | when a release was created | as today | Unchanged. |
| `release-summary` (new) | when a release was created, beside `publish-images` | `contents: write` | Checks out the tag and looks for `docs/release-notes/<version>.md`. If it is there: puts it on top of the release notes, and hands its text on. |
| `announce-release` | after `publish-images` succeeded, whether or not `release-summary` did | none, and no checkout, as today | Today's step with one more input, the summary text: added under today's line when there is one and the whole message fits in 2,000 characters. |
| `release-summary-preview` (new; Q3 A) | on a push that created no release | `contents: read`, `pull-requests: write` | Finds the open release PR and posts or updates its one comment. |

The tool, `tools/ReleaseNotes` (no package references):

- `check` — every file in `docs/release-notes/` against the rules of D2 (the tests run the same
  rules, so a PR fails on a bad file).
- `render` — release time: reads the version's file and the notes release-please wrote; writes the
  new release notes and the text for Discord; says whether there was a summary.
- `preview` — prints the Discord message and the release notes for a version without sending
  anything; one form of it writes the release PR's comment.

## 7. Execution plan

**0. Baseline**
- [x] 0.1 A Release build and the full test run on the branch before any change; counts and warnings noted. (2026-11-14: 0 errors and 22 warnings; 1,292 tests green — 721 API, 418 bot, 153 web.)

**1. The tool — `tools/ReleaseNotes/`**
- [x] 1.1 The project (`net10.0`, nullable, no packages) and its entry in `CalCrony.slnx` under `/tools/`.
- [x] 1.2 Reading a summary: file name to version (`<major>.<minor>.<patch>.md`; `README.md` is the folder's only other file), a byte-order mark and Windows line ends removed, surrounding blank lines trimmed.
- [x] 1.3 The rules of D2, each with a message that says what to change.
- [x] 1.4 The texts: the release notes (summary, rule, notes; unchanged when the notes already begin with the summary, so a re-run adds nothing twice); the Discord text (the file as written; over 1,800 characters cut at a line end, with a pointer); the whole announcement as `announce-release` builds it, for the preview and the tests.
- [x] 1.5 The commands `check`, `render` (writes the notes file and the job outputs) and `preview` (with the release PR's comment as one form: version from the PR title, last release from `CHANGELOG.md`, leftover files named).

**2. Tests — `tests/CalCrony.ReleaseNotes.Tests/`**
- [x] 2.1 The project and its entry in `CalCrony.slnx` under `/tests/`.
- [x] 2.2 The 0.37.0 sample: the release notes and the Discord message compared character for character with files kept beside the tests, using release-please's real notes for 0.37.0.
- [x] 2.3 Each rule: a file that passes and one that fails for that reason alone; the limit at 1,800 and at 1,801.
- [x] 2.4 No file: no summary, notes unchanged. The cut for an over-long file. A second `render` over notes that already carry the summary. The job outputs as the runner reads them.
- [x] 2.5 The preview comment: summary found, none written, a leftover file; the version read from a release PR title.
- [x] 2.6 The repository's own files: every file in `docs/release-notes/` passes the rules.
- [x] 2.7 The workflow file: `announce-release` still has `permissions: {}`, the same role id and `allowed_mentions`, and today's first line. Where bash, jq and curl exist (the CI runner), the step's own script is run against a listener on localhost: with no summary the body received is today's, byte for byte; with the sample it is the expected message; with no webhook set it posts nothing.
- [x] 2.8 Each rule and each guard taken out in turn, to see the test meant to pin it fail. (86 changes on a copy, section 10.)

**3. The workflow — `.github/workflows/release-please.yml`**
- [x] 3.1 `release-summary`: checkout of the tag, the SDK, `render`, then `gh release edit` when there was a summary.
- [x] 3.2 `announce-release`: waits for `release-summary` as well, runs when `publish-images` succeeded whatever became of `release-summary`, and adds the summary under today's line. The mention, `allowed_mentions` and `permissions: {}` unchanged.
- [x] 3.3 `release-summary-preview` (Q3 A): the open release PR found by its branch; one comment, found again by a marker and updated in place.
- [x] 3.4 The file parsed locally as YAML, and each job's `needs`, `if` and outputs read against the others.

**4. Documents**
- [x] 4.1 `docs/release-notes/README.md` (what the folder is, the template, the rules in short) and `docs/release-notes/0.37.0.md`.
- [x] 4.2 `CONTRIBUTING.md`: a "Release summary" part under "Versioning & releases" — who writes it and when, the file name, the shape, the rules, the preview command, what happens without one — and the release flow's steps brought up to date.
- [x] 4.3 The PR template's checklist gains a line; the README's sentence on releases says where the summary comes from.

**5. Verification**
- [x] 5.1 A Release build with no errors and no new warnings; the full suite green.
- [x] 5.2 `preview 0.37.0` run; the exact Discord message and release notes kept as the evidence for the pull request.
- [x] 5.3 Two review agents, one on security and one on correctness and quality, read the local commits before anything is pushed, as on the last three changes; findings answered. (Three rounds and a confirmation round: sections 11 to 14.)

**6. Bookkeeping and delivery — each outward step on Joshua's own go-ahead**
- [x] 6.1 `audit.md` entries throughout; `aidlc-state.md` carries a section for this work. (Up to the completion gate; the entries go on from there.)
- [x] 6.2 `master` merged into the branch (it will have gained the 0.37.0 records by then), `audit.md` kept in the order of its timestamps; then the push and the pull request. (2026-11-16, on Joshua's "B) Continue": merge `55062f6`, pull request #179. Section 15.)
- [x] 6.3 The workflow run once by hand from the branch (the `workflow_dispatch` trigger it already has), so that GitHub parses the changed file and release-please runs and finds nothing to do. (As built after review: the preview job runs from `master` only, so on a run from the branch it is skipped.) Checks green; Copilot's review if it starts, the two agents if it does not. (2026-11-16: run 43890372277 succeeded and released nothing; `build-test` and `pr-title` green on #179, all 351 new tests passing on the runner with none skipped; Copilot's review did not start. Section 15.)
- [ ] 6.4 The squash merge through the REST call. It cuts no release.

What cannot be run before a real release: the two jobs that only exist when one is created. They are
covered by the tool's tests and by the announce step run for real in CI (2.7), and they fail safe
(D5). The first real use is the next release.

## 8. Risks

| Risk | Answer |
|---|---|
| A mistake in the workflow file stops every job, release-please included. | 3.4 and 6.3: the file is parsed locally and then by GitHub itself, from the branch, before the merge. |
| The summary's file name does not match the version released. | The announcement is as today, nothing breaks. Q3 A shows it on the release PR beforehand. |
| A summary pings someone. | `allowed_mentions` names the one role and is unchanged, so nothing else in the text can ping in Discord. On GitHub, where `@name` notifies, the check refuses a mention outside code in the forms it knows, and a summary it refuses is not published; and before a summary is published GitHub itself is asked whether it reads a mention in it (section 12). |
| The summary job breaks on a release day. | D5: `announce-release` does not depend on its success. |
| Text from an untrusted source reaches the announcement. | The text comes only from a file at the release tag, which got there through a reviewed PR — the same trust as the workflow file. Nothing is read from PR titles, comments or forks. |

## 9. Not in this change

- Adding the 0.37.0 summary to the published v0.37.0 release notes, or posting it to Discord after
  the fact. Both are public edits and Joshua's to decide once this is in.
- Any change to how release-please versions, writes `CHANGELOG.md` or opens its PR.
- A summary as a condition for releasing. It stays optional by design.

## 10. As first built (2026-11-14, commit `9ae3677`): what differed from the plan as approved, and what was run

This section is the state the two reviewers first read. Sections 11 and 12 record what their two
reviews changed; where a later section differs from this one, the later one is how it is now.

**Differences.**

- **`comment` is a command of its own** (1.5 and section 6 had it as a form of `preview`). The
  release PR's comment starts from a PR title and writes a file; the preview starts from a version
  and prints for a person.
- **D2's rules, made exact.** The first line is `### What's new`; at least one bullet stands under
  it; `### How to use it` may follow once, and there is no other heading; no table, no image, and
  no line of dashes — a rule on GitHub and plain dashes in Discord, which D2 did not list; no `<`
  outside backticks other than a link written `<https://…>`, which is how "no HTML" is checked and
  also refuses Discord's own tags, which GitHub would show raw; no `@` followed by a name outside
  backticks; a code block that is opened is closed; no control character other than a tab, and no
  bidirectional control — characters nobody can see in review, also not in D2; at most 1,800
  characters, counted in UTF-16 units, which can only overstate what Discord counts.
- **Discord gets the summary only when the release notes got it** (D4 and D5 did not say). The
  summary job's output is empty unless the step that edits the release succeeded, so the two places
  never disagree.
- **The two new jobs keep no token in their checkout** (`persist-credentials: false`), and in
  `release-summary` only the two steps that call GitHub are given one.
- **The preview job reads the release PR's title**, to learn the version. Section 8 said nothing
  is read from PR titles; that stays true of the announcement, which the title never reaches. The
  job takes release-please's own PR only: its branch in this repository, opened by the Actions bot.
- **The folder's README says `0.37.0.md` was written after that release went out.**

**What was run, on this machine.**

- Release build of the whole solution, from scratch: no errors and the baseline's 22 warnings,
  none of them in the two new projects. Full suite: the 1,292 tests there before, green (721 API,
  418 bot, 153 web), and 207 new ones — 199 green and 8 skipped.
- **The 8 skipped tests are the ones that run the announce step's own script** against a stand-in
  webhook on localhost. They need bash, jq and curl; this machine has no jq, so here they are
  skipped, and CI — where a missing tool fails them rather than skips them — is their first real
  run. They were rehearsed here in a Linux container with a stand-in for jq that knows only the
  step's two programs: all eight passed, which checks the test harness and the shell around jq
  (bash with the runner's flags, curl, the request the listener receives) and says nothing about
  jq itself.
- **86 changes that each take one rule or guard out, or bend it**, on a copy: 23 in the rules, 10
  in the texts, 9 in the announcement, 3 in the outputs, 6 in the preview, 4 in versions, 3 in
  reading a summary, 2 in the folder, 6 in the commands, 2 in the title and changelog readers, 17
  in the workflow file and 1 in the sample summary. The first pass ran 84 of them and 81 failed a
  test. One of the three that did not was a pattern that could not match, which showed that nine
  escapes in four source files had been written as the invisible characters themselves; they are
  escapes again, and no file in the change holds a control or bidirectional character. The other two were
  real gaps — a line starting `#word` is text, not a heading; a code span ends only at a run of
  backticks as long as the one that opened it — and each now has a test. Second pass, all 86: every
  one fails at least one test.
- The workflow file parses as YAML, and each job's `needs`, `if`, permissions and outputs were read
  against the others. The preview job's two lookups were run read-only against the repository as it
  is: the pull-request lookup finds release-please's merged PR #175 and prints nothing when no
  release PR is open; the comment lookup finds no preview comment, as there is none.
- `preview --version 0.37.0` with the real notes: the Discord message is 838 of 2,000 characters
  and both texts equal the files kept beside the tests.

**Not run, and why.** The release workflow itself: it runs only on a push to `master` or by hand.
Step 6.3 has GitHub parse the file and run the no-release path from the branch after the push; the
two jobs of the release path first run at the next release, and fail safe (D5).

## 11. Review round 1 (2026-11-14)

Two agents read commit `9ae3677`, each in a copy of its own: one on security and privacy, one on
correctness and quality. Neither found anything critical or major. Security: one medium, five low,
four notes. Quality: five minor and a group of nits. Three statements of this plan did not hold when
they were checked — D5's "nothing about a summary can hold a release back", section 8's "the check
refuses mentions" and section 10's "only the two steps that call GitHub are given a token" — and
each is made true below.

**What they found, and what was done.**

| # | Finding | Answer |
|---|---|---|
| S1, medium | `publish-images` runs the whole suite on the tag, and the suite now checks every summary file. A file that breaks a rule and reached `master` — two pull requests that each fit and together do not, or a merge past a red check — would have stopped the images and the announcement, on a tag that cannot change. | The image job leaves the release-notes tests out with a test filter. The exact command was run here: it exits 0 and runs all 1,292 tests of the three product projects. This is the one change to `publish-images`, which section 6 called unchanged. |
| S2, low | The tool was built and run in a step that held the job's token, and it followed a symbolic link, so a link committed as a summary could have put what it pointed at into the public notes. | No step that runs the tool has a token, in either job. A link named for a version is not a summary: the release reads nothing through it, and `check` reports it. |
| S3, low; Q5, minor | Ways to get text past the check that GitHub then reads as a mention, a heading or a table: a line that starts with three backticks and goes on; a fence with a language inside a block; a backtick after a backslash; a backtick inside a link in angle brackets; HTML entities; code that runs over a line end; an underline of one or two characters; a table without a leading pipe; a heading inside a bullet; several invisible characters. One false alarm: an `@` in a link's address. And at release time nothing was refused: a summary with a mention the check had caught still went out. | The rules were rewritten to read every line by itself, the way GitHub reads a line (below). At release time a summary the check refuses for anything but its length is left out. The comment on the release PR shows such a file as code, so that the comment itself notifies nobody. |
| S4, low | A failed summary job left a red run after the ping had gone out, and "Re-run failed jobs" would have pinged a second time. | Every step of the job that does the work carries on after a failure, so a failed step does not end the job red (corrected in round 3: this said "the job never ends red", which the job's own time limit still can); a last step puts a warning on the run, and `CONTRIBUTING.md` says not to re-run the job. |
| S5, low; Q4, minor | Nothing bounded how long the summary job could hold the announcement back. | A time limit on every step and on the job. |
| S6, low; Q1, minor | The preview job read the commit its run was started for, so a re-run, a run from a branch, or two merges in quick succession could write about older files while saying "is on `master`". | The job runs from `master` only and checks out the head of `master`. |
| Q2, minor | Twelve of thirteen one-line changes to the wiring of the two new jobs failed no test; each would have switched the summary off without a sign. | The settings and the script of every step of both jobs are pinned as text, and so is the announce step's whole script. 25 such changes are now among the mutations. |
| Q3, minor; S note | The tests ran the step with `bash -eo pipefail`; the runner gives a step that names no shell `bash -e`. | The announce step now says `shell: bash`. With it the step fails when jq fails, whatever the webhook answers to the empty request curl still sends. The tests run each version of the step with the flags it has. |
| S note | `gh pr list` returns the first 30 pull requests before the filter is applied. | The lookup asks for the Actions bot's pull requests (`--app github-actions`). (Replaced in round 2 by a lookup of the exact branch: section 12.) |
| Q nits | `check` run outside the repository passed with nothing checked. A release PR for a prerelease would have failed the preview job on every push. The first-line message looked the same as what the author had written. A cut could leave the heading alone. Four small branches had no test. A correction to `docs/release-notes/0.37.0.md` would have failed the sample's tests. Four statements in `CONTRIBUTING.md` and one comment in the workflow file said more than is true. | `check` says so and exits 2. `comment` writes nothing for such a title and succeeds, and the job posts nothing. The message says what "exactly" means. A line that does not fit is cut inside when only headings came before it. Each branch has a test. The sample's tests read copies kept beside them. The statements are corrected. |

**Not taken.**

- *A second post when Discord refuses the first* (security note): posting the line alone if Discord
  answers 400 to the message with the summary. No input was found that makes Discord refuse, the
  length is guarded twice, and this is the step that holds the webhook, so it stays as small as it
  is. If a post is refused the step fails, as it always has, and the line can be posted by hand.
- *Narrower permissions for `release-please` and `publish-images`, and no token left in the image
  job's checkout* (security note; not from this change): Joshua's to decide, as a change of its own.
- *actionlint in CI* (quality): the same.

**The rules after round 1.** They replaced the list in section 10; section 12 has the rules as they
are now. Every line is read by itself. Round 2 showed that four statements in this list said more
than the check does. They are corrected here, each marked, so that nothing in this document still
claims them.

- The first line is exactly `### What's new`, and at least one bullet stands under it.
  `### How to use it` may follow once, on a line of its own. No other heading, behind list and
  quote markers either. (Corrected: this said "at any depth of list or quote". A heading inside a
  footnote was not seen, nor one behind a quote marker or a tab.)
- A line that starts with a pipe, or a row of dashes between pipes, is refused as a table. No
  image. No line of only dashes or only equals signs, of any length, and no three or more asterisks
  or underscores alone. (Corrected: this said "no table" outright. It names the two lines the check
  looks for.)
- No code block, fenced with backticks or with tildes, at the margin or inside a list. Code is
  written in backticks on one line, and a backtick with no partner on its line is refused. This is
  new: a block was where both reviewers could make the check and GitHub disagree about what is
  code. (Corrected: this went on to say that with no state carried from one line to the next there
  was nothing left to disagree about. Round 2 found three more shapes, within one line.)
- No `<` outside code other than a whole link `<https://…>`, and no HTML entity.
- No `@` followed by a name outside code. (Corrected: round 1 let an `@` in the path of a bare web
  address through, as "not a mention". GitHub reads such a path differently from the check in
  three shapes, and round 2 took the exemption out.)
- No control character other than a tab, no format character other than the joiner emoji are built
  with, and no line or paragraph separator.
- At most 1,800 characters in UTF-16 units. This is the one problem that does not keep a summary
  out of a release: such a file still goes out, cut for Discord.

**Other differences from the plan as approved**, beside those of section 10:

- The comment on the release PR shows the summary's text and says where it goes. It does not show
  the whole Discord message with its first line, as Q3 A put it; `preview` shows that.
- Step 6.3's run from the branch has GitHub parse the file and runs release-please alone; the
  preview job is skipped there.
- Section 10's "a code block that is opened is closed" and "only the two steps that call GitHub
  are given a token" are superseded by the rows above.

**What was run after these changes.**

- Release build from scratch: no errors, the baseline's 22 warnings, none in the new projects.
- Windows: the 1,292 earlier tests green; of 294 new ones, 284 green and 10 skipped — the nine that
  run the announce step's script, and one that creates a symbolic link, which this Windows user may
  not.
- Linux, with nothing skipped: all 294 pass. This ran offline in the .NET SDK container that was
  already on this machine (Ubuntu 24.04, bash 5.2.21, curl 8.5.0), with a real jq 1.8.1 binary and
  the libraries it needs copied out of another local image, and with `CI=true`. So the announce
  step's own script has been executed with a real jq: with no summary it posts byte for byte what
  the step of v0.37.0 posts, and with the 0.37.0 summary it posts the message kept beside the
  tests. The runner has jq 1.7.1, which was not to be had here; CI is the first run on that.
- The reviewers had run the same before me, each in its own way: the security reviewer the script
  alone with jq 1.8.2, the quality reviewer the test project with jq 1.8.1. The quality reviewer
  also ran actionlint 1.7.12 and shellcheck 0.11.0 on the workflow file as it was then, both
  clean, and the security reviewer rendered its inputs with GitHub's Markdown API — two
  unauthenticated calls with made-up text.
- 148 changes that each take one rule or guard out, or bend it: 45 in the rules, 11 in the texts,
  9 in the announcement, 3 in the outputs, 8 in the preview and its comment, 4 in versions, 5 in
  reading a summary, 4 in the folder, 10 in the commands, 4 in the warnings, 2 in the title and
  changelog readers, 42 in the workflow file and 1 in the sample. All 148 fail a test: 146 here,
  and the two about links on Linux, where a link can be made.
- The workflow file parses as YAML, and no file in the change holds a control, format or separator
  character.

**Still not run.** The jobs on GitHub. How GitHub evaluates the job-output expression, what
`!cancelled()` gives after a job-level timeout, and jq 1.7.1 are taken from documentation or from a
neighbouring version. Step 6.3 covers the parse; the release path first runs at the next release.

## 12. Review round 2 (2026-11-14)

The same two agents read commit `275834d`, the answers to round 1. Both found one problem that
round 1's own fix had brought in; the quality reviewer rated it major and the security reviewer
low. (Corrected in round 3: this said both had found "one major problem".) The quality reviewer also found three minor ones and a
group of nits. The security reviewer found three shapes of text that still passed the check and are
live mentions on GitHub, and four statements in section 11 that were not true; those are corrected
there, each marked.

**What they found, and what was done.**

| # | Finding | Answer |
|---|---|---|
| Major (quality), low (security) | Round 1 let every step of `release-summary` carry on after a failure. So when reading the release's notes failed part-way, the next step still ran, on an empty or half-written file, and with nothing under it the summary would have *replaced* the notes release-please wrote. | The notes are written under another name and moved into place only when the read succeeded, and the step that builds the texts runs only when the read did. A test runs the step's own script against a stand-in `gh` that fails part-way. |
| Security A | Three shapes the check passed and GitHub reads as a mention. A backtick inside a bare web address: GitHub takes the address up to the next space, backtick and all, so the "code" after it is text. A bare address inside square brackets, where round 1's exemption for an `@` in an address's path also hid an entity or an image. And a formula — `$`, then code, then `$` — inside which GitHub reads mentions. Two more shapes gave a heading or a box: a footnote and an alert. | Two answers. *The check:* the exemption is gone, and only a link written whole in angle brackets is not text; a backtick in a word after `://` or `www.`, or right after `$`, is refused; so is a line that starts a footnote or an alert. *And what the reviewer recommended as the answer that lasts:* the check stays a check for mistakes, and GitHub decides. Before the release is edited, `release-summary` has GitHub render the summary — its Markdown API, which posts nothing — and leaves the summary out when the answer holds a mention of a person or a team. It first asks about a text that is a mention for certain, the repository owner's name: if that answer does not look like a mention, the question means nothing, and the summary is left out too. So is one GitHub could not be asked about. |
| Security A, the comment | The release PR's comment rendered a summary the check had passed, and a comment notifies whoever it mentions the moment it is posted, before any release. | The comment always shows the summary as code, in a fence longer than any run of backticks in it. |
| Quality, minor | The preview job looked for release-please's pull request with `--app`, which goes through a search index: it can be behind, and it does not match a branch exactly. | The job asks for the pull request by its exact branch in this repository (`pulls?state=open&head=<owner>:release-please--branches--master`) and takes it only when the Actions bot opened it from this repository. Run read-only against the repository: it finds merged PR #175 when asked for closed ones, and prints nothing while none is open. |
| Quality, minor | A heading or a rule line behind a quote marker, or after a tab, was not seen. | List and quote markers are taken off together with the spaces and tabs after them, and a rule line is looked for both in the whole line and behind its markers. |
| Quality, minor; security note | The last step's warning could say something untrue. It spoke whenever any step had failed, and said the summary "could not be added" when it might have been. | It speaks when the texts were not built, when GitHub read a mention or could not be asked, or when the release was not edited. What it says holds in each case: the summary is not in the announcement and may be missing from the notes. |
| Nits, both | The job's time limit, 15 minutes, was under the sum of its steps', 17. The preview job's `concurrency` block had no test. Some guards had no test. Two documents said an over-long summary is cut "at a line end", which a cut inside a line is not. (Corrected in round 3: this said the two documents had said "trimmed".) A cut could end on a heading. A comment in a test helper said more than is true. A folder that is a link was followed, though a file that is one was not. The state file called the work reviewed before it was. | The job has 25 minutes and its steps 19 together, and a test adds them up. The block is pinned. Each guard the review named has a test (round 3 found two more without one: section 13). Both documents now say "cut short". A heading left at the end of a cut is dropped with the blank lines. The comment is corrected. Nothing is read through a link: not a file, not `docs/release-notes`, not `docs`. The state file says where the work stands. |

**Not taken.** Nothing new in this round. The three of section 11 stand, and are Joshua's to decide.

**One limit to know.** GitHub is asked at release time only. The comment on the release PR shows the
text and says that the question will be asked, but it cannot say what the answer will be. A summary
reaches that point with a live mention only if it got one past the check, in a shape nobody has
found yet; the release then goes out without the summary, and the run carries a warning.

**The rules after round 2.** They replaced section 11's list; section 13 has the rules as they are
now, after round 3 narrowed one of them. Every line is read by itself. The list says what the check
looks for. It is a check for mistakes, and GitHub has the last word at release time.

- The first line is exactly `### What's new`, and at least one bullet stands under it.
  `### How to use it` may follow once, on a line of its own. No other heading: one to six number
  signs and a space, at the start of a line or behind list and quote markers and the spaces or tabs
  after them.
- A line that starts with a pipe, or a row of dashes between pipes, is refused as a table. No
  image. No line of only dashes or only equals signs, of any length, and no three or more asterisks
  or underscores alone, whole or behind list and quote markers.
- No line that starts a footnote (`[^`) or an alert box (`[!`).
- No code block, fenced with backticks or with tildes. Code is written in backticks on one line. A
  backtick with no partner on its line is refused, and so is a backtick in a word that holds a web
  address before it (`://` or `www.`), or right after a `$`.
- No `<` outside code other than a whole link `<https://…>` or `<http://…>`, and no HTML entity.
- No `@` followed by a letter, a digit or an underscore outside code. A web address with an `@` in
  it has to be written whole in angle brackets.
- No control character other than a tab, no format character other than the joiner emoji are built
  with, and no line or paragraph separator.
- At most 1,800 characters in UTF-16 units. This is the one problem that does not keep a summary
  out of a release: such a file still goes out, cut short for Discord.

**The release path as it is now.** `release-summary` has seven steps. Each but the last carries on
after a failure and has a time limit of its own, 19 minutes together under the job's 25. It checks
out the tag and keeps no token; sets up the SDK; reads the notes release-please wrote; builds the
texts, with no token; asks GitHub whether the summary mentions anyone; puts the summary on top of
the notes; and, when a summary may be missing, says so in a warning on the run. The announcement
gets the summary only when the notes got it.

**Other differences from the plan as approved**, beside those of sections 10 and 11:

- `render` takes `--summary-out`, and writes the summary by itself there for the question to GitHub.
- Round 1's rules let an `@` stand in the path of a bare web address; D2 had not said either way.
  Such an address now goes whole in angle brackets, or in backticks. (Corrected in round 3: this
  said D2 had allowed it.)

**What was run after these changes.**

- Release build of the whole solution from scratch: no errors, the baseline's 22 warnings, none in
  the new projects.
- Windows: the 1,292 earlier tests green; of 337 new ones, 321 green and 16 skipped — the thirteen
  that run a step's own script with bash, and three that make a symbolic link, which this Windows
  user may not.
- Linux, with nothing skipped: all 337 pass. As in round 1 this ran offline in the .NET SDK
  container already on this machine, with the real jq 1.8.1 and `CI=true`.
- The image job's test command, with its filter: exits 0 and runs the 1,292 earlier tests.
- The step that asks GitHub, run for real. Its script was taken out of the workflow file and run
  here with the real `gh` against GitHub's Markdown API, with made-up text and a public sample
  repository as the context: sixteen requests in all, over three versions of the script, the last
  seven with the script as it stands. A line with a mention failed the step with the warning. A
  line with `who:@GM` in backticks passed. The reviewer's first shape — a backtick inside a bare
  web address, then a name — failed it: GitHub reads the mention there, which is what the step is
  for. And with an owner name that does not exist the step stopped at its control. Nothing from
  this repository was sent, and nothing was posted anywhere. The requests went out under the login
  `gh` has on this machine.
- 191 changes that each take one rule or guard out, or bend it: 60 in the rules, 15 in the texts,
  9 in the announcement, 3 in the outputs, 10 in the preview and its comment, 4 in versions, 5 in
  reading a summary, 8 in the folder, 13 in the commands, 4 in the warnings, 2 in the title and
  changelog readers, 57 in the workflow file and 1 in the sample. All 191 fail a test: 184 here,
  and the seven about links on Linux, where a link can be made. Seven of the workflow ones bend
  the scripts of the two steps that are run for real. Those were run on Linux as well, and each
  fails a test that runs the script, not only the test that pins its text. Writing the list found
  two pieces of code no test could miss, because nothing depended on them: a flag set just before
  the loop it was read in ended, and a second refusal of a backtick inside a link in angle
  brackets, which the rule for backticks in web addresses already refuses. Both are gone.
- The workflow file parses as YAML, and none of the 55 files in the change holds a control, format
  or separator character.
- `check` passes on this checkout, and `preview --version 0.37.0` with the real notes gives the
  same texts as before: the Discord message is 838 of 2,000 characters.

**Still not run.** The jobs on GitHub, as in section 11. One thing more is taken from documentation:
that the token a workflow run gets may call the Markdown API and is given mentions as links there.
GitHub documents the first. The step's control exists for the second: if a run's answer about a
certain mention does not look like one, the summary is left out and the log says why.

## 13. Review round 3 (2026-11-14)

The same two agents read commit `45adb31`, the answers to round 2. Both report every one of their
round-2 findings closed. The security reviewer found no security problem; the quality reviewer
found nothing major and nothing the answers had broken. Both found the same fault in a rule round 2
had added, and each a few smaller things.

**What they found, and what was done.**

| # | Finding | Answer |
|---|---|---|
| Low (security), minor (quality) | The rule round 2 added for a backtick glued to a web address or a `$` looked at every backtick on the line, the one that closes code as well. So it refused ordinary code that ends in an address or a dollar sign — an address shown as code, a `curl` line, a pattern such as `^abc$` — with a message that is untrue there. And a bare address with an `@` in it was told to go in backticks, where it was then told to have a space before it. | Only a backtick met outside code is asked about. Code already read counts as a space, so it ends the word before it. The mention message names both ways out: backticks, or the address whole in angle brackets. Checked on the reviewers' own probe files, 590 summaries in 299 folders, with the check as it was and as it is: 20 verdicts changed, all from refused to passing. Eighteen are plain code. The other two — code right after a link in angle brackets, and code whose content is wrapped in dollar signs — I put to GitHub's renderer, which reads both as code. The two shapes that are live mentions are still refused. |
| Info (security) | The step's search also matched a mention's class typed into a summary as text. Such a summary would have been left out, with a warning that named a mention. | The search is tied to the link tag. A test gives the step the class as text and as code, and it passes. |
| Question (security) | The examples pointed at a YouTube handle nobody had checked: a public guide naming an address anyone could register. | They use `example.com`, which is reserved for examples. |
| Statements (security) | Four were not true: the documents' line on backticks, which described half of what the rule did; "both found one major problem" in section 12 and in the audit entry; "every step carries on" in a workflow comment, though the last step does not; and "never ends red". | The rule now does what the line says. Section 12 says who rated what, and the next audit entry corrects the last. The comment says "every step that does the work". The documents say a failed step does not end the job red. |
| Nits (quality) | 1. No test that runs the step failed the request about the summary alone, so three bends of the script failed only the test that pins its text; one of them would have published a summary without an answer. 2. The time-limit test compared the steps' sum with a number written in the test. 3. Two guards had no test: a tab after a numbered marker, and `WWW.` in capitals. 4. Three comments still spoke of the announce step alone. 5. Three statements in section 12. 6. The tests that pin a script pinned its comments too. | 1. The stand-in `gh` fails one request at a time, and each of the three bends now fails a test that runs the step. 2. It reads the job's limit from the file. 3. Both have tests. 4. Corrected. 5. Corrected in place, each marked. 6. The pins compare a script's commands and leave its comments out. (The confirmation round found this left out too much: section 14.) |

**Limits the reviewers noted, which are not findings.**

- The step's control is a person's name, so it proves the step sees a mention of a person. A team
  is seen only by a token that can see the team. This repository belongs to a user account and has
  no teams, and the check refuses `@org/team` like any other `@name`.
- Whether the token of a workflow run is given mentions as links is not known before a run. GitHub
  documents that such a token may call the render API (read on 2026-11-14). If mentions do not come
  back as links, the control stops the step, every summary is left out, and the log says why: safe,
  but the feature would be off until that is looked at.

**The rules as they are now.** Every line is read by itself. The list says what the check looks
for. It is a check for mistakes, and GitHub has the last word at release time.

- The first line is exactly `### What's new`, and at least one bullet stands under it.
  `### How to use it` may follow once, on a line of its own. No other heading: one to six number
  signs and a space, at the start of a line or behind list and quote markers and the spaces or tabs
  after them.
- A line that starts with a pipe, or a row of dashes between pipes, is refused as a table. No
  image. No line of only dashes or only equals signs, of any length, and no three or more asterisks
  or underscores alone, whole or behind list and quote markers.
- No line that starts a footnote (`[^`) or an alert box (`[!`).
- No code block, fenced with backticks or with tildes. Code is written in backticks on one line. A
  backtick with no partner on its line is refused. So is a backtick that would open code but is
  glued to a web address (`://` or `www.` earlier in the same word) or stands right after a `$`:
  an address takes the backtick along, and code with a `$` glued to each end is a formula.
  (Corrected in the
  confirmation round: this said GitHub "does not open code" there, which for a `$` is so only
  when another `$` follows the code.) What the code itself holds or ends in does not matter.
- No `<` outside code other than a whole link `<https://…>` or `<http://…>` with no backtick in
  it, and no HTML entity.
- No `@` followed by a letter, a digit or an underscore outside code. A web address with an `@` in
  it goes in backticks, or whole in angle brackets.
- No control character other than a tab, no format character other than the joiner emoji are built
  with, and no line or paragraph separator.
- At most 1,800 characters in UTF-16 units. This is the one problem that does not keep a summary
  out of a release: such a file still goes out, cut short for Discord.

**What was run after these changes.**

- Release build of the whole solution from scratch: no errors, the baseline's 22 warnings, none in
  the new projects.
- Windows: the 1,292 earlier tests green; of 349 new ones, 332 green and 17 skipped — the fourteen
  that run a step's own script with bash, and three that make a symbolic link.
- Linux, with nothing skipped: all 349 pass, offline, with the real jq 1.8.1 and `CI=true`.
- The image job's test command, with its filter: exits 0 and runs the 1,292 earlier tests.
- 198 changes that each take one rule or guard out, or bend it: 63 in the rules, 15 in the texts,
  9 in the announcement, 3 in the outputs, 10 in the preview and its comment, 4 in versions, 5 in
  reading a summary, 8 in the folder, 13 in the commands, 4 in the warnings, 2 in the title and
  changelog readers, 61 in the workflow file and 1 in the sample. All 198 fail a test: 191 here,
  and the seven about links on Linux. Eleven of the workflow ones bend the scripts of the two
  summary-job steps that are run for real. Run on Linux as well, each fails a test that runs the
  script, the three the quality reviewer listed among them.
- GitHub's renderer, asked from this machine with made-up lines and a public sample repository as
  the context. Eleven requests without a login: a code span right after a link in angle brackets,
  a code span wrapped in dollar signs, and code ending in an address or a dollar sign followed by
  more code are all code, with no mention; a backtick glued to a bare address and a formula round
  code are both live mentions, and both are refused. Six requests under this machine's `gh` login,
  running the step's own script with its search tied to the link tag: a mention fails it, the
  class typed as text and as code passes, and code with an `@` in it passes. With the sixteen of
  round 2 that is thirty-three requests from this session. Nothing from this repository was sent,
  and nothing was posted.
- The reviewers' own runs in this round: the security reviewer about fifty requests to the render
  API, without a login and with made-up text; the quality reviewer none.
- The workflow file parses as YAML, and none of the files in the change holds a control, format or
  separator character.
- `check` passes on this checkout, and `preview --version 0.37.0` with the real notes gives the
  same texts as before: the Discord message is 838 of 2,000 characters.

**Still not run.** The jobs on GitHub, jq 1.7.1, and what a workflow run's token gets back from
the render API, as above. The release path first runs at the next release, and fails safe.

## 14. Confirmation round (2026-11-14)

Both agents were asked to confirm round 3's answers on the diff from `45adb31` to `f9b7c4e` alone.
The rules listed in section 13 are the rules as they are; this round changed the wording of one
message and one test helper.

**Security.** Nothing that is live on GitHub passes the check: all 31 forms GitHub had rendered
as a mention in earlier rounds are refused, and of the reviewer's 297 crafted folders ten changed
verdict, all from refused to passing and all code on GitHub. Sixteen new attempts to break the
narrowed rule all came back from GitHub as code. The step's search tied to the link tag behaves:
the class typed as text passes, and a mention, the two live shapes and an organization's name
fail it. One statement was still a little too broad, in four places: a `$` glued to code is a
formula on GitHub only when another `$` follows the code; without it GitHub opens code as usual.
The check refuses both, which is the safe side, but the message, `CONTRIBUTING.md` and section 13
said the backtick "does not open code" in either case. They now say it may not, and that code
with a `$` glued to each end is a formula. (Worded more exactly on 2026-11-16: this said "code
between two `$`".)

**Quality.** Everything of round 3 is closed, and nothing in section 13 that could be checked is
untrue. One thing was wrong in the answer to nit 6. The tests that pin a script had been made to
leave out every line that starts with a number sign. After a line that ends in a backslash such a
line is no comment: bash joins the two, and the rest of the command is cut off. A comment put
there would have passed every test while the step lost its arguments. Now a number-sign line is
left out only where a command could start: not after a backslash, not inside quoted text that
runs over several lines, not after a here-document has begun. The helper counts quotes plainly,
which can only err towards pinning more. A test holds it to that, another says that every comment
in the workflow's scripts stands where a command could start, and five new changes of that kind —
the reviewer's own among them — each fail a test. (Corrected on 2026-11-16: the helper described
here did not hold. Counting quotes can err the other way too, and a comment is not inert on
GitHub. It is replaced: section 15.)

**These last two answers were checked by me and not read again by the agents.** Each agent's
round costs far more than the change it would confirm: a wording, and a helper of the tests.
(A fresh agent read them on 2026-11-16, and found the helper wanting: section 15.)

**What was run after these changes.**

- Windows: 351 new tests, 334 green and 17 skipped — the fourteen that run a step's own script
  with bash and the three that make a symbolic link. Linux, offline, with the real jq 1.8.1 and
  `CI=true`: all 351 green.
- 203 changes that each take one rule or guard out, or bend it: the 198 of section 13 and the
  five above. All 203 fail a test: 196 here, and the seven about links on Linux. The eleven that
  bend a script which is run for real were run on Linux again, and each fails a test that runs it.
- Release build of the whole solution from scratch, the full suite and the image job's test
  command, on the final code: as in section 13.
- The numbers of section 13 that this round did not touch stand: the build's 22 warnings, the
  1,292 earlier tests, the image job's command, and the requests to GitHub's renderer from this
  session, thirty-three. In this round the security reviewer made about thirty more of its own,
  without a login and with made-up text; the quality reviewer none.

## 15. The pull request, and a fresh read of the last two commits (2026-11-16); the change as it stands

**Steps 6.2 and 6.3, on Joshua's "B) Continue".** `master` had gained the 0.37.0 records
(`56b61ab`, pull request #178) and nothing else. It was merged into the branch as `55062f6`. Two
files conflicted, as expected. In `aidlc-docs/audit.md` both sides had only appended; their
entries, 26 from `master` and 9 from this branch, are kept whole and stand in the order of their
timestamps. In `aidlc-docs/aidlc-state.md` this branch's section follows `master`'s text. A
script checked both before anything was written. The branch was pushed and pull request #179
opened at 2026-11-16T14:51:52Z. The release workflow was then started once by hand from the
branch, after checking that it could release nothing (no release pull request open; v0.37.0 both
the latest release and the newest version in `CHANGELOG.md`; without a `target-branch` the action
works on the default branch). Run 43890372277 succeeded in fifteen seconds: GitHub parsed the
changed file, `release-please` found nothing to release, and the other four jobs were skipped.
Nothing was created, and nothing was posted to Discord.

**On GitHub's runner** (`build-test` on #179, image ubuntu-26.04): all 1,643 tests passed, the
351 of the new project among them with none skipped. That is the first run of the three steps'
scripts against the runner's own bash, jq and curl. `pr-title` passed. No Copilot review was
requested on #179; the agents' rounds stand in for it.

**A fresh agent then read the two commits no reviewer had read**: `0799ea4`, the confirmation
round's two corrections, and the merge `55062f6`. The merge holds: the common start is a byte
prefix of both sides' audit log, the 35 entries are each there once and whole with their stamps
ascending, the state file is `master`'s followed by this branch's section, and no other file
differs from both parents. The wording about `$` holds, with one nit. In the helper of the tests
it found three things, all in what the confirmation round's answer had built.

| # | Finding | Answer |
|---|---|---|
| A | A comment is not inert on GitHub. An expression in it is filled in before bash sees the script, and what is filled in is then read as shell. The tests left comments out of what they pin, so a comment naming the summary's output would have passed every test while putting the summary's text, line by line, into the announce step's shell, beside the webhook secret. | A test: no script in the workflow has an expression in it, in a comment or anywhere else. Values reach a script through `env`. |
| B | The tests read the workflow file as text, not as YAML. A comment behind a no-break space is a comment to the tests and a command to bash. A line end of another kind inside a comment ends the line for a YAML reader and not for the tests. A comment indented deeper, as a block's first line, makes the file invalid. | Only a space or a tab indents a comment. Three tests hold the file to what makes the two readings agree: no character that two readers could take differently; every script either a plain text on its key's line or a block under `run: \|` that starts at its own indentation; and the test of row A. |
| C | Section 14 said that counting quotes "can only err towards pinning more". It can err the other way: after an apostrophe inside double quotes, a number-sign line inside quoted text over several lines was dropped. | The counting is gone. The tests no longer decide what bash takes for a comment. Each number-sign line is cut down to its number sign and stays where it stands, so a line added, taken out or moved changes the pinned text wherever it is, and only what a comment says is free. |
| Nit | "Code between two `$`" is a formula only when a `$` is glued to each end. | The message and `CONTRIBUTING.md` say so. |

The reviewer also put a marker comment at each of 228 positions in the seven pinned scripts and
asked bash's own parser: every number-sign line the old helper dropped was a true comment, so
nothing wrong had got through; the fault was in what the helper claimed and in what it would have
let through later. Sections 13 and 14 are corrected in place where they said otherwise.

**What was run after these changes.**

- Release build of the whole solution from scratch: no errors, the baseline's 22 warnings.
- The 1,292 earlier tests green. 353 new ones: on Windows 336 green and 17 skipped (the fourteen
  that run a step's script with bash, the three that make a symbolic link); on Linux, offline,
  with the real jq 1.8.1 and `CI=true`, all 353 green.
- 207 changes that each take one rule or guard out, or bend it: the 203 of section 14 and four
  from this read — an expression in a comment, a comment behind a no-break space, a block whose
  first line is indented deeper, and a one-line script with a comment at its end. All 207 fail a
  test: 200 here, and the seven about links on Linux. The eleven that bend a script which is run
  for real were run on Linux again, and each fails a test that runs it.
- The workflow file parses as YAML, and no file in the change holds a control, format or
  separator character.

**Requests to GitHub in this part of the work.** The reads and the three writes named above (the
push, the pull request, the start of the workflow), all on Joshua's go-ahead. The fresh agent made
two requests to GitHub's renderer, without a login and with made-up text.

**Still not run.** `release-summary` and the announcement with a summary: they exist only when a
release is created. The preview job first runs on the push to `master` that the merge makes; with
no release pull request open it finds none and posts nothing.
