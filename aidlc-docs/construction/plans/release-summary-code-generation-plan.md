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
- [ ] 5.3 Two review agents, one on security and one on correctness and quality, read the local commits before anything is pushed, as on the last three changes; findings answered.

**6. Bookkeeping and delivery — each outward step on Joshua's own go-ahead**
- [ ] 6.1 `audit.md` entries throughout; `aidlc-state.md` carries a section for this work.
- [ ] 6.2 `master` merged into the branch (it will have gained the 0.37.0 records by then), `audit.md` kept in the order of its timestamps; then the push and the pull request.
- [ ] 6.3 The workflow run once by hand from the branch (the `workflow_dispatch` trigger it already has), so that GitHub parses the changed file and the path where nothing is released runs: release-please finds nothing to do, and the preview job finds no open release PR. Checks green; Copilot's review if it starts, the two agents if it does not.
- [ ] 6.4 The squash merge through the REST call. It cuts no release.

What cannot be run before a real release: the two jobs that only exist when one is created. They are
covered by the tool's tests and by the announce step run for real in CI (2.7), and they fail safe
(D5). The first real use is the next release.

## 8. Risks

| Risk | Answer |
|---|---|
| A mistake in the workflow file stops every job, release-please included. | 3.4 and 6.3: the file is parsed locally and then by GitHub itself, from the branch, before the merge. |
| The summary's file name does not match the version released. | The announcement is as today, nothing breaks. Q3 A shows it on the release PR beforehand. |
| A summary pings someone. | `allowed_mentions` names the one role and is unchanged, so nothing else in the text can ping in Discord; the check refuses mentions outside code, which also covers GitHub's `@name`. |
| The summary job breaks on a release day. | D5: `announce-release` does not depend on its success. |
| Text from an untrusted source reaches the announcement. | The text comes only from a file at the release tag, which got there through a reviewed PR — the same trust as the workflow file. Nothing is read from PR titles, comments or forks. |

## 9. Not in this change

- Adding the 0.37.0 summary to the published v0.37.0 release notes, or posting it to Discord after
  the fact. Both are public edits and Joshua's to decide once this is in.
- Any change to how release-please versions, writes `CHANGELOG.md` or opens its PR.
- A summary as a condition for releasing. It stays optional by design.

## 10. As built (2026-11-14): what differs from the plan as approved, and what was run

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
