# Workflow linter — actionlint and shellcheck on the workflow files, in CI: code generation plan

**Request** (handed over on 2026-11-16 by the session that built pull request #179, as the card it
proposed; not typed by Joshua): add a linter for GitHub Actions workflow files to CI, as a pull
request against `master` — actionlint, with shellcheck for the `run:` scripts, over every file in
`.github/workflows/`, on pull requests and on pushes to `master`; what it downloads pinned to an
exact version and verified; `contents: read` and nothing else; passing on the files as they are; a
note in `CONTRIBUTING.md`; the ruleset left alone, and whether the check becomes a required one put
to Joshua with a recommendation. The task's own words are in `audit.md` under 2026-11-16.
**Why**: section 16 of `release-summary-code-generation-plan.md`. The tests that pin the release
workflow read it as text. They are not a YAML reader, they cannot say whether a shell script is
well formed, and no check on a pull request executes that workflow. A linter is the other half.
**Depth**: minimal — one unit; the requirements, the one question and the plan are this one
document, as on the release summary and the docs readability pass. User stories, application
design, units, NFR and infrastructure stages are skipped: CI tooling, one stakeholder, nothing a
member of a server sees. **Baseline**: `master` 4af1305 (pull request #179 merged). **Branch**:
`ci/lint-workflows`, in a worktree of its own → PR `ci: lint the workflow files with actionlint and
shellcheck` (`ci:` cuts no release).
**Status**: approved by Joshua on 2026-11-16, as written ("Approve as recommended"): Q1 answered A,
and D1–D9 stand.

## 1. What exists today (verified 2026-11-16, on `master` 4af1305)

| File | Jobs | Runs |
|---|---|---|
| `ci.yml` | `build-test`, `pr-title`, `publish-main` | `build-test` on a pull request into `master` and on a push to `master`; `pr-title` on the pull request only; `publish-main` on the push only (corrected in review: this row had `pr-title` on the push as well) |
| `codeql.yml` | `analyze`, once per language | on a pull request, on a push to `master`, weekly |
| `release-please.yml` | `release-please`, `publish-images`, `release-summary`, `announce-release`, `release-summary-preview` | on a push to `master` and by hand |

- **What a pull request never runs**: all of `release-please.yml`, and `publish-main` with its build
  script. A mistake there shows after the merge — in the release roll, in the `:main` images, or at
  the next release in its images and its announcement.
- **What reads the files before a merge today**: the tests in `tests/CalCrony.ReleaseNotes.Tests`
  read `release-please.yml` as text and pin three of its jobs (since pull request #181, the two
  older jobs as well, so all five: section 10). No test reads `ci.yml` or `codeql.yml`. Nothing checks a shell script unless a test runs it, which three steps' scripts are.
- **The ruleset** `protect-master` requires two checks by name, `build-test` and `pr-title`.
  Repository admins bypass it, which is how pull requests are merged here.
- **How actions are named**: the workflows name their actions by major tag
  (`actions/checkout@v4`), none by commit. There is no Dependabot configuration and no actionlint
  configuration. (Reworded in review: this point also listed three settings of the repository
  that the files do not show.)
- **Line ends**: `.gitattributes` has `* text=auto`, so a Windows checkout holds the workflow files
  with CRLF and the repository holds them with LF.

## 2. The two tools, and what was run here (2026-11-16)

| Tool | Version | Released | Archive the job downloads | Bytes | SHA-256 |
|---|---|---|---|---|---|
| [actionlint](https://github.com/rhysd/actionlint) | 1.7.12, the latest | 2026-03-30 | `actionlint_1.7.12_linux_amd64.tar.gz` | 2,353,908 | `8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8` |
| [shellcheck](https://github.com/koalaman/shellcheck) | 0.11.0, the latest | 2025-08-04 | `shellcheck-v0.11.0.linux.x86_64.tar.xz` | 2,559,196 | `8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198` |

These are the versions the review of pull request #179 ran by hand. Each checksum was read three
ways, and they agree: computed from the downloaded file, listed by GitHub as the asset's digest
(the release page shows it), and, for actionlint, in the release's own
`actionlint_1.7.12_checksums.txt`. shellcheck publishes no checksum file.

**`master` as it is passes.** All three files, with nothing ignored: with the Windows binaries on
this checkout (CRLF), and with the Windows and the Linux binaries on the files as the repository
holds them (LF).

**The job as drafted passes its own check and runs.** On a scratch copy of the workflows with the
job added, the linter finds nothing, in the job's two scripts either. The two scripts were then
taken out of the YAML as GitHub would hand them to bash and run on Linux the way the runner runs a
step (`bash -e`, from the repository root): both archives downloaded, both checksums matched, the
two binaries unpacked, both printed their versions, and the lint of the scratch copy ended 0. The
Linux was this machine's WSL Ubuntu (24.04: bash 5.2.21, curl 8.5.0, `sha256sum` of GNU coreutils
9.4, GNU tar 1.35, xz 5.4.5), not GitHub's runner, which is on Ubuntu 26.04: the pull request's own
run is the first there. (`sha256sum` added to this list in review; section 10 says why it is the
one that matters.)

**A wrong pin stops the job before anything is unpacked.** Five runs of the download step, each
with one thing changed: a checksum with one digit changed, an empty checksum, the two checksums
swapped, a version that was never released, and the version set back to 1.7.11 with the checksum
left. Each ended non-zero (the unreleased version at the download, the others at the checksum),
and none left an unpacked binary behind.

**What the linter catches.** Thirteen scratch copies, each with one deliberate mistake, linted with
the Linux binaries the job pins. Every one fails, and the unchanged copy passes. The last column
is the release-notes tests run here on the same copy (338 pass and 17 are skipped on the unchanged
file, on Windows); they read `release-please.yml` and no other workflow file.

| # | The mistake | In | What the linter says (one finding, shortened; for rows 5 and 6 the second, the first being a note that points at the function) | Linter ends | Today's tests |
|---|---|---|---|---|---|
| 1 | `=` where `==` is meant, in `announce-release`'s `if:` | `release-please.yml` | `203:116: got unexpected character ' ' while lexing == operator, expecting '=' [expression]` | 1 | 1 fails |
| 2 | a step id misspelt in an `if:` (`steps.mention` for `steps.mentions`) | `release-please.yml` | `178:59: property "mention" is not defined in object type {checkout: …; mentions: …} [expression]` | 1 | 1 fails |
| 3 | the three inputs under a `with:` moved out two spaces — still valid YAML | `ci.yml` | `50:14: "with" section should not be empty`, then `51:9: unexpected key "registry" for step to execute action` and two more `[syntax-check]` | 1 | file not read |
| 4 | a script line moved out of its `run:` block — no longer valid YAML | `release-please.yml` | `124:8: could not parse as YAML: could not find expected ':' [syntax-check]` | 1 | 2 fail |
| 5 | the closing brace of `build_and_push()` removed, in `publish-main` | `ci.yml` | `56:9: shellcheck reported issue in this script: SC1056:error:13:1: Expected a '}'` and three more `[shellcheck]` | 1 | file not read |
| 6 | the same brace removed in `publish-images`, the job that builds a release's images | `release-please.yml` | `59:9: shellcheck reported issue in this script: SC1056:error:12:1: Expected a '}'` and three more `[shellcheck]` | 1 | **all pass** |
| 7 | an `if` without its `fi`, in the step that asks GitHub about mentions | `release-please.yml` | `152:9: shellcheck reported issue in this script: SC1046:error:10:1: Couldn't find 'fi' for this 'if'` and three more `[shellcheck]` | 1 | 1 fails |
| 8 | `"$WEBHOOK_URL"` left unquoted in the announce step | `release-please.yml` | `218:9: shellcheck reported issue in this script: SC2086:info:1:9: Double quote to prevent globbing and word splitting [shellcheck]` | 1 | 2 fail |
| 9 | `needs:` naming a job that does not exist | `release-please.yml` | `240:3: job "release-summary-preview" needs job "release-pleese" which does not exist in this workflow [job-needs]` and one more | 1 | 1 fails |
| 10 | an input the action does not have (`dotnet-verison`) | `ci.yml` | `18:11: input "dotnet-verison" is not defined in action "actions/setup-dotnet@v4" [action]` | 1 | file not read |
| 11 | a permission scope that does not exist (`package: write`) | `ci.yml` | `44:7: unknown permission scope "package" [permissions]` | 1 | file not read |
| 12 | a runner label that does not exist (`ubuntu-lastest`) | `codeql.yml` | `20:14: label "ubuntu-lastest" is unknown [runner-label]` | 1 | file not read |
| 13 | a schedule on a day of the week that does not exist (`8`) | `codeql.yml` | `12:13: invalid CRON format "17 6 * * 8" in schedule event [events]` | 1 | file not read |
| — | nothing changed | | no output | 0 | all pass |

Rows 1, 3 and 4, and 5 to 7 are the three kinds the task names: an invalid expression, a
mis-indented block, a shell error. Rows 5 and 6 are the case this job is for. `publish-main` is
skipped on a pull request and `publish-images` exists only when a release is cut, so bash would
first read either script after the merge — row 6 on the tag of a release, whose images would then
not be built. And row 6 is the one the tests pass: that script is pinned by no test. The six other
mistakes made in `release-please.yml` each fail one or two tests, as a pin that no longer matches,
which the author of an intended change brings up to date; whether the new text is valid is what
the linter says. The same fourteen copies give the same findings and the same endings with the
Windows binaries, with LF and with CRLF line ends.

**This table is `master` at `4af1305`.** Pull request #181 has since changed `release-please.yml`
and the tests that read it: row 6 no longer passes them, and the positions in that file have
moved. Section 10 has the table as it is on `master` now.

**What the linter does not catch, and one thing to know about it.**

- It reads; it runs nothing. What a script does, what an expression is worth at run time, and what
  an action does with its inputs are outside it.
- **Without shellcheck it leaves the shell checks out and says nothing.** Rows 5 to 8 pass, ending
  0 with no output, when shellcheck's path points at nothing or no shellcheck is on the `PATH`.
  Only `-verbose` says `Rule "shellcheck" was disabled`. The job answers this in D4.
- **Its lists are as old as its release.** actionlint 1.7.12 does not know the label
  `ubuntu-26.04`: a copy of `codeql.yml` naming it is refused as an unknown label, and passes once
  the label is declared in `.github/actionlint.yaml`. The workflows name `ubuntu-latest`, so
  nothing is refused today. (Not so in the job as it stands, which takes nothing from that file:
  section 11 has what does let such a label through.)
- It is not a security review of the workflows: it does not ask for pinned actions or narrower
  permissions.

**The branch `ci/codeql-action-v4`**, which another session holds, changes `codeql.yml` to
`github/codeql-action` v4. Its file, read from the branch without touching it, passes the linter.

**Fetched for this**: seven files from the two projects' release pages on GitHub, into this
session's scratch folder: the two Linux archives above, the two Windows ones, actionlint's checksum
file, and the part of each release page that lists its files, to see that it shows the digests.
(Corrected in review: this said five, and left the two page parts out.) Nothing was installed on the machine, and the temporary folders used inside WSL were
removed. Every request to GitHub was a read.

## 3. The question

Answer with the letter; the recommendation is marked.

**Q1 — Does `lint-workflows` become a required check?**

- A) Yes, once this pull request is merged and the job has run green on `master`. Joshua adds
  `lint-workflows` to the required checks of `protect-master` himself (Settings → Rules →
  Rulesets → `protect-master` → the required status checks). `CONTRIBUTING.md` names it beside
  `build-test` and `pr-title` in this pull request. **(Recommended — the job exists for files no
  other check on a pull request reads, and a red check that blocks nothing is the easiest to merge
  past. It can be required safely: it runs on every pull request, with no path filter, so it never
  leaves a pull request waiting for a check that does not come; it takes seconds; and with both
  tools pinned it cannot turn red by itself, only on a finding or on a download that failed, which
  a re-run clears. The cost: while GitHub cannot serve a release download, a pull request of
  someone without the bypass waits.)**
