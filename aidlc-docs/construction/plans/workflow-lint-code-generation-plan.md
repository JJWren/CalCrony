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
| `ci.yml` | `build-test`, `pr-title`, `publish-main` | on a pull request into `master` and on a push to `master`; `publish-main` on the push only |
| `codeql.yml` | `analyze`, once per language | on a pull request, on a push to `master`, weekly |
| `release-please.yml` | `release-please`, `publish-images`, `release-summary`, `announce-release`, `release-summary-preview` | on a push to `master` and by hand |

- **What a pull request never runs**: all of `release-please.yml`, and `publish-main` with its build
  script. A mistake there shows after the merge — in the release roll, in the `:main` images, or at
  the next release in its images and its announcement.
- **What reads the files before a merge today**: the tests in `tests/CalCrony.ReleaseNotes.Tests`
  read `release-please.yml` as text and pin three of its jobs. No test reads `ci.yml` or
  `codeql.yml`. Nothing checks a shell script unless a test runs it, which three steps' scripts are.
- **The ruleset** `protect-master` requires two checks by name, `build-test` and `pr-title`.
  Repository admins bypass it, which is how pull requests are merged here.
- **Actions settings**: the default token is read-only, any action is allowed, and pinning to a
  commit is not required. The workflows name their actions by major tag (`actions/checkout@v4`).
  There is no Dependabot configuration and no actionlint configuration.
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
Linux was this machine's WSL Ubuntu (bash 5.2.21, curl 8.5.0, GNU tar 1.35, xz 5.4.5), not GitHub's
runner, which is on Ubuntu 26.04: the pull request's own run is the first there.

**A wrong pin stops the job before anything is unpacked.** Five runs of the download step, each
with one thing changed: a checksum with one digit changed, an empty checksum, the two checksums
swapped, a version that was never released, and the version set back to 1.7.11 with the checksum
left. Each ended non-zero (the unreleased version at the download, the others at the checksum),
and none left an unpacked binary behind.

**What the linter catches.** Thirteen scratch copies, each with one deliberate mistake, linted with
the Linux binaries the job pins. Every one fails, and the unchanged copy passes. The last column
is the release-notes tests run here on the same copy (338 pass and 17 are skipped on the unchanged
file, on Windows); they read `release-please.yml` and no other workflow file.

| # | The mistake | In | What the linter says (first finding, shortened) | Linter ends | Today's tests |
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

**What the linter does not catch, and one thing to know about it.**

- It reads; it runs nothing. What a script does, what an expression is worth at run time, and what
  an action does with its inputs are outside it.
- **Without shellcheck it leaves the shell checks out and says nothing.** Rows 5 to 8 pass, ending
  0 with no output, when shellcheck's path points at nothing or no shellcheck is on the `PATH`.
  Only `-verbose` says `Rule "shellcheck" was disabled`. The job answers this in D4.
- **Its lists are as old as its release.** actionlint 1.7.12 does not know the label
  `ubuntu-26.04`: a copy of `codeql.yml` naming it is refused as an unknown label, and passes once
  the label is declared in `.github/actionlint.yaml`. The workflows name `ubuntu-latest`, so
  nothing is refused today.
- It is not a security review of the workflows: it does not ask for pinned actions or narrower
  permissions.

**The branch `ci/codeql-action-v4`**, which another session holds, changes `codeql.yml` to
`github/codeql-action` v4. Its file, read from the branch without touching it, passes the linter.

**Fetched for this**: five files from the two projects' release pages on GitHub (the two Linux
archives above, the two Windows ones, and actionlint's checksum file), into this session's scratch
folder. Nothing was installed on the machine, and the temporary folders used inside WSL were
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

## 4. Decisions this plan takes (accepted unless Joshua says otherwise)

