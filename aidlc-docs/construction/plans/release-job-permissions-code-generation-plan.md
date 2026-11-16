# Release workflow — least access for the two older jobs: code generation plan

**Request**: a card proposed by the session that built the release summary (pull request #179) and
started for this session on 2026-11-16; nothing was typed to this session. Give the two older jobs
of `.github/workflows/release-please.yml`, `release-please` and `publish-images`, only the
repository access each needs, make the workflow's default as small as it can be, and stop the image
job's checkout from keeping its token. The security review of #179 had noted that the two jobs hold
more access than they use and that the image job's checkout keeps a token. Neither was part of that
change, and both were left as Joshua's to decide (`release-summary-code-generation-plan.md`,
section 11, "Not taken").
**Depth**: minimal — one unit; the task is the requirement, and what was verified, the change, its
steps and what a release will show are this one document. User stories, application design, units,
NFR and infrastructure stages are skipped: release tooling, with nothing in it a member sees.
**Baseline**: `master` 4af1305 (pull request #179 merged; latest release v0.37.0). **Branch**:
`ci/narrow-release-job-permissions` → PR `ci: give the two older release jobs only the access each
needs` (`ci:` cuts no release; the change takes effect with the run its own merge starts).
**Status**: generated on the local branch on 2026-11-16 and read by two review agents the same day
(section 8). On Joshua's go-ahead of that day the branch was pushed, the workflow run once by hand
from it (section 4) and pull request #181 opened; on his go-ahead for the merge it was squash-merged
the same day as `ab32e96`. Step 5.1, the read of the next release's run, stays open.

## 1. What exists today (verified 2026-11-16)

- **The workflow** at `master` 4af1305 gives `contents: write`, `pull-requests: write` and
  `packages: write` at its outermost level. `release-please` and `publish-images` have no
  `permissions` of their own and so hold all three. The three jobs that #179 added or changed state
  their own: `release-summary` `contents: write`, `announce-release` none (`permissions: {}`),
  `release-summary-preview` `contents: read` and `pull-requests: write`.
- **A job's own block replaces the workflow's default. It is not added to it, and it is not held to
  it either.** GitHub's workflow syntax reference: "If you specify the access for any of these
  permissions, all of those that are not specified are set to `none`", and the permissions are
  adjusted "first at the workflow level and then at the job level". The runs show both directions,
  since each job's log begins with the access its token has.
  - Less than the default: in the run that released 0.37.0 (43515848010) `release-please` and
    `publish-images` each had `Contents: write`, `Metadata: read`, `Packages: write` and
    `PullRequests: write`, and `announce-release` had `Metadata: read` alone. In the run for the
    merge of #179 (43912177390) `release-summary-preview` had `Contents: read`, `Metadata: read` and
    `PullRequests: write`.
  - More than the default, which this change relies on: `codeql.yml` gives `contents: read` at its
    outermost level and its one job a block with more, and that job's token had `Actions: read`,
    `PullRequests: read` and `SecurityEvents: write` beside `Contents: read` (run 43912177365,
    2026-11-16).
- **Every token can read the repository's metadata**, whatever a workflow says. Where this
  document says a job has no permissions, it means nothing beyond that.
- **The repository's own setting**, which a workflow that says nothing falls back to, is read-only
  access, with Actions allowed to open pull requests.

## 2. What each job needs, and how that was worked out

### `release-please`

One step, `googleapis/release-please-action@v4`. The runs resolve the tag to v4.4.1 (commit
`5c625bf`), which runs release-please 17.3.0.

What it did in this repository, read from the logs of four runs, each with the token of four
permissions named above:

| Run | What happened | What release-please wrote |
|---|---|---|
| 43328559894, 2026-11-12 | the first `feat:` after 0.36.4 reached `master` | created the branch `release-please--branches--master`, a tree and a commit, moved the branch to the commit, opened pull request #175, put the label `autorelease: pending` on it |
| 43331726475, 2026-11-12 | a `docs:` commit reached `master` with #175 open | nothing ("remained the same") |
| 43501031245, 2026-11-13 | a second `feat:` reached `master` | a new tree and commit, the branch moved, the pull request updated |
| 43515848010, 2026-11-13 | #175 was merged | created the release, and the tag with it; commented on #175; took `autorelease: pending` off it and put `autorelease: tagged` on |

- In GitHub's terms the branch, the commit, the tag and the release are `contents`, and the pull
  request, its labels and the comment on it are `pull-requests`. The reference's own examples are
  these two: "`contents: write` allows the action to create a release" and "`pull-requests: write`
  permits an action to add a label to a pull request".
- None of this is a package. At 17.3.0 release-please's own calls to GitHub (`src/github.ts`) read
  the repository, its commits, tags, releases and pull requests, and write refs, file contents,
  releases, pull requests, comments and labels. None is for packages. The library it opens pull
  requests with (`code-suggester`) is what wrote the branch, the commit, the pull request and the
  label of the first row.
- **The action's README asks for three**: `contents: write`, `issues: write` and
  `pull-requests: write`. The middle one was added on 2025-09-09 (the action's pull request 1108,
  for its issue 1105), where other repositories had reported that without it the action failed
  when it had to make a label that did not exist yet: "You do not have permission to create labels
  on this repository". This repository's own history is different. The workflow has never had
  `issues: write`, and its first release, on 2026-07-17, made both labels all the same:
  `autorelease: pending` and `autorelease: tagged` were each created in the very second in which
  the job first put them on release PR #2. The two runs of the table that labelled did so without
  it as well. The repository was still private at that hour, and whether GitHub would let the job
  make a label again today has not been tried.
- **So: `contents: write` and `pull-requests: write`** — the two that every write in those runs
  falls under. What goes is `packages: write`, for which release-please has no call.

### `publish-images`

| Step | What it takes from the token |
|---|---|
| `actions/checkout@v4`, the release's tag | `contents: read` (the action's README, "Recommended permissions") |
| `actions/setup-dotnet@v4` | nothing |
| `dotnet test` on the tag | nothing |
| `docker/login-action@v3`, to `ghcr.io` with the token | `packages` |
| `docker build` and `docker push`: three images, two tags each | `packages: write` |

