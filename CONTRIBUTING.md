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

**What counts as `docs:`** — repository-only material a user of the running app never sees: README, CONTRIBUTING, SECURITY, the legal files, `aidlc-docs/`. Content that ships **inside the applications** — including the web app's `/docs` page (`Docs.razor`), landing-page copy, command descriptions, and embed/reply text — is product surface: use `feat:` (new user-visible content/guidance) or `fix:` (correcting it) so the change appears in the CHANGELOG and cuts a release users can actually pull.

**Release flow:**

1. Merged PRs accumulate on `master`; release-please maintains a rolling **release PR** (e.g. `chore(master): release 0.2.0`) containing the computed version and CHANGELOG entries. The release workflow keeps one comment on it that shows the [release summary](#release-summary) the release will go out with, or says that none has been written yet.
2. **Merging the release PR *is* the release.** It creates the `vX.Y.Z` tag, the GitHub Release with notes, and triggers image publishing. The `release-summary` job then puts the version's summary, when it has one, on top of those notes.
3. The `publish-images` job re-runs tests on the tagged commit, then pushes Docker images to GHCR:
   - `ghcr.io/jjwren/calcrony-api:{version}` and `:latest`
   - `ghcr.io/jjwren/calcrony-bot:{version}` and `:latest`
   - `ghcr.io/jjwren/calcrony-web:{version}` and `:latest` (nginx-served Blazor app; `API_BASE_URL` is injected at container start)
4. Once the images are published, the `announce-release` job posts the release in the community Discord: a ping for the Release Ping role and a link to the release notes, with the summary underneath.
5. Deploying a release = `docker login ghcr.io` then point compose at the versioned images and `docker compose pull && docker compose up -d`. The running API reports its version at `GET /health`.

Don't hand-create `v*` tags; let release-please own them. To force a specific version (rarely), put `Release-As: X.Y.Z` in a commit body on `master`.

### Release summary

The changelog lines release-please writes come from PR titles and are written for developers. A **release summary** is the part written for the people who use CalCrony: a few bullets on what changed and a short how-to for anything new or changed. The release workflow puts it on top of the GitHub release notes and into the Discord announcement, exactly as written.

**Where.** One file per release, named for its version: `docs/release-notes/0.38.0.md`. The version is the one in the open release PR's title. If no release PR is open, your change is the first since the last release, and the version is that release with the minor number raised for a `feat:` or the patch number raised for a `fix:`. If a later `feat:` moves the number, rename the file in that PR; the comment on the release PR names a file left behind.

**Who and when.** Whoever opens a PR that changes something users will notice writes the file, or adds to it, in that same PR, next to the README and Docs page edits. Before merging the release PR, read its "Release summary" comment; anything missing or wrong goes in through a small `docs:` PR first. A summary on its own is `docs:`, so it rides with the next release rather than causing one.

**What.** Major changes; minor ones when they matter to users or are all the release has. Plain words, commands in backticks:

```markdown
### What's new

- **Free slots.** `/availability slots` lists the times a member, or everyone in a role, is free over the next 1 to 14 days.

### How to use it

1. **GM:** save your hours, for example `/settings available-hours set days:weekdays from:6pm to:11pm`.
2. **Players:** run `/availability slots who:@GM` to see when the GM is free.
```

**The rules.** The file is posted as written to two places that render Markdown differently, so the `build-test` check holds every file in the folder to what both show the same way:

- The first line is `### What's new`, with at least one bullet under it. `### How to use it` may follow, once. No other headings.
- No tables, images, HTML or horizontal rules.
- `@name` only inside backticks: outside them GitHub notifies the user of that name, and Discord draws a mention. Anything in `<angle brackets>` goes in backticks too, because one side or the other shows it as raw text; a link written as `<https://…>` is fine.
- A code block that is opened is closed, and the file has no invisible control characters.
- At most 1,800 characters. Discord takes 2,000 in a message, and the announcement's first line needs room as well.

**Preview.** `dotnet run --project tools/ReleaseNotes -- preview --version 0.38.0` prints the Discord message and the release notes that version would get, and sends nothing; `-- check` runs the rules by hand.

**At release time** a summary can add to a release and cannot hold one back. With no file for the version, or if the `release-summary` job fails, the notes and the announcement are what they were before summaries existed: the changelog lines, and a ping with a link. The file is read at the release's tag, so one added after the release PR is merged is too late for that release; edit the GitHub release by hand if one was missed.

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