| # | Decision | Why |
|---|---|---|
| D1 | The job is a fourth job in `ci.yml`, `lint-workflows`, not a workflow of its own. It runs on every pull request and every push to `master`, with no path filter, and no other job waits for it. | `ci.yml` already has exactly the triggers asked for. A job that always runs can be made required (Q1). `publish-main` goes on needing `build-test` alone: a finding should not hold the `:main` images back. |
| D2 | Both tools are downloaded as release binaries from their projects on GitHub, each held to a version and to the SHA-256 of its archive, checked before anything is unpacked. actionlint 1.7.12 and shellcheck 0.11.0. | What runs is named in the workflow file and can be checked against the release page by anyone. Not taken: actionlint's Docker image by digest (one opaque digest, shellcheck's version only implied, Docker Hub added to a check that may be required), and a wrapper action (it would download the linter itself at run time, unchecked). Not taken either: the runner's own shellcheck, whose version moves with the runner image. |
| D3 | `actions/checkout` is pinned to a commit, `3d3c42e5aac5ba805825da76410c181273ba90b1`, the tag `v7.0.1` (the current release; it declares Node.js 24), with `persist-credentials: false`. The other jobs stay on `@v4`. | The task asks for a commit where an action is used. v7.0.1 rather than the commit behind `@v4` (v4.4.0, `11d5960a…`): a new job has no reason to start on a release line that already runs with a deprecation notice. Raising the other jobs is a change of its own (section 8). |
| D4 | shellcheck is given to actionlint by its path, and each tool prints its version before the lint. | Section 2: actionlint is silent when it cannot run shellcheck. A missing or broken binary fails the step at the version line, and every run's log says what did the checking. |
| D5 | `permissions: contents: read`, nothing else; `timeout-minutes: 5`. | As asked. The job needs the files and nothing more; it holds no secret and posts nothing. |
| D6 | No ignore and no configuration file. Every finding fails the check, shellcheck's at every level, down to an unquoted variable (row 8). | The files pass as they are, so there is nothing to except. If a finding ever has to be excepted, it is with a `# shellcheck disable=SC…` comment on that line of the script, the reason beside it, and never a rule switched off for the whole run. |
| D7 | `CONTRIBUTING.md` gets a short part under "Pull request cycle": what the check is, how to run it locally (install the two tools, run `actionlint` in the repository root), which versions CI pins, and that actionlint is silent without shellcheck. Step 3 of the cycle names the check. | As asked. One place, and no per-system install commands to keep true: it links to the two projects' own install pages. |
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
`release-please.yml` are not touched.

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
- [ ] 3.3 Two review agents read the local commit (D8); findings answered.

**4. Records and delivery — each outward step on Joshua's own go-ahead**
- [ ] 4.1 `audit.md` entries throughout; the section in `aidlc-state.md` kept up to date.
- [ ] 4.2 At the completion gate, on his go-ahead: `master` taken in first if it has moved, the branch pushed, the pull request opened with the evidence of section 2 in its description.
- [ ] 4.3 On the pull request: `lint-workflows` green — its first run on GitHub's runner — with `build-test` and `pr-title`; Copilot's review answered if it starts.
- [ ] 4.4 On his separate go-ahead: the squash merge through the REST call. It cuts no release.
- [ ] 4.5 After the merge: the job's run on the push to `master` read. With Q1 A, Joshua then adds the check to the ruleset, and that is recorded.

## 7. Risks and limits

| Risk or limit | Answer |
|---|---|
| The job fails on GitHub's runner for a reason this machine could not show (another Ubuntu, another `tar` or `xz`). | The pull request's own run is the test, before any merge. If `xz` were missing there, shellcheck also ships a `.tar.gz`; the pin would move to it. |
| A release archive is replaced upstream, or GitHub cannot serve it. | The checksum refuses a replaced file, and a failed download fails the step: red, never a silent pass. A re-run clears a failed download; a mismatch is looked into, not re-pinned blindly. |
| The pinned linter grows old: new runner labels, new inputs of an action, new syntax. | It shows as a finding on a pull request that uses the new thing (section 2: `ubuntu-26.04`). The answer then is a newer actionlint, pinned the same way, or the one label declared in `.github/actionlint.yaml`. Nothing raises the pins by itself; the comment in the job says how. |
| A finding nobody agrees with blocks a pull request. | D6: a narrow exception on the line, with its reason. |
| A pull request changes the job itself to check less. | It lints the pull request's own copy of the workflows, the job included, so such a change is in the diff under review like any other. |
| A contributor's local run passes and CI's does not. | `CONTRIBUTING.md` names the versions and the case that causes it most often, shellcheck missing locally. |

## 8. Not in this change

- The ruleset. Q1 decides whether Joshua changes it afterwards.
- `codeql.yml` and `release-please.yml`, and every job of `ci.yml` but the new one. Raising the
  other jobs' actions from the releases that declare Node.js 20, or pinning them to commits, is a
  change of its own; the session on `ci/codeql-action-v4` listed them.
- A security linter for the workflows (pinned actions, narrower permissions, what untrusted text
  reaches a script), and a Dependabot configuration that would raise pinned actions.
- Findings shown as annotations on a pull request's diff. The job's log has the file, line and
  column of each.

## 9. As built (2026-11-16): what differs from the plan as approved, and what was run

**The job** is the text of section 5, character for character. `ci.yml` as written is the file as
it was with that text appended: 49 lines added, none changed. As the repository holds it, it is
byte for byte the scratch copy the runs of section 2 used.

**`CONTRIBUTING.md`** is the text of section 5, worded for Q1 A — the new part says "It is a
required check", and the paragraph on the ruleset names `lint-workflows` beside `build-test` and
`pr-title` — with two differences:

- The command stands in a code block of its own, like the commands of "Local dev quickstart".
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
- The workflow files of the open pull request #181 (`ci/narrow-release-job-permissions`, head
  `1a7f16b`), read from its branch without touching it: nothing found, alone and with this job
  added to its `ci.yml`.

**Not run.** The job on GitHub's runner: the pull request's own run is the first. The whole test
suite: this change touches no code and no file a test reads, and `build-test` runs it on the pull
request.