- GitHub's guide to publishing Docker images gives the job that pushes to GHCR `contents: read` and
  `packages: write`, and two more for attestations, which this workflow does not make.
- **The same pair already publishes the same three packages.** `publish-main` in `ci.yml` has
  exactly `contents: read` and `packages: write`, and runs on a push to `master` once `build-test`
  has passed, with the same checkout and login actions. Its run for the merge of #179
  (43912177374, 2026-11-16): a token with `Contents: read`, `Metadata: read` and `Packages: write`;
  logged in to `ghcr.io`; pushed `:main` and `:sha-4af1305` for `calcrony-api`, `calcrony-bot` and
  `calcrony-web`.
- **So: `contents: read` and `packages: write`.** What goes is the write access to the repository's
  contents and to pull requests, which no step uses.
- **The token the checkout keeps.** Unless told otherwise, `actions/checkout` leaves the token in
  the checkout's git config until the job ends (its README: "The auth token is persisted in the
  local git config. ... Set `persist-credentials: false` to opt-out"). No step after the checkout
  uses git: the tests and the image builds read files, and the login step is handed the token
  itself. Looked for, and not found: a build file, test or script in the repository that calls git,
  a submodule, a package that takes a version from git. So the checkout gets
  `persist-credentials: false`, as the two jobs of #179 have it. What that changes is real and
  small: the tests on the tag, which are code from the repository, no longer run beside a file that
  holds the token. It does not put the token out of their reach, since code that runs in a job can
  come by the job's token in other ways, and for the image builds it changes nothing, since the
  Dockerfiles copy from `src/` only. The larger gain for this job is the other one: its token can
  push packages and read the repository, and can no longer write to the repository or to pull
  requests.

## 3. The change

| Where | Before | After |
|---|---|---|
| The workflow's default | `contents: write`, `pull-requests: write`, `packages: write` | none: `permissions: {}` |
| `release-please` | the default | `contents: write`, `pull-requests: write` |
| `publish-images` | the default | `contents: read`, `packages: write` |
| `publish-images`, its checkout | the token kept in git's config | `persist-credentials: false` |

- **The default is `{}` and not left out.** With the key gone, a job without a block of its own
  would fall back to the repository's setting, which can be changed outside this file. With `{}`,
  a job added later without a block gets nothing, whatever that setting says.
- **Nothing else in the file changes.** Both versions were parsed and compared: with those four
  things taken out they are the same data, and the text of the three newer jobs is the same byte
  for byte. That includes one comment that now reads oddly: in `announce-release`, above its
  `permissions`, "don't hand it the workflow's write token" — the workflow no longer has one. It
  stays because the task says to change nothing in that job; rewording it is Joshua's call
  (section 6).