- B) No. It runs on every pull request and shows red when it finds something, and blocks nothing.
  `CONTRIBUTING.md` says so.
- C) Not yet. As B for now; decided again after it has run for a while — after the next release,
  say.
- D) Other (describe).

[Answer]: A — the recommended option; Joshua, 2026-11-16.

The ruleset is not changed by this work under any answer.

(After review, section 10: two things option A says are not so. "It never leaves a pull request
waiting" holds for pull requests opened or pushed after the merge, not for one already open, which
has no such check until its next push. "It cannot turn red by itself" leaves out the runner image,
which is not pinned. And `CONTRIBUTING.md` does not name the check as required in this pull
request after all, but once the ruleset does.)

## 4. Decisions this plan takes (accepted unless Joshua says otherwise)

| # | Decision | Why |
|---|---|---|
| D1 | The job is a fourth job in `ci.yml`, `lint-workflows`, not a workflow of its own. It runs on every pull request and every push to `master`, with no path filter, and no other job waits for it. | `ci.yml` already has exactly the triggers asked for. A job that always runs can be made required (Q1). `publish-main` goes on needing `build-test` alone: a finding should not hold the `:main` images back. |
| D2 | Both tools are downloaded as release binaries from their projects on GitHub, each held to a version and to the SHA-256 of its archive, checked before anything is unpacked. actionlint 1.7.12 and shellcheck 0.11.0. | What runs is named in the workflow file and can be checked against the release page by anyone. Not taken: actionlint's Docker image by digest (one opaque digest, shellcheck's version only implied, Docker Hub added to a check that may be required), and a wrapper action (it would download the linter itself at run time, unchecked). Not taken either: the runner's own shellcheck, whose version moves with the runner image. |
| D3 | `actions/checkout` is pinned to a commit, `3d3c42e5aac5ba805825da76410c181273ba90b1`, the tag `v7.0.1` (the current release; it declares Node.js 24), with `persist-credentials: false`. The other jobs stay on `@v4`. | The task asks for a commit where an action is used. v7.0.1 rather than the commit behind `@v4` (v4.4.0, `11d5960a…`): a new job has no reason to start on a release line that already runs with a deprecation notice. Raising the other jobs is a change of its own (section 8). |
| D4 | shellcheck is given to actionlint by its path, and each tool prints its version before the lint. (Tightened after review, section 10: one variable holds the folder for all three lines. Again in round 2, section 11: each tool's whole path is written once, under `set -u`.) | Section 2: actionlint is silent when it cannot run shellcheck. A missing or broken binary fails the step at the version line, and every run's log says what did the checking. |
| D5 | `permissions: contents: read`, nothing else; `timeout-minutes: 5`. | As asked. The job needs the files and nothing more; it holds no secret and posts nothing. |
| D6 | No ignore and no configuration file. Every finding fails the check, shellcheck's at every level, down to an unquoted variable (row 8). (After review, section 10: the job is now told to take nothing from a configuration file, where this only said there was none; "every finding" means every one the two tools report, and actionlint itself leaves seven of shellcheck's rules out; and the comment goes on the line above the command.) | The files pass as they are, so there is nothing to except. If a finding ever has to be excepted, it is with a `# shellcheck disable=SC…` comment on that line of the script, the reason beside it, and never a rule switched off for the whole run. |
| D7 | `CONTRIBUTING.md` gets a short part under "Pull request cycle": what the check is, how to run it locally (install the two tools, run `actionlint` in the repository root), which versions CI pins, and that actionlint is silent without shellcheck. Step 3 of the cycle names the check. (After review, section 10: it names no version, and points at `ci.yml` for them, so that raising a pin leaves nothing behind here.) | As asked. One place, and no per-system install commands to keep true: it links to the two projects' own install pages. |
| D8 | Two review agents, one on security and one on correctness, read the local commit before the completion gate, as on the last changes. Copilot's review, if it starts on the pull request, is answered as well. | The practice here since Copilot's review stopped starting by itself. The diff is small, so one round is expected. Say so at this gate if a change this small should go without. |
| D9 | Outward steps one at a time, each on Joshua's own go-ahead: the push and the pull request at the completion gate; the merge asked separately, through the REST call. | The repository's process. Auto-merge stays off. |

