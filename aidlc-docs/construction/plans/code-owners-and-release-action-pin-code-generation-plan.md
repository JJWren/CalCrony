# A code owners file, and the release action named by its commit: code generation plan

**Request**: Joshua, 2026-11-17: "Yes, do it" — his answer to "Add CODEOWNERS and pin the release
action in a small PR from here?", put to him when pull request #181 had been merged. Two things,
in one small pull request: a `.github/CODEOWNERS` file that names him for every file, and
`googleapis/release-please-action` named in the release workflow by the commit it runs today
instead of by its `v4` tag. The second was listed as seen and not taken in
`release-job-permissions-code-generation-plan.md`, section 6. His answer was taken as approval of
the change as it was offered; no separate approval of this plan was asked, and the plan and the
code go to him together at the completion gate.
**Depth**: minimal — one unit; his answer is the requirement, and what was verified, the change,
its steps and what GitHub will show are this one document. User stories, application design, units,
NFR and infrastructure stages are skipped: repository settings and release tooling, with nothing in
it a member sees.
**Baseline**: `master` 47c8a15 when the branch was cut (pull requests #180 and #183 merged; latest
release v0.37.0; release pull request #185 open for 0.37.1). `master` then gained #182 and #186, and
`916ecd1` was taken in as `34c64aa`; then #184, and `723696d` was taken in as `c29f180`; then #185
(the release of 0.37.1) and #188, and `e792e1a` was taken in as `bc4faa2`. **Branch**:
`ci/code-owners-and-pinned-release-action` → pull request #187, `ci: add a code owners file and name
the release action by its commit` (`ci:` cuts no release).
**Status**: generated on the local branch on 2026-11-17 and read by two review agents the same day,
in two rounds (section 7); the findings of both rounds are answered. Joshua answered the completion
gate B the same day ("B, continue"), so `master` was taken in again and the branch pushed and opened
as a pull request (step 4.1). On his "Hand run, then merge" the workflow was run once by hand from
the branch (step 4.2) and pull request #187 was squash-merged as `162fd42` (step 4.3). Step 5.1 is
done but for what GitHub lists as required on the next release pull request; on one the owner
opened it was read on 2026-11-18, and `protect-master` gained "Restrict updates" the same day.

## 1. What exists today (verified 2026-11-17)

Read from GitHub with the owner's login, read-only, unless another source is named.

**The ruleset.** `protect-master` (active, on `refs/heads/master`) has these rules: no deletion, no
force push, squash as the only merge method, signed commits, Copilot code review on every push, the
checks `build-test` and `pr-title` (and `lint-workflows`, added to them at 23:13Z the same day, as
#188's records say), and for pull requests one approving review, stale reviews
dismissed on a push, approval of the last push, every review thread resolved, one approval more for
a pull request that Copilot opens and that is credited to no person — and a review by a code owner
(`require_code_owner_review`). Repository admins bypass the ruleset, always; it is the ruleset as a
whole that is bypassed, not one rule of it.

**Code owners.** The repository has no code owners file. GitHub looks for one in `.github/`, then
the root, then `docs/`, and uses the first it finds; none of the three has one, and GitHub's
`codeowners/errors` endpoint answers 404 for `master`. So no file has a code owner. GitHub's
documentation of the rule: with it on, "any pull request that modifies content with a code owner
must be approved by that code owner". With no code owner for any file, the rule asks for nothing.

**Who can be a code owner.** A code owner needs write access. The repository has one collaborator,
its owner, `JJWren`, a user account with the admin role.

**The release action.** The `release-please` job has one step,
`uses: googleapis/release-please-action@v4`. `v4` is an annotated tag that the action's own release
workflow deletes and makes again at each 4.x release; it was last made on 2026-04-13, five seconds
after release v4.4.1 was published. It names commit `5c625bfb5d1ff62eadeeb3772007f7f66fdcf071`,
which is also what the `v4.4.1` tag names directly: the release commit of 2026-03-30, a signed
commit on the action's main branch. Every run of this workflow read so far fetched that commit —
the runner's log says so in "Set up job" — among them the five on `master` from 43977699270
(2026-11-16T23:15Z) to 44133746712 (2026-11-17T17:09Z). The one at 16:36Z, 44127732988, opened
release pull request #185.

**The action's newest release** is v5.0.0 (2026-04-22). It moves the action to Node 24 and
release-please from 17.3.0 to 17.6.0. Not part of this change (section 5).

**How this repository already writes a pinned action.** Since #183 the lint job in `ci.yml` names
`actions/checkout` by a commit with the version after it, under a comment that says which tag the
commit is. This change writes the release action the same way.

**Nothing moves a pin by itself.** There is no Dependabot configuration. A newer version of a
pinned action comes in by hand.

## 2. The change

### `.github/CODEOWNERS`

```
# The protect-master ruleset asks for a code owner's review on a pull request into master that
# changes a file with a code owner. This file gives every file one: the repository's owner.
* @JJWren
```

One rule. `*` matches every file, so every pull request into `master` changes a file with a code
owner. In `.github/`, the first place GitHub looks.

What it changes for pull requests, from GitHub's documentation and the ruleset as read:

- **Pull requests opened by anyone else** — the release pull request, which the release workflow
  opens, or a contributor's: the approval that lets one merge without the bypass is now the
  owner's. Another reviewer's approval still counts as a review; it does not stand in for the
  owner's unless the file names that reviewer too, and they have write access.
- **The owner's own pull requests**: nothing in how they are merged. Nobody can approve their own
  pull request, so these are merged with the admin bypass, which covers the whole ruleset, and the
  merge call is the same one. Whether GitHub also lists a code owner's review as required on a pull
  request the owner opened is not in its documentation; it is read after the merge (section 4).
- **Review requests**: GitHub asks a code owner for a review when someone else opens a pull request
  that changes their files (not for a draft). So the owner will be asked on each new release pull
  request.
- **When it takes effect**: GitHub reads the file from the pull request's base branch. It applies
  to pull requests into `master` once this one is merged, the open ones included, and a pull request
  cannot change who its own code owners are.

A line GitHub cannot read is skipped, and a line that names someone without write access assigns no
owner. Section 4 has the question that tells whether GitHub reads this file as written.

### The release action by its commit

```yaml
    steps:
      # Named by a commit, the one the v4.4.1 tag names, and not by the v4 tag, which is moved
      # from one 4.x release to the next. A newer version goes in the same way: the commit its
      # tag names in the action's own repository, not in a fork, with the version after it.
      - uses: googleapis/release-please-action@5c625bfb5d1ff62eadeeb3772007f7f66fdcf071 # v4.4.1
        id: release
```

The same code the runs fetch today, so nothing about a release changes. What changes is that the
workflow keeps running that code until the line is edited in a pull request. Nothing else in the
workflow file is touched: the other steps and the three newer jobs are as they were.

### Tests

In `tests/CalCrony.ReleaseNotes.Tests`:

- `ReleaseWorkflowTests`: the test that pins the release job's one step now expects the new line,
  commit and version comment included. No test is added there; one pin moves.
- `CodeOwnersTests`, new, three tests. They read the file's bytes as they are, not through the
  helper that normalizes text for the other tests:
  - the rules are exactly `* @JJWren`. A line is a comment only when its first character is a
    number sign and blank only when it is empty; every other line is a rule and is compared whole.
    GitHub goes by the last rule that matches a file, and a rule that names nobody takes its files'
    owner away, so a second rule after it fails whatever it says;
  - the file is a plain file, not a link; at most 16 KiB, far under the 3 MB above which GitHub
    does not load it; and every byte is printable ASCII or part of a line end, a line feed alone or
    a carriage return before one;
  - git gives the file no attribute but `text=auto`, which every file here has from the
    repository's `.gitattributes`. GitHub reads the file as the repository stores it and the tests
    read it as a checkout writes it; an attribute such as `working-tree-encoding`, set in a
    `.gitattributes` file, could make the two differ. The test asks `git check-attr`, so it runs in
    a git checkout: a new attribute, `GitCheckoutFact`, skips it in a copy without one, as an export
    made with `git archive` is, and never in CI.
- `Repository`, the helper that finds the repository's root for every test of the project: in a
  GitHub Actions run, the folder it finds has to be `GITHUB_WORKSPACE`. It finds the first folder
  above the tests that holds the solution file, so a second solution file lower in the tree would
  have every test that reads the repository's files, the workflow's included, read others in their
  place. On a machine that is not CI the variable is not set, and the check is not made. Its summary
  also names the code owners file among what the tests read.
- The pins are strict on purpose, as the workflow's is: a form GitHub likely reads the same — a
  comment after the rule, other spacing, a tab, the owner in lower case, a comment line indented, a
  rule for one folder before the rule for every file, `text` set for the file where the repository
  has `text=auto`, two spaces before the version comment, the reference in quotes — fails too, and
  goes in with the test changed. Free are whole-line comments, blank lines, either line end and the
  final line end.

### `CONTRIBUTING.md`

One note under the ruleset's: the ruleset also asks for a code owner's review, the file names the
repository owner for every file, and so a pull request that someone else opens, the release PR
included, needs the owner's approval to merge without the admin bypass; another reviewer's approval
does not stand in for it unless the file names them too and they have write access. Put after the
ruleset note's blank line, so that a later edit of that note's one long line does not collide with
it. The review step's sentence about a second human gains that their approval counts as a code
owner's only if the file names them.

## 3. What was run here

On the tree with `master` (`916ecd1`) taken in and both rounds of the review answered:

- The release-notes project: 365 tests, 348 green and 17 skipped — the 17 that are skipped on
  Windows on `master` too; CI runs them all. Three more than `master`'s 362: the three new tests.
- Fifty-six changes to a copy of the tree that is a git repository of its own, one at a time, with
  the two test classes run on each and `GITHUB_WORKSPACE` set to the copy, as a GitHub Actions run
  has it:
  - Thirty-eight change, or may change, what GitHub does, and each fails a test. Eight are to the
    workflow line: the action by its `v4` tag again, by `v5`, by a short commit, by another commit,
    by the commit in upper case, without the version after it, with another version after it, and
    another action at the same commit. Twenty-three are to the code owners file: the rule taken out,
    turned into a comment, narrowed to one folder, indented, given another owner or a second one; a
    second rule after it, with or without an owner; the file gone; a no-break space, U+2028,
    U+3000, NEL, a form feed or a zero-width space after the owner; a lone carriage return before
    the rule; a byte-order mark, with and without the rule on the first line; the file in UTF-16; a
    no-break space before a comment's number sign; 3.2 MB of comment lines; and the file made a
    link to another file that holds the rule. Six give the file an attribute in a `.gitattributes`
    file: `working-tree-encoding` for it, in the repository's file, in one of `.github/`'s own and
    behind a macro, the same for every file, a filter and `ident`. One puts a second solution file
    under `tests/`, with copies of what the tests read beside it, and changes the real files.
  - Ten are harmless and refused by the strict pins, and each fails a test, as section 2 says.
  - Seven are harmless and pass: each file's comment reworded, a blank line and a comment after the
    rule, the comment lines removed, carriage returns before every line feed, no final line end,
    and a second code owners file in `docs/`, which GitHub does not read while `.github/` has one.
  - One shows what the root check leaves open: the second solution file without `GITHUB_WORKSPACE`
    set, as on a machine that is not CI, and the tests pass.
  - The link could not be made on Windows without a privilege. It was made in a Linux container of
    the .NET SDK, without network and with `CI` and `GITHUB_WORKSPACE` set as on GitHub, where the
    plain-file test failed on it and the other two passed. In the same container the three tests
    passed on the tree as it is, and three repositories were built whose stored rule line differs
    from what their checkout holds, through `working-tree-encoding`: in each the earlier tests
    passed, and the new git test failed.
- actionlint 1.7.12, from an image already on this machine and run without network: nothing to
  report on the three workflow files.
- A Release build of the whole solution from scratch: no errors and the baseline's 22 warnings.
- The whole suite, 18:24Z to 18:25Z: 1,763 tests, 1,746 green and the 17 skipped (827 API, 418
  bot, 153 web, 365 release notes).
- Again on the merged tree `c29f180`, after `master` (`723696d`, #184) was taken in, 23:09Z to
  23:14Z: a Release build with no errors and the baseline's 22 warnings, then the whole suite,
  1,801 tests, 1,784 green and the same 17 skipped (865 API, 418 bot, 153 web, 365 release notes);
  #184 accounts for the 38 more in the API project.

## 4. What only GitHub can show, and when

- **That GitHub reads the code owners file as written.** After the push,
  `GET /repos/JJWren/CalCrony/codeowners/errors?ref=<the branch>` lists what GitHub cannot read in
  it; an empty list is the answer wanted. Today the same question about `master` answers 404, there
  being no file.
- **The pull request's own checks.** `lint-workflows` reads the changed workflow file on GitHub's
  runner, and `build-test` runs the 17 tests that Windows skips, the new git test with
  `GITHUB_WORKSPACE` and `CI` set as they are there.
- **That the runner fetches the action by its commit.** No check on a pull request runs the release
  workflow. Two ways to see it: the workflow started once by hand from the branch, as for #179 and
  #181, or the run that this pull request's own merge starts. A run by hand works on `master`,
  whatever branch it is started from. It would find release pull request #185 open, and leaves it
  as it is when the text it would write is the text the pull request has: release-please compares
  the two (`maybeUpdateExistingPullRequest` in its `manifest.ts`, read at 17.3.0). That holds while
  nothing user-facing has reached `master` since the pull request was last written and the day is
  the same, the date being part of the text; otherwise the run brings it up to date, as the next
  push to `master` would. It makes a release only when a merged release pull request is waiting for
  one, so it is not to be started between a merge of #185 and the run that merge starts. If the
  runner could not fetch the commit, the job would fail in "Set up job", before the action runs and
  before anything is written; the release pull request would then not be kept up to date, and no
  release made, until the line is put right.
- **That a code owner's review is asked for.** After the merge, on the pull requests that are open
  then: on a release pull request, GitHub lists a review by a code owner among what is required. On
  a pull request the owner opened, what GitHub lists is read as well, since its documentation does
  not say. #184 and #185 were both merged before this change, so each is read on the next one.
- **A release under the pinned action.** The first release after this change is merged. #185 was
  merged first, at 23:27:30Z on 2026-11-17, so 0.37.1 was released with the action still named by
  its `v4` tag; its run (44199537457) fetched the same commit, `5c625bf…`, for it. That release was
  the first full run of #181's change, whose plan has that read as its step 5.1.

## 5. Not taken

Each is a change or a decision of its own.

- **v5.0.0 of the action.** It would replace the code the release runs with newer code — Node 24,
  release-please 17.6.0 — which is the opposite of what this change is for. It goes in the way the
  workflow's comment says, as its commit, after a look at what changed between the two versions.
  There is a reason to take it up before long: each run carries GitHub's notice that the action
  targets Node 20, which is deprecated, and is being run on Node 24.
- **The other actions named by tags**, in this workflow and in `ci.yml` and `codeql.yml`.
  `docker/login-action` would come first.
- **Dependabot for actions.** Its version updates would open a pull request with the new commit and
  version when a pinned action has a new release. Its alerts are another matter: GitHub's reference
  on using actions securely says Dependabot "will not create alerts for actions pinned to SHA
  values", so with this change no alert would come for the release action, and version updates are
  how a fix would reach it.
- **The repository setting that refuses an action not named by a full commit.** It is off, and it is
  Joshua's to change; every action would have to be pinned first.

## 6. Execution plan

- [x] 0.1 Read first: `master` and what has reached it since #181; the open pull requests, none of
      which touches `.github/`, the tests that pin the release workflow or `CONTRIBUTING.md` (#182,
      #184 and #186 add to the audit log and the state file; #185 changes `CHANGELOG.md` only); the
      branch cut from `origin/master` at `47c8a15`.
- [x] 1.1 Verified, read-only: the ruleset and its bypass, that there is no code owners file, who
      has write access, what the action's `v4` and `v4.4.1` tags name, and what the runs fetched.
- [x] 2.1 `.github/CODEOWNERS`.
- [x] 2.2 The workflow: the release action by its commit, with the version after it.
- [x] 2.3 Tests: the moved pin, and the new tests for the code owners file.
- [x] 2.4 `CONTRIBUTING.md`: the note on code owners.
- [x] 2.5 Checks here: the test project, the bends, actionlint, a build from scratch and the whole
      suite.
- [x] 3.1 Two review agents read the local commit, one on security and what the public text says,
      one on correctness and on every fact in these records (section 7).
- [x] 3.2 `master` (`916ecd1`) taken in as `34c64aa`, the audit entries of both sides in the order
      of their timestamps; the findings answered; the checks of 2.5 run again.
- [x] 3.3 Both agents read the answers: each found its findings answered, and each found a little
      more (section 7, second round).
- [x] 3.4 The second round answered, and the checks of 2.5 run again. These answers were checked by
      running the agents' own cases again, and were not read again by an agent.
- [x] 4.1 Completion gate. On Joshua's go-ahead: `master` taken in if it has moved, the branch
      pushed, the pull request opened, and GitHub asked whether it reads the code owners file.
      Answered B on 2026-11-17 ("B, continue"): `master` `723696d` (#184) taken in as `c29f180`,
      and the whole suite run again on it (section 3). GitHub's answer on the code owners file is
      in the pull request's description.
- [x] 4.2 On his go-ahead: the workflow started once by hand from the branch, its result in the
      pull request's description. Approved 2026-11-17 ("Hand run, then merge"): run 44199345170,
      green; the action fetched by its commit, the job's token as #181 set it, #185 "remained the
      same", no release made.
- [x] 4.3 On his go-ahead: the squash merge. Approved 2026-11-17 ("Hand run, then merge"). #185 and
      #188 were merged first, so `master` was taken in a third time; then, every check on the head
      `09d6cd1` green, the REST call with that head named merged it at 23:38:12Z as `162fd42`.
- [ ] 5.1 After the merge: the run the merge starts read for the action's commit; the code owners
      question asked again about `master`; what GitHub lists as required on the release pull request
      and on one the owner opened. Read on 2026-11-17: the merge's run (44201104260) fetched the
      action by `5c625bf…` from `master`, found no user-facing commit and wrote nothing; GitHub's
      `codeowners/errors` for `master` is empty. No pull request was open after the merge, so the
      last part waits for the next release pull request and the next one the owner opens. Read on
      2026-11-18, by Joshua, signed in: the merge box of #189, which he opened, does not list
      "Code owner review required". The same day `protect-master` gained "Restrict updates", so
      only those who may bypass the ruleset, the admins, can update `master` (`CONTRIBUTING.md`).
      The release pull request's part waits for the next one.

## 7. Review (2026-11-17)

Two agents read the local commit `c42c173`, each in a copy of its own and given the commit and what
to check, not my conclusions: one on security and what the public text says, one on correctness
and on every fact in these records. Neither found anything critical, high or major in the change.
Both then read the merge and the answers, found their findings answered, and found a little more:
the second round, below the first.

**First round**

| Finding | Answer |
|---|---|
| Both: `CodeOwnersTests` read the file more leniently than GitHub. It trimmed each line and went through the helper that drops a byte-order mark and reads a lone carriage return as a line end, so a no-break space or U+2028 after the owner, a lone carriage return before the rule, a byte-order mark, the file in UTF-16, an indented rule and a file over GitHub's 3 MB limit all passed. | The tests read the bytes as they are and compare each line whole, and a second test holds the file to printable ASCII and plain line ends, at most 16 KiB, and not a link. Each of those cases now fails a test (section 3). |
| Both: `master` had moved (#182, #186), and its newest audit entry was later than this branch's two. | `master` taken in as `34c64aa`: its text stands as it is, and this branch's two entries are put in where their timestamps belong; a script checked that taking them out again gives `master`'s file back. |
| Both: the `CONTRIBUTING.md` note said the owner's approval is what lets any pull request merge without the bypass. That is documented for a pull request someone else opens; for one the owner opens the documentation does not say. And the review step's sentence about a second human needed a word on code owners. | The note says "a pull request that someone else opens", and adds that a reviewer named in the file needs write access; the review step says a second human's approval counts as a code owner's only if the file names them. What GitHub lists on a pull request the owner opened is read after the merge (sections 4 and 6). |
| Both: section 1 dated the `v4` tag two weeks after the release it names. Release v4.4.1 was published on 2026-04-13, the tag made five seconds later; 2026-03-30 is the release commit's date. | Section 1 corrected. That the tag moves stands: the action's release workflow deletes and makes `v<major>` again at each release. |
| Correctness: step 0.1 said none of the open pull requests touches the same files; three of them add to the audit log and the state file. | Step 0.1 reworded; the audit entry's narrower wording was true and stays. |
| Correctness: the first audit entry called run 44127732988 the latest on `master`; a later run had already finished. | Reworded in place; the three later runs fetched the same commit, and section 1 names them. |
| Correctness: section 1's list of the ruleset's rules left one out. | Added. |
| Correctness: section 4 called 0.37.1 the next release under the pinned action, which holds only if this merges first. | Reworded. |
| Correctness: harmless forms fail the strict pins, so "comments and blank lines aside" covers whole-line comments only. | Kept strict, and said so in section 2. |
| Correctness: the first audit entry was written after the construction it calls next, and no separate approval of the plan is recorded. | Its context reworded in place; the request at the top of this plan says how his answer was taken. |
| Security: section 1 put the job's access next to the moving tag; better left out. | Cut. |
| Security: pinned by commit, the action gets no Dependabot alert, and GitHub's guidance says to check that a commit comes from the action's own repository and not a fork. | The workflow's comment says so for the next version; section 5 says what Dependabot does and does not do. |
| Security, outside this change: other actions are still named by tags, `docker/login-action` among them. | Listed in section 5 as the next to pin; not taken here, as the change was approved as it stands. |

**Second round**

| Finding | Answer |
|---|---|
| Both: the tests could still be made to read something other than what GitHub reads, without a byte of the code owners file changing: through an attribute for the file in a `.gitattributes` file, and — older than this change, and reaching the workflow's tests too — through a second solution file lower in the tree, which the helper that finds the repository's root took for it. | A third test asks git for the file's attributes and allows only `text=auto`; in a GitHub Actions run the root has to be `GITHUB_WORKSPACE`. Both agents' cases fail a test now (section 3). The root check is not made where the variable is not set. |
| Security: the commit message and the class summary said the tests and GitHub could not read the file differently, which the finding above contradicted; and the plan called a reading of GitHub's that its documentation does not give a fact. | The commit is re-made with a message that says what the tests check, the summary says the same, and the plan says "likely". |
| Security: section 5 still set `docker/login-action` next to what it logs in to. | Cut to the name. |
| Correctness: a rule for one folder before the rule for every file was counted among the changes that matter; GitHub goes by the last rule that matches, so it changes nothing. | Counted among the harmless forms the pins refuse. |
| Correctness: step 0.1's new wording said all the open pull requests add to the audit log, but #185 changes `CHANGELOG.md` only; and section 2 left either line end off the list of what is free. | Both reworded. |