- **Tests**, in `ReleaseWorkflowTests`: six new ones, tightened in two rounds of review
  (section 8). Between them they hold every line of the two older jobs that is not a comment.
  - The workflow starts on a push to `master` or by hand, and on nothing else.
  - Two about the file's form, so that the tests and YAML read the same lines: every job has its
    keys four spaces in, with nothing further out; and only a script is a block of text, with any
    quoted value closed on its own line.
  - The default is `permissions: {}`; the workflow has its five jobs and no other; each states its
    permissions, once.
  - `release-please`: its keys, its runner, its two permissions and no other, its one step, the
    action, which is given no token of its own and so takes the job's, and its four outputs, which
    decide whether the image job runs and which tag it checks out.
  - `publish-images`: its keys, that it runs for a release only and on GitHub's runner, its two
    permissions and no other, its one variable, its five steps in their order with what each is
    given, the checkout keeping no token, the build step's script, and every expression in the
    job, so that the token is named once, for the login.
  - In `ReleaseWorkflow`, the helper that reads the file: `JobNames` is new, and refuses a job
    whose key does not stand alone on its line; `Block` and `Step` now end where YAML ends them, at
    the next setting indented no deeper, and no longer at a comment. That closes the same gap in
    the tests #179 wrote for the three newer jobs. No assertion of an existing test was changed,
    and no pin of theirs moved: the outermost keys are still `name`, `on`, `permissions`, `jobs`.
    The list of pinned scripts that one of those tests walks gained the build step's.
- **`CONTRIBUTING.md` is unchanged**: nothing it says about the release flow changes.

## 4. What can be run before a release, and what cannot

**Run here (2026-11-16).**

- The file parses as YAML, before and after, and the comparison of section 3 holds.
- The release-notes test project: 361 tests, 344 green and 17 skipped. The 17 are the ones that are
  skipped on Windows without this change too (fourteen run a step's script with bash, three need a
  symbolic link); CI runs all of them. Before the change the project had 355.
- Changes that each bend the workflow one way, each run against the tests: 36 of my own, the 29
  the security reviewer made in its first round, and the 56 and then 18 files the correctness
  reviewer made in its two. They widen, change or remove the default; add, take or change a
  permission on either job, or hide one behind a comment; remove a job's block or write it on one
  line; keep the token in the checkout, in a second checkout however it is spelled, or hand it to
  another step under another name; add, swap or reorder steps; give the release action another
  token; change what the release job hands on; add a variable for every step of the image job;
  change the build script; add or widen a trigger; add a sixth job, with or without a block, in a
  form the tests could not see; write a job with its real keys at another indentation and the
  lines the tests look for parked in a block of text. Every one fails a test, but for two that
  are meant to pass: a harmless comment inside a permissions block, and one inside a step.
- actionlint 1.7.12, from an image already on this machine, run without network: nothing to report
  on the file, before or after. (It is not part of CI.)
- A Release build of the whole solution from scratch: no errors and the baseline's 22 warnings. The
  whole suite: the 1,292 earlier tests green (721 API, 418 bot, 153 web), and the 361.

**Can be run on GitHub before a release**, on Joshua's go-ahead: the workflow started once by hand
from the branch. GitHub parses the file. `release-please` runs against `master` with its own token
and reads commits, releases and pull requests. The first lines of its log say what access it got;
they should read `Contents: write`, `Metadata: read`, `PullRequests: write`, which would also be
the first time this workflow shows a job block wider than its default. With nothing waiting to be
released it writes nothing, and the other four jobs are skipped. Checked before it is started:
that no merged release PR still carries `autorelease: pending`, that the latest release is the
newest version in the changelog, and what has reached `master` since.

**Run on GitHub (2026-11-16)**: run 43973160250, started by hand from the branch at `b766a2f` at
22:46:00Z, after those three checks: no merged pull request carried `autorelease: pending`; v0.37.0
was both the latest release and the newest version in the changelog; `master` had gained only a
`docs:` and a `ci:` commit since. It succeeded in seventeen seconds. GitHub parsed the file. The
log of `release-please` gives its token as `Contents: write`, `Metadata: read`,
`PullRequests: write`, and the action as commit `5c625bf`, running release-please 17.3.0. It worked
on `master`, considered the two commits since v0.37.0, found no user-facing commit and skipped.
Nothing was written: no release PR, no branch, no tag, no release. The other four jobs were
skipped. What the run does not show: a write under the new token, since there was nothing to
write, and anything of `publish-images`.