## 5. What gets built

**The job**, added at the end of `.github/workflows/ci.yml`, after `pr-title`:

```yaml
  # Reads every file in .github/workflows/ with actionlint, which hands the bash of their run:
  # steps to shellcheck. No check on a pull request executes release-please.yml (it runs on a
  # push to master and by hand), so without this a slip in it would first show after the merge.
  # CONTRIBUTING.md ("Pull request cycle") says how to run the same two tools locally.
  lint-workflows:
    name: lint-workflows
    runs-on: ubuntu-latest
    timeout-minutes: 5
    permissions:
      contents: read
    steps:
      # Pinned to a commit, like everything this job fetches: the commit of the v7.0.1 tag.
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false

      # Both linters come from their projects' releases on GitHub, each held to one version and
      # to the SHA-256 of its linux x86_64 archive. To move to a newer release, change a version
      # and its checksum together. actionlint lists its checksums in the release's
      # actionlint_<version>_checksums.txt; shellcheck publishes none, so take the digest GitHub
      # lists for the archive on the release page.
      - name: Download actionlint and shellcheck
        env:
          ACTIONLINT_VERSION: "1.7.12"
          ACTIONLINT_SHA256: "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8"
          SHELLCHECK_VERSION: "0.11.0"
          SHELLCHECK_SHA256: "8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198"
        run: |
          cd "$RUNNER_TEMP"
          curl -fsSL --retry 3 -o actionlint.tar.gz \
            "https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/actionlint_${ACTIONLINT_VERSION}_linux_amd64.tar.gz"
          curl -fsSL --retry 3 -o shellcheck.tar.xz \
            "https://github.com/koalaman/shellcheck/releases/download/v${SHELLCHECK_VERSION}/shellcheck-v${SHELLCHECK_VERSION}.linux.x86_64.tar.xz"
          # Checked before anything is unpacked: a file that is not the pinned one stops the job.
          printf '%s  %s\n' "$ACTIONLINT_SHA256" actionlint.tar.gz "$SHELLCHECK_SHA256" shellcheck.tar.xz \
            | sha256sum --check --strict -
          mkdir lint-tools
          tar -xzf actionlint.tar.gz -C lint-tools actionlint
          tar -xJf shellcheck.tar.xz -C lint-tools --strip-components=1 "shellcheck-v${SHELLCHECK_VERSION}/shellcheck"

      # actionlint leaves the shell checks out, and says nothing, when it cannot run shellcheck.
      # So it is given shellcheck's path, and each tool prints its version first: a tool that
      # does not run fails the step there.
      - name: Lint the workflow files
        run: |
          "$RUNNER_TEMP/lint-tools/actionlint" -version
          "$RUNNER_TEMP/lint-tools/shellcheck" --version
          "$RUNNER_TEMP/lint-tools/actionlint" -color -shellcheck "$RUNNER_TEMP/lint-tools/shellcheck"
```

This is the text the runs of section 2 used. Nothing else in `ci.yml` changes, and `codeql.yml` and
`release-please.yml` are not touched. (This is the job as approved. Section 10 has it as it stands
after review.)

**`CONTRIBUTING.md`**, under "Pull request cycle". Step 3 becomes: "**CI must be green** —
`build-test` runs the full suite, including Testcontainers integration tests, and `lint-workflows`
reads the workflow files (below)." And after the five steps:

> ### Workflow files are linted
>
> The `lint-workflows` check runs [actionlint](https://github.com/rhysd/actionlint) on every file
> in `.github/workflows/`, and actionlint hands the bash in their `run:` steps to
> [shellcheck](https://github.com/koalaman/shellcheck). No check on a pull request executes the
> release workflow or the `publish-main` job — both run on a push to `master` — so this is what
> reads a change to them before the merge. Every finding fails the check, down to an unquoted
> variable.
>
> To run it locally, install both tools ([actionlint](https://github.com/rhysd/actionlint/blob/v1.7.12/docs/install.md),
> [shellcheck](https://github.com/koalaman/shellcheck#installing)) and, in the repository root, run
> `actionlint`. It prints nothing when it finds nothing. CI pins actionlint 1.7.12 and shellcheck
> 0.11.0 — `ci.yml` has the versions and their checksums — so use those if your result and CI's
> differ. Without shellcheck on the `PATH`, actionlint leaves the shell checks out and does not
> say so: `actionlint -verbose` shows whether the `shellcheck` rule was disabled. A shellcheck
> finding is reported at the step's `run:` line; the message carries the line and column inside
> the script.

(The text above is the part as approved. Sections 10 and 11 changed it: it names no version, says
what the linter does not see and how one finding is excepted, and does not call the check required
yet.)

With Q1 A, that part says the check is required, and the paragraph on the `protect-master` ruleset
names it beside `build-test` and `pr-title`. With B or C, it says the check blocks nothing.

**Records**: this plan, `audit.md` entries, and a section in `aidlc-state.md`.

## 6. Execution plan

**0. Before the plan (done 2026-11-16)**
- [x] 0.1 Pull request #179 confirmed merged (`4af1305`, 2026-11-16T16:51:53Z); the branch cut from `origin/master` at that commit, in a worktree of its own.
- [x] 0.2 The three workflow files on `master` linted as they are with actionlint 1.7.12 and shellcheck 0.11.0: nothing found.
- [x] 0.3 The job drafted on a scratch copy, linted, and its two scripts run on Linux; the wrong-pin runs; the thirteen broken copies, the release-notes tests on the seven made from `release-please.yml`, and the controls (section 2).

**1. The job — `.github/workflows/ci.yml`**
- [x] 1.1 `lint-workflows` added after `pr-title`, as in section 5. No other line of the file changes.
- [x] 1.2 On the file as written: the linter finds nothing in the three workflow files; the file parses as YAML with the same three jobs as before plus the new one; no control, format or separator character; the repository holds it with LF. (2026-11-16: section 9.)
- [x] 1.3 The job's two scripts taken out of the file as written and run on Linux again, ending 0. (2026-11-16: both are byte for byte the scripts run before the plan.)

**2. `CONTRIBUTING.md`**
- [x] 2.1 Step 3 of "Pull request cycle" and the new part "Workflow files are linted", worded for the answer to Q1. With Q1 A, the paragraph on the ruleset names the check as well. (2026-11-16, for Q1 A; one paragraph more than section 5 shows: section 9.)

**3. Verification**
- [x] 3.1 The broken copies made again from the file as written and linted with the pinned Linux binaries; the outputs kept for the pull request's description. (2026-11-16: thirteen fail, the unchanged copy passes; the output is byte for byte that of section 2.)
- [x] 3.2 The release-notes tests (`tests/CalCrony.ReleaseNotes.Tests`), the one project that reads repository files, run here. The whole suite is left to `build-test` on the pull request: this change touches no code and no file a test reads. (2026-11-16, Windows: 338 pass, 17 skipped, none fail.)
- [x] 3.3 Two review agents read the local commit (D8); findings answered. (2026-11-17: both read the three local commits up to `631a26e`. Nothing critical; one major finding, that `master` had moved; the rest minor. Section 10. A second round on the four commits up to `33b0f0a`, and a last read of the answers to it: section 11.)

**4. Records and delivery — each outward step on Joshua's own go-ahead**
- [ ] 4.1 `audit.md` entries throughout; the section in `aidlc-state.md` kept up to date.
- [x] 4.2 At the completion gate, on his go-ahead: `master` taken in first if it has moved, the branch pushed, the pull request opened with the evidence of section 2 in its description. (2026-11-17, on Joshua's "Continue to next stage": `master` had not moved since the merge of it; twelve commits pushed; pull request #183 opened at 16:32:29Z. Section 12.) (`master` was taken in once already, on 2026-11-17 as `43076bf`, because the review's evidence had to be restated on it: section 10. It is taken in again here if it has moved since. The description carries the table of section 10.)
- [x] 4.3 On the pull request: `lint-workflows` green — its first run on GitHub's runner — with `build-test` and `pr-title`; Copilot's review answered if it starts. (2026-11-17, on the first head `7ebecd9`: all three green, and the job's first run there took 13 seconds; Copilot's review did not start. Section 12.)
- [x] 4.4 On his separate go-ahead: the squash merge through the REST call. It cuts no release. (2026-11-17, on Joshua's "Merge now": merged at 16:49:15Z as `47c8a15`. Section 13.)
- [x] 4.5 After the merge: the job's run on the push to `master` read. With Q1 A, Joshua then adds the check to the ruleset, and that is recorded. (Since the review: once the ruleset names the check, the two sentences of `CONTRIBUTING.md` that call it required go in with those records — the paragraph on the ruleset, and "It is a required check" in the new part. 2026-11-17: the run on `master` is read and green, in 12 seconds; the ruleset, as GitHub returns it, does not name the check yet, so this step stays open. Section 13. Later the same day the ruleset names it, and the two sentences are written: section 13.)

## 7. Risks and limits

| Risk or limit | Answer |
|---|---|
| The job fails on GitHub's runner for a reason this machine could not show (another Ubuntu, another `tar` or `xz`). | The pull request's own run is the test, before any merge. If `xz` were missing there, shellcheck also ships a `.tar.gz`; the pin would move to it. (Review: those `.tar.gz` files were added to the release five months after it, so that move would be weighed first. And the tool that differs most on the runner is `sha256sum`: section 10.) |
| A release archive is replaced upstream, or GitHub cannot serve it. | The checksum refuses a replaced file, and a failed download fails the step: red, never a silent pass. A re-run clears a failed download; a mismatch is looked into, not re-pinned blindly. |
| The pinned linter grows old: new runner labels, new inputs of an action, new syntax. | It shows as a finding on a pull request that uses the new thing (section 2: `ubuntu-26.04`). The answer then is a newer actionlint, pinned the same way, or the one label declared in `.github/actionlint.yaml`. Nothing raises the pins by itself; the comment in the job says how. (After review: the job takes nothing from a configuration file, and there is no newer actionlint yet. What lets one such label through is an `-ignore` for that one message on the job's last line. Sections 10 and 11.) |
| A finding nobody agrees with blocks a pull request. | D6: a narrow exception on the line, with its reason. |
| A pull request changes the job itself to check less. | It lints the pull request's own copy of the workflows, the job included, so such a change is in the diff under review like any other. (Review named two more ways to check less: a configuration file in the repository, from which the job now takes nothing, and `SHELLCHECK_OPTS` set in the workflow, which the job now unsets. Sections 10 and 11.) |
| A contributor's local run passes and CI's does not. | `CONTRIBUTING.md` names the versions and the case that causes it most often, shellcheck missing locally. (After review it points at `ci.yml` for the versions.) |

## 8. Not in this change

- The ruleset. Q1 decides whether Joshua changes it afterwards.
- `codeql.yml` and `release-please.yml`, and every job of `ci.yml` but the new one. Raising the
  other jobs' actions from the releases that declare Node.js 20, or pinning them to commits, is a
  change of its own; the session on `ci/codeql-action-v4` listed them.
- A security linter for the workflows (pinned actions, narrower permissions), and a Dependabot
  configuration that would raise pinned actions. (Corrected in review: this also listed "what
  untrusted text reaches a script", and actionlint does check that — a pull request's title put
  into a script is refused as untrusted.)
- Findings shown as annotations on a pull request's diff. The job's log has the file, line and
  column of each.

## 9. As built (2026-11-16): what differs from the plan as approved, and what was run

**The job** is the text of section 5, character for character. `ci.yml` as written is the file as
it was with that text appended: 49 lines added, none changed. As the repository holds it, it is
byte for byte the scratch copy the runs of section 2 used.

**`CONTRIBUTING.md`** is the text of section 5, worded for Q1 A — the new part says "It is a
required check", and the paragraph on the ruleset names `lint-workflows` beside `build-test` and
`pr-title` — with three differences (the third was left out of this list until review):

- The command stands in a code block of its own, like the commands of "Local dev quickstart".
- Step 3's "(below)" is a link to the new part.
- One paragraph more, on excepting a finding. It is D6 written for a contributor, and it corrects
  D6's wording in two places: the comment goes on the line above the command, not on the command's
  line, and the reason cannot follow it as plain text. Tried on scratch copies with the pinned
  Linux binaries, an unquoted variable in `publish-main`'s script each time:

  | The script has | The linter |
  |---|---|
  | the unquoted variable, nothing else | fails, SC2086 |
  | `# shellcheck disable=SC2086` on the line above it, the reason in a comment above that | passes |
  | `# shellcheck disable=SC2086 # the reason` on the line above it | passes |
  | `# shellcheck disable=SC2086 -- the reason` on the line above it | fails: shellcheck cannot read the directive (SC1072, SC1073) |
  | the first form, placed above an earlier command instead | fails, SC2086 |

  So an exception covers the one command it stands above, which is what the paragraph says.
  (Round 1 of the review: a "command" can be a whole function, `if` or block, and the paragraph
  now says so. Section 10.)

(The next paragraph no longer holds: after review `CONTRIBUTING.md` does not call the check
required until the ruleset names it. Section 10.)

**Until the ruleset is changed, `CONTRIBUTING.md` is ahead of it.** The text says the check is
required from the moment this is merged. It becomes so when Joshua adds it to `protect-master`
(step 4.5).

**What was run on the files as written.**

- The linter, both tools, on the three workflow files: nothing found — the Windows binaries on the
  checkout (CRLF), the pinned Linux binaries on the files as the repository holds them (LF).
- `ci.yml` read as YAML before and after: `build-test`, `publish-main` and `pr-title` are the same
  key for key and value for value, the workflow's other keys are the same, and `lint-workflows` is
  the one new job, the last. Its four pins are text, not numbers. No control, format or separator
  character and no tab; the job's own text is plain ASCII.
- The job's two scripts, taken out of the file as written: byte for byte the two run before the
  plan, and run again on Linux (WSL Ubuntu) the way the runner runs a step. Both archives
  downloaded, both checksums matched, both tools printed their versions, the lint ended 0.
- The thirteen broken copies, made again from the file as written: all fail, the unchanged copy
  passes, and the output is byte for byte that of section 2.
- The release-notes tests, on Windows: 338 pass, 17 skipped, none fail.
- The workflow files of pull request #181 (`ci/narrow-release-job-permissions`, head `1a7f16b`),
  read from its branch without touching it: nothing found, alone and with this job added to its
  `ci.yml`. (Corrected in review: this called #181 open. It had been merged a few minutes before
  this was written, at 2026-11-16T23:15:32Z; section 10.)

**Not run.** The job on GitHub's runner: the pull request's own run is the first. The whole test
suite: this change touches no code and no file a test reads, and `build-test` runs it on the pull
request.

## 10. Review round 1 (2026-11-17): `master` taken in, the findings and their answers, and the change as it stands

Two agents read the three local commits (`8960946`, `74707c2`, `631a26e`): one on security, one on
whether the job works and whether every statement written about it is true. Nothing critical. One
major finding, from the correctness reviewer, which the security reviewer made as a note: `master`
had moved. The rest are minor findings and notes. Each finding below was tried again here, on
scratch copies with the pinned Linux binaries, before it was answered; what was not tried here
says so.

### `master` had moved (Q1, S6)

Pull request #181 ("give the two older release jobs only the access each needs") was squash-merged
at 2026-11-16T23:15:32Z as `ab32e96`: two minutes after this branch last looked at `master`
(23:13:23Z), and before its two later commits. It changes `release-please.yml` and the tests that
read it, and adds to the records. So the branch was one commit behind; its `audit.md` would have
conflicted, and GitHub starts no pull-request run while a pull request has conflicts; and part of
section 2 no longer described `master`.

`master` was merged into the branch on 2026-11-17 as `43076bf`. One file conflicted,
`aidlc-docs/audit.md`, where both sides had only appended: six entries from `master`, four from
this branch. They are kept whole, in the order of their timestamps, by a script that writes nothing
unless every entry of each side is there once and whole and each side's own order is kept.
`aidlc-state.md` merged by itself. #181 touches neither `ci.yml` nor `CONTRIBUTING.md`.

What that changes in sections 1, 2 and 9, which describe `master` at `4af1305` and are left as
written but for their marks:

- The release-notes tests now pin the two older jobs as well, `release-please` and
  `publish-images`, so all five. Row 6, the missing brace in the image job's script, was "the one
  the tests pass". It fails one test now
  (`The_images_job_may_read_the_repository_and_push_packages_and_names_its_token_for_the_login_alone`),
  so all seven copies made from `release-please.yml` fail a test.
- On the unchanged file 344 tests pass and 17 are skipped, on Windows (it was 338 and 17).
- Every position in `release-please.yml` is 15 lines further down.
- So what no test sees is `ci.yml` and `codeql.yml`: rows 3, 5 and 10 to 13. Row 5 is the plainest
  case left: `publish-main`'s script is run by no check on a pull request and read by no test. For
  the release workflow the linter adds what the tests cannot give: they say that a line changed,
  not whether the new line is valid.

The table on the merged tree, with the job as revised below and called as the job calls it:

| # | The mistake | In | What the linter says (one finding, shortened) | Linter ends | Today's tests |
|---|---|---|---|---|---|
| 1 | `=` where `==` is meant, in `announce-release`'s `if:` | `release-please.yml` | `218:116: got unexpected character ' ' while lexing == operator, expecting '=' [expression]` | 1 | 1 fails |
| 2 | a step id misspelt in an `if:` (`steps.mention` for `steps.mentions`) | `release-please.yml` | `193:59: property "mention" is not defined in object type {checkout: …; mentions: …} [expression]` | 1 | 1 fails |
| 3 | the three inputs under a `with:` moved out two spaces — still valid YAML | `ci.yml` | `50:14: "with" section should not be empty`, then `51:9: unexpected key "registry" for step to execute action` and two more `[syntax-check]` | 1 | file not read |
| 4 | a script line moved out of its `run:` block — no longer valid YAML | `release-please.yml` | `139:8: could not parse as YAML: could not find expected ':' [syntax-check]` | 1 | 2 fail |
| 5 | the closing brace of `build_and_push()` removed, in `publish-main` | `ci.yml` | `56:9: shellcheck reported issue in this script: SC1056:error:13:1: Expected a '}'` (the second of four; the first, SC1009, is a note that points at the function) `[shellcheck]` | 1 | file not read |
| 6 | the same brace removed in `publish-images`, the job that builds a release's images | `release-please.yml` | `74:9: shellcheck reported issue in this script: SC1056:error:12:1: Expected a '}'` (the second of four) `[shellcheck]` | 1 | 1 fails |
| 7 | an `if` without its `fi`, in the step that asks GitHub about mentions | `release-please.yml` | `167:9: shellcheck reported issue in this script: SC1046:error:10:1: Couldn't find 'fi' for this 'if'` and three more `[shellcheck]` | 1 | 1 fails |
| 8 | `"$WEBHOOK_URL"` left unquoted in the announce step | `release-please.yml` | `233:9: shellcheck reported issue in this script: SC2086:info:1:9: Double quote to prevent globbing and word splitting [shellcheck]` | 1 | 2 fail |
| 9 | `needs:` naming a job that does not exist | `release-please.yml` | `255:3: job "release-summary-preview" needs job "release-pleese" which does not exist in this workflow [job-needs]` and one more | 1 | 1 fails |
| 10 | an input the action does not have (`dotnet-verison`) | `ci.yml` | `18:11: input "dotnet-verison" is not defined in action "actions/setup-dotnet@v4" [action]` | 1 | file not read |
| 11 | a permission scope that does not exist (`package: write`) | `ci.yml` | `44:7: unknown permission scope "package" [permissions]` | 1 | file not read |
| 12 | a runner label that does not exist (`ubuntu-lastest`) | `codeql.yml` | `20:14: label "ubuntu-lastest" is unknown [runner-label]` | 1 | file not read |
| 13 | a schedule on a day of the week that does not exist (`8`) | `codeql.yml` | `12:13: invalid CRON format "17 6 * * 8" in schedule event [events]` | 1 | file not read |
| — | nothing changed | | no finding | 0 | all pass |

The same fourteen copies give the same findings and the same endings with the Windows binaries,
called plainly as a contributor would, with LF and with CRLF line ends. Rows 5 to 8 still pass in
silence when shellcheck cannot be run.

### Findings and answers

| # | Finding | Answer |
|---|---|---|
| Q1, major; S6, note | `master` had moved, and the evidence described a `master` that no longer exists. | Above: `master` taken in, the evidence run again on it, the statements marked. |
| S1, minor | A configuration file in the pull request switches any finding off, without a word. actionlint reads `.github/actionlint.yaml` from the tree it lints, which is the pull request's. Tried here: a file that ignores every finding, beside the missing brace of row 6, ends 0. | The job passes `-config-file /dev/null`, so it takes nothing from a configuration file (round 2: it still parses one that is there; section 11). The same copy ends 1. A finding can then be excepted only in the workflow files themselves: one command by a shellcheck comment in its script, a whole run or a runner label actionlint does not know only in the job. (The job's comment first said "no file in the repository can switch a finding off", which the shellcheck comment contradicts; reworded before the reviewers' confirmation.) |
| Q5, minor | The version lines guarded the two binaries, not the path handed to `-shellcheck`: with only the flag's path misspelt, the step printed both versions and ended 0 on a broken copy. And a green log said nothing about what had been read. | One variable holds the folder for the three lines, so the path cannot be right in one and wrong in another. `-verbose` puts in the log the files that were read, the count of findings, and any rule that was left out. It names pyflakes there when the runner has none, which the security reviewer found to be so on both images: that rule checks steps written in Python, and there are none. (Round 2: the path could still be wrong at the flag alone, and `-verbose` names shellcheck and pyflakes only, not the seven rules. Section 11.) |
| S4, minor; Q9 | The checksum check rests on the runner's `sha256sum`. The reviewers read, from this repository's recent job logs and from the runner-images repository, that the Ubuntu 26.04 image ships the Rust coreutils (uutils 0.10.0), while every wrong-pin run here used GNU coreutils 9.4; and a pull request's green run only ever shows the accepting path. Both read uutils' source at that version and found no fault; neither ran it, and nor did I. | A control before the real check: a checksum of 64 zeros has to be refused, or the step stops. Tried here with a stand-in `sha256sum` that accepts anything: the step ends 1 with nothing unpacked, where the step as first built would have gone on to unpack. So every run, on whatever image, shows the refusing path as well. |
| S8, note | curl could follow a redirect to an address that is not HTTPS. The checksums never rested on that. | `--proto '=https' --tlsv1.2` on both downloads. Tried: both archives download as before. |
| Q2, minor | The reason given in the job's comment and in `CONTRIBUTING.md` was not true of the release workflow: a slip in it does fail `build-test`, through the tests that pin its text. And the comment did not name `publish-main`, which is the job nothing reads. | Both reworded: no check on a pull request executes `release-please.yml` or `publish-main`; the tests that pin the release workflow's text cannot say whether a changed line is valid, and no test reads `ci.yml`. |
| Q3, S5, minor | `CONTRIBUTING.md` called the check required in two places, one of them the paragraph headed "Enforced by the `protect-master` ruleset", while the ruleset names `build-test` and `pr-title` alone. Section 9 said so, but only here, and nothing bounds the time until the ruleset is changed. | Taken, and it departs from the wording of Q1 A, so it is put to Joshua at the completion gate. The paragraph on the ruleset is as on `master`. The new part says "It becomes a required check once it has run green on `master` and the `protect-master` ruleset names it." The two sentences that call it required go in with the records after the merge, once the ruleset names it. Q1's answer itself stands: the check is to be required. |
| Q4, S5, minor | "No rule is switched off for the whole run" was not so. actionlint itself calls shellcheck without seven rules (SC1091, SC2043, SC2050, SC2153, SC2154, SC2157, SC2194), and no flag changes that list. One of them is the rule for a variable that is never assigned, so a misspelt variable name passes. Tried here: `"${imgae}:main"` in `publish-main` ends 0. | The sentence is gone. A new paragraph, "What it does not see", says this and the two points below. |
| S3, minor | A step whose shell is not `bash` or `sh` by that plain name gets no shell check, without a word. Tried here, each with the missing brace: `shell: bash -e {0}` ends 1; `shell: /usr/bin/bash -e {0}` and `shell: pwsh` end 0. The reviewer showed the same through `defaults.run.shell`, three lines at the top of a file. | In "What it does not see". No guard in the job: the reviewer's two text matches are easy to get past, by their own account, and such a line is in the diff. |
| S2, Q6, minor | The paragraph on excepting a finding said an exception covers one command. A command can be a whole function, `if` or block. Tried here: `disable=SC2086` above `build_and_push() {`, or above an `if`, passes an unquoted variable inside; `disable=all`, or the four SC1… codes, above the function with its brace missing ends 0, so a script that does not parse passes. And actionlint's own findings take no such comment. | The paragraph says: one simple command; not above a function, an `if`, a loop or a block; never `disable=all` or an SC1… code; and for actionlint's own findings a newer actionlint, pinned the same way. |
| Q7, nit | The versions stood in `CONTRIBUTING.md` too, twice, where raising a pin would leave them behind. | `CONTRIBUTING.md` names no version: "CI pins one version of each — `ci.yml` has the versions, and the checksums of the Linux archives". The install link points at actionlint's current page, not at one release. |
| Q8, nit | "Pinned to a commit, like everything this job fetches": the two archives are held to a version and a checksum, not to a commit. | "Pinned like everything this job fetches: here to a commit, the one the v7.0.1 tag names." |
| S7, note | When a pin is raised there is a stronger check for actionlint than the checksum file, which sits in the same release as the archive: its build attestation. Tried here: `gh attestation verify` passes for the pinned archive. shellcheck has none. | Recorded here for whoever raises the pins; not put into the job, which would then need a token and a call to GitHub for one of its two tools. (Round 2: the job's comment on raising a pin names it after all. Section 11.) |
| S9, note | For the owner: the two required checks in the ruleset name no source, so any app could report them; when `lint-workflows` is added, GitHub Actions can be chosen as its source. And section 1 listed three settings of the repository that the files do not show. | The first is passed on to Joshua with step 4.5. The second is reworded in place; the earlier wording stays in this branch's first commit, and says nothing the workflow files do not let a reader infer but that the default token is read-only. |
| Q9, nits | In these records: the table's header said "first finding" where rows 5 and 6 quote the second; section 9 counted two differences where there are three; "five files" fetched left out two parts of release pages; section 1 had `pr-title` running on a push; the list of the WSL tools left out `sha256sum`; and Q1's "cannot turn red by itself" leaves out the runner image, which is not pinned. | Each corrected in place and marked. |
| Q3, addendum | Q1's "it never leaves a pull request waiting" does not hold for a pull request that is already open when the check becomes required: it has no such check until its next push. #180 is one. | Marked under Q1, and passed on to Joshua with step 4.5. |
| Q10, nits | In the helper scripts, which are not in the repository: one printed the wrong exit status under a pipe, so one saved file shows `exit=0` under a finding; the guard on a temporary folder's name let `/../` through; a header said six where seven are run. | The guard and the header are fixed. No exit status from that saved file is quoted anywhere in these records. |

### What the linter does not catch — added to the list in section 2

Tried here, each on a scratch copy, each ending 0 with no output:

- a misspelt command (`dockr push`), a misspelt flag, and a misspelt variable name;
- a broken script in a step whose shell is `pwsh`, or `bash` written as a path;
- a broken script under `# shellcheck disable=all`, or under the SC1… codes, placed above the
  function it is in.

From the reviewers' own copies, not tried again here: a misspelt secret name; an action's name or
version that does not exist (`actions/setup-dotnet@v99`); a wrong word inside a quoted string in an
`if:`; a workflow file whose name ends in `.YML`; a script in a composite action under
`.github/actions/`; and a script reached through `bash -c`, a here-document or a file. They also
found caught, which section 2 does not list: `.yaml` files, an empty file, a duplicate key, a tab,
an `if:` that is always true, and shellcheck's findings of every level.

### The job as it stands

```yaml
  # Reads every file in .github/workflows/ with actionlint, which hands the bash of their run:
  # steps to shellcheck. No check on a pull request executes release-please.yml or the
  # publish-main job above: both run on a push to master. The tests that pin the release
  # workflow's text cannot say whether a changed line is valid, and no test reads this file,
  # so without this an invalid change to either would first show after the merge.
  # CONTRIBUTING.md ("Pull request cycle") says how to run the same two tools locally.
  lint-workflows:
    name: lint-workflows
    runs-on: ubuntu-latest
    timeout-minutes: 5
    permissions:
      contents: read
    steps:
      # Pinned like everything this job fetches: here to a commit, the one the v7.0.1 tag names.
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false

      # Both linters come from their projects' releases on GitHub, each held to one version and
      # to the SHA-256 of its linux x86_64 archive. To move to a newer release, change a version
      # and its checksum together. actionlint lists its checksums in the release's
      # actionlint_<version>_checksums.txt, and `gh attestation verify <archive> -R
      # rhysd/actionlint` checks an archive against the build that made it. shellcheck publishes
      # neither, so take the digest GitHub lists for the archive on the release page.
      - name: Download actionlint and shellcheck
        env:
          ACTIONLINT_VERSION: "1.7.12"
          ACTIONLINT_SHA256: "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8"
          SHELLCHECK_VERSION: "0.11.0"
          SHELLCHECK_SHA256: "8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198"
        run: |
          cd "$RUNNER_TEMP"
          curl --proto '=https' --tlsv1.2 -fsSL --retry 3 -o actionlint.tar.gz \
            "https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/actionlint_${ACTIONLINT_VERSION}_linux_amd64.tar.gz"
          curl --proto '=https' --tlsv1.2 -fsSL --retry 3 -o shellcheck.tar.xz \
            "https://github.com/koalaman/shellcheck/releases/download/v${SHELLCHECK_VERSION}/shellcheck-v${SHELLCHECK_VERSION}.linux.x86_64.tar.xz"
          # First a checksum that is wrong for certain. If sha256sum lets that one pass, its
          # answer about the real ones would mean nothing.
          if printf '%064d  %s\n' 0 actionlint.tar.gz | sha256sum --check --strict - > /dev/null 2>&1; then
            echo "sha256sum accepted a checksum of 64 zeros, so it cannot be asked about the real ones."
            exit 1
          fi
          # Then the real ones, each asked the same way, before anything is unpacked: a file
          # that is not the pinned one stops the job.
          printf '%s  %s\n' "$ACTIONLINT_SHA256" actionlint.tar.gz | sha256sum --check --strict -
          printf '%s  %s\n' "$SHELLCHECK_SHA256" shellcheck.tar.xz | sha256sum --check --strict -
          mkdir lint-tools
          tar -xzf actionlint.tar.gz -C lint-tools actionlint
          tar -xJf shellcheck.tar.xz -C lint-tools --strip-components=1 "shellcheck-v${SHELLCHECK_VERSION}/shellcheck"

      # actionlint leaves the shell checks out, and says nothing, when it cannot run shellcheck.
      # So each tool's path is written once, and the tool prints its version from it first: a
      # tool that does not run fails the step there, and with set -u so does a misspelt name.
      # -verbose puts in the log which files were read and whether shellcheck or pyflakes was
      # left out (pyflakes is, when the runner has none: it checks steps written in Python, and
      # there are none). SHELLCHECK_OPTS is unset and -config-file /dev/null is given so that
      # neither that variable, set elsewhere in a workflow, nor a configuration file in the
      # repository can switch findings off.
      - name: Lint the workflow files
        run: |
          set -u
          unset SHELLCHECK_OPTS
          actionlint="$RUNNER_TEMP/lint-tools/actionlint"
          shellcheck="$RUNNER_TEMP/lint-tools/shellcheck"
          "$actionlint" -version
          "$shellcheck" --version
          "$actionlint" -color -verbose -config-file /dev/null -shellcheck "$shellcheck"
```

(This block is the job after the second round, section 11, which changed four things in it: the
two checksums are asked one at a time, each tool's whole path is written once under `set -u`,
`SHELLCHECK_OPTS` is unset, and the comment on raising a pin names the attestation.)

### `CONTRIBUTING.md` as it stands

The paragraph on the ruleset is as on `master`. Step 3 of "Pull request cycle" names the check and
links to the new part. The new part, "Workflow files are linted", has five paragraphs and the
command in a code block: what the check is and why, and that it becomes a required check once the
ruleset names it; how to run it locally; what a local run prints, with no version named; what the
linter does not see; and how one finding is excepted when it has to be. (Counted as four
paragraphs until the second round, which also changed three sentences in the last two: section
11.)

### What was run after these changes, all on the merged tree

(This list is the job as round 1 left it: 63 lines, both checksums asked in one call. Section 11
has what was run on the job as it stands, 68 lines.)

- The linter on the three workflow files: nothing found — the Windows binaries on the checkout,
  the pinned Linux binaries, called as the job calls them, on the files as the repository holds
  them.
- `ci.yml` against `master`'s: 63 lines added, none changed. Read as YAML, the three earlier jobs
  and the workflow's other keys are the same, and `lint-workflows` is the one new job. As the
  repository holds it, the file is byte for byte the scratch copy whose scripts were run.
- The job's two scripts, taken out of the YAML and run on Linux as the runner runs a step: both
  archives downloaded, the control passed, both checksums matched, both tools printed their
  versions, and the lint read three files and ended 0. Then the download step six times with one
  thing wrong: the five wrong pins of section 2, and a `sha256sum` that accepts anything. Each
  ended non-zero with nothing unpacked.
- The thirteen broken copies and the unchanged one, as in the table; the same on Windows with LF
  and with CRLF; and the four shell rows without shellcheck.
- The release-notes tests on the merged tree, on Windows: 344 pass, 17 skipped; and on each of the
  seven copies made from `release-please.yml`, where one or two fail every time.
- Fourteen more copies, one for each claim of the reviewers that was tried here.

### Still not run

The job on GitHub's runner. The reviewers read these versions for its Ubuntu 26.04 image from
recent job logs of this repository and from the runner-images repository — bash 5.3, curl 8.18,
GNU tar 1.35, xz 5.8, and `sha256sum` from the Rust coreutils — and that `ubuntu-latest` is still
moving to it until 2026-11-19, so a run may land on 24.04, which this machine's WSL matches. None
of that was read again here. The pull request's own run is the first on either image, and with the
control it tries the refusing path of that image's `sha256sum` as well.

## 11. Review round 2 (2026-11-17): the answers confirmed, and what the second read found

The same two agents read the four commits after `631a26e`: the merge `43076bf`, the answers
`37e5906`, their records `098483c`, and `33b0f0a`. Both report every finding of round 1 closed,
but for the note on the attestation, and the merge clean: nothing in it but the merge and the
resolution of the one conflict; `aidlc-state.md` exactly what git's own merge gives; and
`audit.md` the base followed by ten entries, each byte for byte from one side — six from `master`,
four from this branch — none missing or doubled, in the order of their timestamps. Both ran the
job's two scripts again and found the control, the flags and the guards doing what section 10
says. Nothing critical or major. What the second read found, each tried here again before it was
answered:

| # | Finding | Answer |
|---|---|---|
| S2-1, minor; also the correctness reviewer | A slip at the `-shellcheck` flag alone still left shellcheck out with the step green. One variable held the folder, but the variable's name and the file's name were each still written twice; `"$tool/shellcheck"` at the flag ends 0 on a broken copy. And the job cannot catch that slip in itself: the rule for an unassigned variable is one of the seven actionlint leaves out. | Each tool's whole path is written once, and the script begins with `set -u`. A misspelt variable name at the flag then ends the step ("unbound variable"), and a misspelt file name fails at the version line. |
| S2-2, minor; Q2-3, nit | `SHELLCHECK_OPTS` set outside the job, in an `env:` at the top of `ci.yml`, switched shell findings off for the whole run, which the job's comment and `CONTRIBUTING.md` said could be done in the job only. Tried here: with that variable excluding the four codes of a missing brace, the step as it was ends 0 on the broken copy, "Found 0 errors in 3 files". | The step unsets `SHELLCHECK_OPTS`. The same run ends 1 with the four findings. |
| Q2-1, minor | The paragraph on excepting a finding gave one answer for a runner label the pinned actionlint does not know, a newer actionlint, and there is none: 1.7.12 is still the latest release and its source does not know `ubuntu-26.04`. The other answer, the label declared in `.github/actionlint.yaml`, no longer works in this job. Tried here: with the label declared, the job's call ends 1 and a plain call 0. | The sentence adds what does work: "or, while no release knows it, an `-ignore` for that one message on the job's last line, with the reason beside it." Tried here: `-ignore 'label "ubuntu-26\.04" is unknown'` ends 0 on that copy, and 1 on a copy with another label that does not exist. |
| S2-3, note; Q2-3, nit | "Reads no configuration file" is not what happens. actionlint still opens and parses `.github/actionlint.yaml` when there is one; with the flag it takes nothing from it. Tried here: a file that does not parse ends the job's call with 3, "could not parse config file". | "Takes nothing from a configuration file" in the job's comment and in `CONTRIBUTING.md`; marked in sections 4, 7 and 10. The audit entry of 2026-11-17T00:22:09Z has the old words; the next entry says so. |
| S2-4, note | The control asks `sha256sum` about a list of one line; the real check gave it two. A `sha256sum` that checked the first line soundly and never read a second would pass the control and let a replaced shellcheck archive through. No real one is known to do that. | The two checksums are asked one at a time, each in the control's own form. |
| S2-5, note | "`-verbose` puts in the log … any rule that was left out" said more than the log does: it names shellcheck or pyflakes when actionlint cannot run them, not the seven rules it leaves out itself, nor a step passed over for its shell. | "…and whether shellcheck or pyflakes was left out." |
| S2-7, note | The attestation belongs where a pin is raised: whoever does that reads the job's comment, not this plan. | One clause in that comment: `gh attestation verify <archive> -R rhysd/actionlint` checks an archive against the build that made it. Still not run by the job. |
| Q2-2, nit | `CONTRIBUTING.md`'s example of a shell that gets no shell check, `/usr/bin/bash`, is one the linter refuses outright as an invalid shell name. The silent case is a path with arguments. And "under that plain name" was narrower than the tool, which still checks `shell: bash -e {0}`. Tried here, each with the missing brace: `/usr/bin/bash` ends 1 with that message; `/usr/bin/bash -e {0}` and `pwsh` end 0; `bash -e {0}` and `sh` end 1 with shellcheck's findings. | "…steps whose shell is `bash` or `sh` by that name — the default on these runners: a step with `shell: pwsh`, or with the shell written as a path such as `/usr/bin/bash -e {0}`, gets no shell check." |
| Q2-4, nits | In these records: the tests pin both older jobs since #181, not the image job alone; step 4.5 did not carry the two sentences that wait for the ruleset; five statements overtaken by round 1 had no mark; the new part of `CONTRIBUTING.md` has five paragraphs and a code block, not four; the header of the superseded helper script still said six. | Each corrected in place or marked. |
| S2-6, note | The audit entry of 2026-11-17T00:22:09Z was one commit behind: it says 62 lines where `33b0f0a` made it 63, and names two commits where a third followed. And the first round had left seventeen small files in WSL's `/tmp` that the security reviewer could not remove. | The next audit entry carries the corrections. The seventeen files — 6.7 KB, each a copy of a `run:` script, the only such files there owned by the user — were looked at and removed, each named in full. |

Accepted by the reviewers as answered without a change to the job: no text guards for shellcheck
comments or for a step's shell (they are easy to get past, and such a line is in the diff);
`ubuntu-latest` kept, because the control holds on any image; and the first commit of the branch
left as it is.

**The job** is the block in section 10, which is updated to it. Against `master`'s `ci.yml` that is
68 lines added, none changed.

**What was run after these changes.**

- The job's two scripts, taken out of the YAML as it now is and run on Linux as the runner runs a
  step: both archives downloaded, the control passed, each checksum matched, both tools printed
  their versions, and the lint read three files and ended 0. Then the download step with the six
  wrong inputs of section 10: each ended non-zero with nothing unpacked.
- The lint step on the copy with the missing brace in `publish-main`, with `SHELLCHECK_OPTS`
  excluding that mistake's four codes: 0 as the step was, 1 as it is.
- The copies for the runner label and for the five ways of writing a step's shell, as in the table.
- On the files as written: the linter finds nothing in the three workflow files, with the Windows
  binaries on the checkout and the pinned Linux binaries as the job calls them; and the thirteen
  broken copies all fail and the unchanged one passes, at the same positions as in section 10's
  table.
- The release-notes tests were not run again: they read neither `ci.yml` nor `CONTRIBUTING.md`,
  and `release-please.yml` and the tests are `master`'s.

**The last read.** Both agents then read the answers to round 2 alone, `af6bbce` and its records
`784bb61`. Both report every finding of round 2 closed and nothing wrong in the changed lines of
the two scripts: `unset SHELLCHECK_OPTS` under `set -u` ends 0 when the variable was never set; a
wrong or an empty checksum for either archive stops the step with nothing unpacked; a name
misspelt at the flag ends the step, and a missing shellcheck fails at its version line. What was
left is wording, answered in the commit after:

- "A finding can be switched off for the whole run only in the job itself" was still one notch too
  wide, the security reviewer found. Keys at the top of `ci.yml` reach the job's steps from outside
  its text: an `env:` that names a file for bash to read first — it tried that, and the lint step
  ends 0 on a broken tree — or a `defaults.run.shell`. Those replace what runs the steps, they
  silence `build-test` as well, and there is no end of them; the job is not hardened against them.
  `CONTRIBUTING.md` now says "only in `ci.yml`", and the job's comment says "neither that variable,
  set elsewhere in a workflow, nor a configuration file". Neither was tried again here: the new
  wording claims less.
- Three marks in this plan that round 2 had made stale: on D4, on step 3.3, and over section 10's
  list of what was run.

**Which commits were read.** Round 1: the three up to `631a26e`, by both agents. Round 2: the four
up to `33b0f0a`, by both. The last read: `af6bbce` and `784bb61`, by both. The commit after that
changes one comment line of the job, one phrase of `CONTRIBUTING.md` and these records, in the
reviewers' own words. It was checked here and not read again by an agent.

## 12. The pull request (2026-11-17)

On Joshua's "Continue to next stage" at the completion gate. `master` had not moved (`ab32e96`), so
there was nothing to take in. Everything the branch adds, and the description, were read once more
by a script for a local path, an address, anything shaped like a token, a mention outside
backticks and a hidden character: nothing. The branch was pushed with the keyring login — twelve
commits, none with an attribution line — and pull request #183 was opened against `master` at
2026-11-17T16:32:29Z. Its description on GitHub is the draft, character for character.

**The job's first run on GitHub's runner** (run 44126950195, on the first head `7ebecd9`): green,
in 13 seconds, 16:32:38Z to 16:32:51Z. From its log:

- the image is `ubuntu-26.04`, version 20261111.82.6;
- `actions/checkout` was fetched at the pinned commit;
- both archives downloaded, and the control let the step go on, so that image's `sha256sum`
  refused the checksum of 64 zeros; it then answered `OK` for each archive;
- the two tools printed 1.7.12 and 0.11.0;
- the lint collected 3 files and found 0 errors in them, and `-verbose` named pyflakes as left
  out, the runner having none, as the job's comment says.

This is the run sections 9 to 11 list as still not run. What a green run cannot show is the real
check refusing a wrong archive on that image; the control is the nearest thing to it.

**The other checks on that head**: `pr-title` passed; `build-test` passed, on the same image, all
1,653 tests with none skipped (721 API, 418 bot, 153 web, 361 release notes); both CodeQL analyses
and the secret scan passed; `publish-main` was skipped, as on every pull request. No review by
Copilot was requested: the pull request's list of events was empty two minutes after it opened,
and no reviewer was asked. "Auto-fix pull requests" is on for this session in the app; auto-merge
is off and stays off.

**`master` taken in a second time.** Pull request #180 was merged at 16:36:36Z as `b582c2b`, half a
minute after the second push (`089f1f0`, the records of this section), and the pull request had
conflicts. `master` was merged in as `a018c99`. The two record files conflicted. In `audit.md`
each side had put entries in among the base's — eleven from `master`, nine from this branch — and
they are kept whole, between the same neighbours, in the order of their timestamps, 237 in all;
in `aidlc-state.md` `master`'s new section stands first and this branch's after it. #180 touches
no workflow file and not `CONTRIBUTING.md`. On the merged tree the linter finds nothing in the
three workflow files and the release-notes tests pass, 345 with 17 skipped on Windows. The checks
run again on the new head.

**What is left**, each on Joshua's own word: the squash merge (step 4.4), through the REST call
with the head named; then, on his side, `lint-workflows` added to the required checks of
`protect-master`; and after that the records, with the two sentences of `CONTRIBUTING.md` that
call the check required (step 4.5).

## 13. The merge, and the first run on `master` (2026-11-17)

On Joshua's "Merge now". Checked before the call: the pull request open at head `65f73dc`, every
check on that head green, `master` unmoved, auto-merge off, the description still the one posted.
Pull request #183 was squash-merged through the REST call with that head named, at
2026-11-17T16:49:15Z, as `47c8a15`, a commit GitHub signed. `master`'s tree is the branch's tree.
It cut no release.

**On `master`** the three runs the push started are green. `lint-workflows` passed in 12 seconds
(run 44130102022, 16:49:21Z to 16:49:33Z), its first run there: the image `ubuntu-26.04`
(20261111.196.1), both checksums `OK`, 3 files collected, 0 errors. `build-test` and
`publish-main` passed after it. The release workflow succeeded and cut nothing. CodeQL passed.
From here on every pull request gets a `lint-workflows` check with its next push.

**The ruleset is not changed yet, as GitHub returns it.** Asked whether he had added the check to
the required ones, Joshua answered "Yes, it's added". Read three ways, at 16:53:52Z and again at
16:54:51Z — the ruleset itself, the rules in force for `master`, and the list of rulesets — the
required checks of `protect-master` are `build-test` and `pr-title`, last changed on 2026-07-17;
there is no other ruleset, and no branch protection of the older kind on `master`. So step 4.5
stays open, and the two sentences of `CONTRIBUTING.md` that call the check required are not
written until a read shows the check named. He is told what was read.

**The ruleset names the check.** On Joshua's "It's saved now", read the same three ways at
23:13:42Z: the ruleset was changed at 2026-11-17T23:13:05Z, and its required checks are
`build-test`, `pr-title` and `lint-workflows`, the last with GitHub Actions named as its source.
So step 4.5 is done: `CONTRIBUTING.md`'s paragraph on the ruleset names `lint-workflows` beside
the other two, and "Workflow files are linted" says "It is a required check." `master`, which had
moved three times (#182, #186, #184), was taken into the records branch first; none of the three
touches `CONTRIBUTING.md` or a workflow file.
