# Code of Conduct — Workflow and Code Generation Plan

**Requirements**: `inception/requirements/code-of-conduct-requirements.md` (approved 2026-11-17T23:43:57Z, Q1–Q4 all A)
**Branch**: `docs/code-of-conduct` from `master` at `e792e1a`
**Source text**: Contributor Covenant 2.1, `content/version/2/1/code_of_conduct.md` in the `EthicalSource/contributor_covenant` repository (branch `release`), CC BY 4.0

## Workflow

| Stage | Runs | Depth |
|---|---|---|
| Requirements analysis | Done | Minimal |
| User stories, application design, units | Skipped | Documentation only |
| Functional, NFR and infrastructure design | Skipped | No code |
| Code generation | Yes | One unit |
| Build and test | Yes | Full suite; no instruction files beyond a short summary entry |

## Steps

### 1. The file (CC1, CC2, CC3, CC5)
- [x] 1.1 `CODE_OF_CONDUCT.md` in the repository root: the Covenant 2.1 text without its Hugo front matter (`+++` block), every other line as published.
- [x] 1.2 Enforcement: `[INSERT CONTACT METHOD]` becomes the address in `SECURITY.md`, with "CalCrony conduct" in the subject.
- [x] 1.3 Scope: one sentence after the Covenant's paragraph — for CalCrony, community spaces include the GitHub repository (its issues, pull requests, reviews and discussions) and the official CalCrony Discord server.
- [x] 1.4 Attribution section and its link references kept whole.

### 2. Links (CC4)
- [x] 2.1 README, "Contributing, releases & security": one clause pointing to `CODE_OF_CONDUCT.md`.
- [x] 2.2 `CONTRIBUTING.md`: one line near the top saying the Code of Conduct applies to everyone taking part.

### 3. Checks (NF2, NF3)
- [x] 3.1 Scan the new and changed files for control and format characters.
- [x] 3.2 Diff the file against the published 2.1 text: only steps 1.1–1.3 differ.
- [x] 3.3 `dotnet test CalCrony.slnx`, full suite.

### 4. Records and pull request
- [x] 4.1 `aidlc-state.md` line, audit entries, this plan ticked.
- [x] 4.2 One commit, `docs: add a code of conduct`, no attribution lines.
- [x] 4.3 Push and open the pull request — on Joshua's go-ahead.
- [x] 4.4 After merge: the community profile lists the code of conduct. Done 2026-11-18: 100 percent, the file listed as "Other" rather than as the Contributor Covenant.