**The run the merge started (2026-11-16)**: run 43977699270, from `master` at `ab32e96`, 23:15:34Z
to 23:15:55Z, green — the workflow's first run from `master` with the default at none. The log of
`release-please` gives its token as `Contents: write`, `Metadata: read`, `PullRequests: write`. It
considered the three commits since v0.37.0, found no user-facing one and skipped, so again nothing
was written. `release-summary-preview` ran, as it does on every push to `master`, with the token
its own block gives — `Contents: read`, `Metadata: read`, `PullRequests: write`, the same as in the
push run before this change (43912177390) — found no open release PR and did nothing more. The
other three jobs were skipped.

**Cannot be run before a release**: `publish-images` as a whole, and what `release-please` writes
at release time. Both happen only when a release is created. **So the first full run of this change
is the next release.** What that release does for the first time:

1. `publish-images` under its own token: the checkout of the tag with no token kept, the tests on
   the tag, the login and six pushes. Each part has been done before in another place: the pair of
   permissions, the login and the pushes by `publish-main`; a checkout with
   `persist-credentials: false` is the action's documented behaviour, though in this repository it
   is otherwise written only in the two jobs of #179, which have not run a checkout yet.
2. `release-please` creating the tag and the release, commenting and changing the labels under its
   own token.
3. From #179, and not from this change: `release-summary` and the announcement with a summary also
   run for the first time.

**The order of the next two merges decides how much is seen before that release.** Pull request
#180, a `fix:`, is open. If this change is merged first, #180's own run opens the release PR for
the next version under the new token — the branch, the commit, the pull request and its label —
which uses both of `release-please`'s permissions before any release. If #180 is merged first, the
release PR is opened under the old token and this change's run finds it unchanged. Unless another
`feat:` or `fix:` follows before the release, the release itself is then the first time the job
writes with its own.

**As it went (2026-11-16):** this change was merged first, with #180 still open. So the next `feat:`
or `fix:` to reach `master`, #180 if it is merged next, opens the release PR under the new token.

## 5. What a release would look like if a permission were missing

| Missing | The step that fails, and when | What exists afterwards | The announcement |
|---|---|---|---|
| `release-please`: `contents: write` | the job's one step, the first time it has to write: on a push that opens or updates the release PR, or, at a release, when it creates the release | no tag and no release; the other four jobs are skipped | not posted |
| `release-please`: `pull-requests: write` | the same step: on a push, when it opens or updates the release PR, after the branch is written; at a release, at the comment, after the tag and the release exist | at a release: the tag and the release, with no images and no summary, because the job ended red and the jobs that wait for it are skipped | not posted |
| `publish-images`: `packages: write` | "Build and push images", at its first push, if not already "Log in to GHCR" | the tag and the release, with the summary on its notes (that job does not wait for the images); no image of that version | not posted |
| `publish-images`: `contents: read` | the checkout, if GitHub refuses the read; the repository is public, so it may not, and then nothing fails | if it fails, as in the row above | not posted if it fails |

- **In no case does the ping go out for images that are not there.** `announce-release` runs only
  when `release-please` reported a release and `publish-images` succeeded. The action writes its
  outputs once `createReleases()` has returned, which creates the release, then comments, then
  changes the labels (`src/manifest.ts`); an error there fails the step with no output written
  (`src/index.ts`). The same step then goes on to the release PR for the version after. Were that
  part to fail, the job would be red with the outputs already written, and the jobs that need it
  would be skipped like those of any red job: again no images and no announcement. That is as it
  was before this change.
- **Putting it right.** Re-running a failed job does not mend a missing permission: a re-run uses
  the run's own commit, and so the same file. In the first row nothing has been created and the
  release PR still carries `autorelease: pending`, so the push that brings the fix should cut the
  release. In the others the tag exists already, and the images would have to come from another
  release or be pushed by hand. The image job fails the same way today for any other cause, such as
  a failing test on the tag.
- **A missing label is not a row**, because no access to make one is being taken away: the job
  never had `issues: write` (section 2). Were one of the two labels deleted, and were GitHub not to
  let the job make it again, the job's step would fail where it labels: on the push that opens the
  release PR, or, for `autorelease: tagged`, at the very end of a release, after the tag, the
  release and the comment exist — and so with no images and no announcement.

