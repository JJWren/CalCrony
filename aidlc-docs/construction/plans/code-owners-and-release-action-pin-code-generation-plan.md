# A code owners file, and the release action named by its commit: code generation plan

**Request**: Joshua, 2026-11-17: "Yes, do it" — his answer to "Add CODEOWNERS and pin the release
action in a small PR from here?", put to him when pull request #181 had been merged. Two things,
in one small pull request: a `.github/CODEOWNERS` file that names him for every file, and
`googleapis/release-please-action` named in the release workflow by the commit it runs today
instead of by its `v4` tag. The second was listed as seen and not taken in
`release-job-permissions-code-generation-plan.md`, section 6.
**Depth**: minimal — one unit; his answer is the requirement, and what was verified, the change,
its steps and what GitHub will show are this one document. User stories, application design, units,
NFR and infrastructure stages are skipped: repository settings and release tooling, with nothing in
it a member sees.
**Baseline**: `master` 47c8a15 (pull requests #180 and #183 merged; latest release v0.37.0; release
pull request #185 open for 0.37.1). **Branch**: `ci/code-owners-and-pinned-release-action` → PR
`ci: add a code owners file and name the release action by its commit` (`ci:` cuts no release).
**Status**: generated on the local branch on 2026-11-17. Nothing is pushed. A review and the
completion gate follow; the push, the pull request, a run by hand and the merge each wait for
Joshua's go-ahead.

## 1. What exists today (verified 2026-11-17)

Read from GitHub with the owner's login, read-only, unless another source is named.

**The ruleset.** `protect-master` (active, on `refs/heads/master`) has these rules: no deletion, no
force push, squash as the only merge method, signed commits, Copilot code review on every push, the
checks `build-test` and `pr-title`, and for pull requests one approving review, stale reviews
dismissed on a push, approval of the last push, every review thread resolved — and a review by a
code owner (`require_code_owner_review`). Repository admins bypass the ruleset, always; it is the
ruleset as a whole that is bypassed, not one rule of it.

**Code owners.** The repository has no code owners file. GitHub looks for one in `.github/`, then
the root, then `docs/`, and uses the first it finds; none of the three has one, and GitHub's
`codeowners/errors` endpoint answers 404 for `master`. So no file has a code owner. GitHub's
documentation of the rule: with it on, "any pull request that modifies content with a code owner
must be approved by that code owner". With no code owner for any file, the rule asks for nothing.

**Who can be a code owner.** A code owner needs write access. The repository has one collaborator,
its owner, `JJWren`, a user account with the admin role.

**The release action.** The `release-please` job has one step,
`uses: googleapis/release-please-action@v4`, and the job's token may write the repository's
contents and its pull requests (#181). `v4` is a tag in the action's repository, and a tag there can
be moved: `v4` is an annotated tag made on 2026-04-13, two weeks after the release it now names. It
names commit `5c625bfb5d1ff62eadeeb3772007f7f66fdcf071`, which is also what the `v4.4.1` tag names
directly: the release of 2026-03-30, a signed commit on the action's main branch. Every run of this
workflow read so far fetched that commit — the runner's log says so in "Set up job" — the latest
being run 44127732988 on `master` (2026-11-17T16:36Z), which opened release pull request #185.

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

- **Joshua's own pull requests**: nothing. Nobody can approve their own pull request, so these are
  merged with the admin bypass today, and the bypass covers the whole ruleset. The merge call is the
  same one.
- **Pull requests opened by anyone else** — the release pull request, which the release workflow
  opens, or a contributor's: the approval that lets one merge without the bypass is now the
  owner's. Another reviewer's approval still counts as a review; it does not stand in for the
  owner's unless the file names that reviewer too.
- **Review requests**: GitHub asks a code owner for a review when someone else opens a pull request
  that changes their files (not for a draft). So the owner will be asked on each new release pull
  request.
- **When it takes effect**: GitHub reads the file from the pull request's base branch. It applies
  to pull requests into `master` once this one is merged, the open ones included, and a pull request
  cannot change who its own code owners are.

A line GitHub cannot read is skipped without an error on the pull request, and a line that names
someone without write access assigns no owner. Section 4 has the question that tells whether GitHub
reads this file as written.

### The release action by its commit

```yaml
    steps:
      # Named by a commit, the one the v4.4.1 tag names, and not by the v4 tag, which is moved
      # from one 4.x release to the next. A newer version goes in the same way: its commit, with
      # the version after it.
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
- `CodeOwnersTests`, new, one test: the file's rules, comments and blank lines aside, are exactly
  `* @JJWren`. GitHub goes by the last rule that matches a file, and a rule that names nobody takes
  its files' owner away, so a second rule is refused whatever it says. No check on a pull request
  reads the file otherwise.
- `Repository`'s summary names the code owners file among what the tests read.

### `CONTRIBUTING.md`

One note under the ruleset's: the ruleset also asks for a code owner's review, the file names the
repository owner for every file, and so the approval that lets a pull request merge without the
admin bypass is the owner's. Put after the ruleset note's blank line, so that a later edit of that
note's one long line does not collide with it.

## 3. What was run here

- The release-notes project: 363 tests, 346 green and 17 skipped — the 17 that are skipped on
  Windows on `master` too; CI runs them all. One more than `master`'s 362: the new test.
- Twenty changes to a copy of the tree, one at a time, with the two test classes run on each.
  Sixteen bend the files and each fails a test: the action by its `v4` tag again, by `v5`, by a short
  commit, by another commit, without the version after it, with another version after it, another
  action at the same commit; the rule taken out, turned into a comment, narrowed to one folder,
  given another owner or a second one, a second rule with and without an owner, a rule that names
  nobody after it, and the file gone. Four are harmless and pass, as they should: each file's comment
  reworded, spaces after the rule, a comment line added after it.
- actionlint 1.7.12, from an image already on this machine and run without network: nothing to
  report on the three workflow files.
- A Release build of the whole solution from scratch: no errors and the baseline's 22 warnings.
- The whole suite, 16:58Z to 17:01Z: 1,747 tests, 1,730 green and the 17 skipped (813 API, 418 bot,
  153 web, 363 release notes).

## 4. What only GitHub can show, and when

- **That GitHub reads the code owners file as written.** After the push,
  `GET /repos/JJWren/CalCrony/codeowners/errors?ref=<the branch>` lists what GitHub cannot read in
  it; an empty list is the answer wanted. Today the same question about `master` answers 404, there
  being no file.
- **The pull request's own checks.** `lint-workflows` reads the changed workflow file on GitHub's
  runner, and `build-test` runs the 17 tests that Windows skips.
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
  runner could not fetch the commit,
  the job would fail in "Set up job", before the action runs and before anything is written; the
  release pull request would then not be kept up to date, and no release made, until the line is
  put right.
- **That a code owner's review is asked for.** After the merge, on the pull requests that are open:
  GitHub names a review by a code owner among what is required.
- **A release under the pinned action.** The next release — 0.37.1, when #185 is merged — is the
  first. It is also the first full run of #181's change, whose plan has that read as its step 5.1.

## 5. Not taken

Each is a change or a decision of its own.

- **v5.0.0 of the action.** It would replace the code the release runs with newer code — Node 24,
  release-please 17.6.0 — which is the opposite of what this change is for. It goes in the way the
  workflow's comment says, as its commit, after a look at what changed between the two versions.
  There is a reason to take it up before long: each run carries GitHub's notice that the action
  targets Node 20, which is deprecated, and is being run on Node 24.
- **The other actions named by tags**: `actions/checkout`, `actions/setup-dotnet` and
  `docker/login-action` in this workflow, and those of `ci.yml` and `codeql.yml`.
- **Dependabot for actions**, which would open a pull request when a pinned action has a newer
  release, with the new commit and the version comment.
- **The repository setting that refuses an action not named by a full commit.** It is off, and it is
  Joshua's to change; every action would have to be pinned first.

## 6. Execution plan

- [x] 0.1 Read first: `master` and what has reached it since #181; the open pull requests, none of
      which touches the same files; the branch cut from `origin/master` at `47c8a15`.
- [x] 1.1 Verified, read-only: the ruleset and its bypass, that there is no code owners file, who
      has write access, what the action's `v4` and `v4.4.1` tags name, and what the runs fetched.
- [x] 2.1 `.github/CODEOWNERS`.
- [x] 2.2 The workflow: the release action by its commit, with the version after it.
- [x] 2.3 Tests: the moved pin, and the new test for the code owners file.
- [x] 2.4 `CONTRIBUTING.md`: the note on code owners.
- [x] 2.5 Checks here: the test project, the bends, actionlint, a build from scratch and the whole
      suite.
- [ ] 3.1 Review agents read the local commit, one on security and what the public text says, one
      on correctness and on every fact in these records.
- [ ] 4.1 Completion gate. On Joshua's go-ahead: `master` taken in if it has moved, the branch
      pushed, the pull request opened, and GitHub asked whether it reads the code owners file.
- [ ] 4.2 On his go-ahead: the workflow started once by hand from the branch, its result in the
      pull request's description.
- [ ] 4.3 On his go-ahead: the squash merge.
- [ ] 5.1 After the merge: the run the merge starts read for the action's commit; the code owners
      question asked again about `master`; one open pull request looked at for the review it now
      needs.
