# Contributing to CalCrony

This document is the standard for how changes, releases, and issues flow through this repo. It applies to humans and AI assistants alike.

## Branching strategy — GitHub Flow

`master` is the trunk and is **always releasable**. No direct pushes; every change lands through a pull request.

> **Enforced by the `protect-master` ruleset** (the repo went public 2026-07-17, mirroring FairShare's `Protect main` ruleset, adapted to this repo's squash-only process): `master` blocks force-pushes and deletion, requires a PR with 1 approving review (stale reviews dismissed on push, last-push approval, all review threads resolved), squash as the only allowed merge method, signed commits, Copilot code review on every push, and passing `build-test` + `pr-title` checks. Repository admins have an always-bypass — which is how solo-maintainer PRs get merged, since you can't approve your own PR: address the Copilot review, wait for green checks, then merge with the bypass (`gh pr merge --squash` as admin, or the UI's bypass confirmation), exactly like FairShare's "self-authored PRs need admin merge" flow.

| Branch prefix | Use for | Example |
|---|---|---|
| `feature/` | New functionality | `feature/time-polls` |
| `fix/` | Bug fixes | `fix/dst-parse-offset` |
| `chore/` | Tooling, deps, refactors with no behavior change | `chore/bump-efcore` |
| `docs/` | Documentation only | `docs/deploy-guide` |

Rules of thumb:

- Branch from current `master`; keep branches short-lived (days, not weeks).
- One logical change per branch/PR. If a PR needs a paragraph of "and also...", split it.
- Rebase or merge `master` into your branch if it falls behind; CI runs against the merge result.
- Urgent production fixes are just `fix/` branches — same flow, they simply get reviewed and released quickly. No separate hotfix machinery.

## Pull request cycle

1. **Open a PR into `master`.** Fill in the template; link issues with `Closes #n` so merges close them automatically.
2. **Title must be a conventional commit** (`type(scope)!: summary`). CI enforces this (`pr-title` check) because the squash commit takes its message from the PR title, and versions are computed from those messages.
3. **CI must be green** — `build-test` runs the full suite, including Testcontainers integration tests.
4. **Review** — solo-maintainer flow: self-review the diff in the PR UI, and/or run `/code-review` (Claude) on the branch; fix findings before merge. If a second human is ever involved, request their review instead.
5. **Squash merge only.** The branch is deleted automatically after merge.

## Versioning & releases — conventional commits + release-please

Versions follow [SemVer](https://semver.org/) and are **computed from commit messages** on `master` by [release-please](https://github.com/googleapis/release-please):

| PR title (squash commit) | Version effect | Changelog section |
|---|---|---|
| `feat: ...` | **minor** bump | Features |
| `fix: ...` | **patch** bump | Bug Fixes |
| `feat!: ...` / `fix!: ...` or a `BREAKING CHANGE:` footer | **major** bump | Breaking |
| `chore:` `docs:` `refactor:` `test:` `ci:` `perf:` `build:` | no release by itself | (not listed) |

**Release flow:**

1. Merged PRs accumulate on `master`; release-please maintains a rolling **release PR** (e.g. `chore(master): release 0.2.0`) containing the computed version and CHANGELOG entries.
2. **Merging the release PR *is* the release.** It creates the `vX.Y.Z` tag, the GitHub Release with notes, and triggers image publishing.
3. The `publish-images` job re-runs tests on the tagged commit, then pushes Docker images to GHCR:
   - `ghcr.io/jjwren/calcrony-api:{version}` and `:latest`
   - `ghcr.io/jjwren/calcrony-bot:{version}` and `:latest`
   - `ghcr.io/jjwren/calcrony-web:{version}` and `:latest` (nginx-served Blazor app; `API_BASE_URL` is injected at container start)
4. Deploying a release = `docker login ghcr.io` then point compose at the versioned images and `docker compose pull && docker compose up -d`. The running API reports its version at `GET /health`.

Don't hand-create `v*` tags; let release-please own them. To force a specific version (rarely), put `Release-As: X.Y.Z` in a commit body on `master`.

## Issues — how they open and close

**Opening:** always via the issue forms (blank issues are disabled):

- **Bug report** — auto-labeled `bug` + `needs-triage`
- **Feature request** — auto-labeled `enhancement` + `needs-triage`

**Triage** (do this when picking up work): remove `needs-triage`, set a priority label (`priority: high` / `priority: low`), and re-label type if needed (`chore`, `documentation`).

**Closing — exactly one of:**

- **By a PR**: the fixing/implementing PR says `Closes #n`; the merge closes the issue. Preferred path.
- **Won't do**: comment the reasoning, label `wontfix`, close.
- **Duplicate**: comment `Duplicate of #n`, label `duplicate`, close.
- **Can't reproduce / stale**: comment what was tried, close; reopen anytime with new info.

An issue is never closed silently — the closing comment or linked PR must make the resolution obvious a year later.

## Local dev quickstart

```bash
dotnet test CalCrony.slnx        # full suite; Docker must be running (Testcontainers)
docker compose up -d postgres    # just the database for local API runs
dotnet run --project src/CalCrony.Api
```