## 6. Left for Joshua

- **`issues: write` for `release-please`**, as the action's README lists it. Not given: the task
  asks for the least each job needs, the workflow has never had it, and this repository's first
  release made both labels without it (section 2). What it would buy is cover for one case that
  has not been seen here: a label deleted, and GitHub refusing to let the job make it again.
  A) leave it out, as built; B) add it. **Joshua, 2026-11-16: A.**
- **The comment in `announce-release`** that speaks of "the workflow's write token" (section 3):
  A) leave it; B) reword it in this pull request. No test pins its wording.
  **Joshua, 2026-11-16: A.**
- **The order of merging with #180** (section 4): this change first shows `release-please` writing
  under its own token before any release.
- **Seen in the review and not taken**, each a change of its own:
  - `googleapis/release-please-action@v4` is a tag that moves, and could be pinned to a commit;
  - the tests on the tag and the push to GHCR run in one job on one machine; a test job of its own
    with `contents: read` would keep the token that can push away from the code under test;
  - `ci.yml` has no default of its own at its outermost level, and `publish-main`'s checkout keeps
    its token.

## 7. Execution plan

- [x] 0.1 Pull request #179 merged before anything is started; the branch cut from `origin/master`
  after it, in a worktree of its own. (2026-11-16: not merged at 16:51Z, so nothing was started;
  merged at 16:51:53Z as `4af1305`, confirmed at 16:54Z; branch cut from it.)
- [x] 1.1 Each job's access worked out from the action's README and source, GitHub's reference and
  guide, and the logs of this repository's own runs. (Sections 1 and 2.)
- [x] 2.1 The workflow: the default `{}`, a block for each of the two jobs, `persist-credentials:
  false` on the image job's checkout; the three newer jobs untouched.
- [x] 2.2 Tests: the pins and the helper. (Three tests as first built; six after the review,
  section 3.)
- [x] 2.3 Checks here: the parse and the comparison, the test project, the bends, actionlint, a
  Release build from scratch and the whole suite. (Section 4.)
- [x] 3.1 Two review agents read the local commit, one on security and what the public text says,
  one on correctness and the facts in these records; their findings answered. (Section 8.)
- [x] 4.1 Completion gate. On Joshua's go-ahead: the branch pushed and the pull request opened.
  (2026-11-16: his go-ahead at 22:44Z; pushed at 22:45Z; pull request #181 opened at 22:47:57Z.)
- [x] 4.2 On his go-ahead: the workflow started once by hand from the branch, after the checks of
  section 4; its result goes into the pull request's description. (2026-11-16: run 43973160250,
  green; section 4.)
- [x] 4.3 On his go-ahead: the squash merge. (2026-11-16: his go-ahead at 23:14Z; merged at
  23:15:32Z as `ab32e96`, through the REST call, at the head `1a7f16b` and no other. Section 4 has
  the run the merge started.)
- [ ] 5.1 At the next release: its run read for the two jobs' tokens and for a release that went out
  whole. Until then section 4's "cannot be run" stands.

## 8. Review (2026-11-16)

Two fresh agents read the local commit before anything was pushed, each in a copy of its own and
given the commit and what to check, not my conclusions: one on security and on what the public
text says, one on correctness and on every fact in these records. They read it as it first stood
(`a3617ba`) and then the answers (`5cb8686`). Both commits were local and have been re-made since,
so neither will be found on GitHub.

**What they found right.** No permission either job needs is missing, and neither holds one it
does not use. A job's block replaces the workflow's default in both directions. The three newer
jobs are unchanged. The run ids, versions, quotations and counts in sections 1 to 5 match their
sources, but for the points below. Nothing in the added text gives away what should not be public.

**Round 1: what they found, and the answers.** Nothing critical, high or medium on security. On
correctness two major findings, both in how the tests read the file.

| # | Finding | Answer |
|---|---|---|
| C1, major | A comment indented no deeper than a job's `permissions:` ended the block for the tests and not for YAML, so a permission written after such a comment went unseen. The same for a step's settings. Both helpers came with #179, and the new tests rest on them. | `Block` and `Step` end only at a setting indented no deeper. |
| C2, major | A job whose key line carries a comment or a value was invisible to the tests, which then read it as part of the job above. So a sixth job could hide, and a job could be given `write-all` on one line while a hidden job below it showed the expected block. | `JobNames` takes every key at the first level under `jobs:` and refuses one that does not stand alone on its line; each job has exactly one `permissions` key; the two older jobs' keys are pinned. |
| C3 and S2, minor and low | The image job was pinned for two spellings only: a second checkout written under a name, the token under another spelling or in the job's `env`, a container with credentials, the login moved before the tests, a step added or swapped all passed. So did a step added to `release-please`, its action swapped, or the action given another token. | The image job's keys, its five steps in order and what each is given, and every expression in it are pinned; the release job's keys and its one step are pinned. |
| S1, low | "Making a missing label would take `issues: write`" is contradicted by this repository's own history: the job made both labels at the first release without it. | Section 2, the workflow's comment and the test's comment now say what is known. Section 6 stays at A. |
| C4 and S5, minor | The audit entry of 17:19:18Z says 22 bends and this plan said 25. | Both were true when written: the image job's test was tightened and three bends added after that entry. The entry of the review says so; section 4 has the counts as they are now. |
| C5, minor | "Every run in the table labelled" without `issues`: two of the four did. | Corrected here and in the audit entry of 17:19:01Z. |
| C6 and S4, minor | "The two jobs do their real work only when a release is created" is wrong for `release-please`, which opens and updates the release PR on ordinary pushes. | The commit message and the state file say it as section 4 does. The security reviewer's suggestion, to merge this before #180, is in sections 4 and 6. |
| S3, note | Section 5 had no word on a missing label, stopped one step short of what the action's step does, and was too gloomy about the first row. | The three bullets under the table. |
| S6, note | `persist-credentials: false` is safe, and its benefit is real and small: the token is still within reach of code that runs in the job, and nothing changes for the image builds. | Section 2 says so, and names the larger gain. |
| C7, nits | Twelve places where the wording said more than the evidence: "no access" where a token keeps `Metadata: read`; the token "handed to the login alone" where the checkout takes it by default; "release-please's README" for the action's; "all three" noted by #179's review, where it noted two; `publish-main` "on every push"; the comment "above" the announce job, which is inside it; the count of tests "as before"; the fourth row's "not posted"; and others. | Each reworded, here, in the tests' comments, in the workflow's comments and in the commit message. |

**Round 2: the answers read, and what was found then.** Each agent found its own findings
answered and nothing broken by the answers, and each found more of the same kind.

| # | Finding | Answer |
|---|---|---|
| C8, major by the measure of C2, though only a deliberate hand writes it | The tests look for a job's keys at four spaces, and YAML takes them wherever the job's first key stands. A job written with its real keys at three spaces, and the lines the tests look for parked in a block of text under a `name`, passed every test with `write-all`. The helpers' four spaces came with #179. | Two tests of the file's form: every job has its keys four spaces in with nothing further out; and only a script is a block of text, with any quoted value closed on its line. |
| S7 and C9, low and minor | The image job's `env` was held only through the list of expressions, so a variable without one could be added. It would reach every step, the login among them, and could send the login or the pushes elsewhere. | The block is pinned to its one line. |
| S8, low | The release job's `outputs` were free, and they decide whether the image job runs and which tag it checks out, tests and publishes. | Pinned to their four lines. |
| S9, note | The build step's script and the triggers were held by no test, as round 1's records said. The script runs after the login, so it is the one place where free text is within reach of the token. | Both are pinned now, and the script is in the list of pinned scripts. |
| C10, nits | The sentence on merging after #180 had lost its condition. Two statements in the audit entry of 17:19:01Z still said "on every push" and "used all along". | Reworded; the audit entry of this round names the two. |

The answers to round 2 were made as the two agents had written them out, and were checked by
running every one of their bent files again. They were not read again by an agent.

**Not taken.** The three points at the end of section 6. And one that was the review's to notice
and is another session's to carry: `aidlc-state.md` still says on this branch that #179 is not
merged, in the release summary's own section, which this change leaves alone.

**Run on the final code.** The release-notes project, 361 tests, 344 green and 17 skipped. All 139
bends of section 4, every one failing a test but for the two harmless comments. actionlint,
nothing to report. A Release build of the whole solution from scratch, no errors and the
baseline's 22 warnings, and the whole suite green: the 1,292 earlier tests (721 API, 418 bot, 153
web) with the 361 (18:49Z to 18:50Z).
