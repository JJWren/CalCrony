<!-- PR title must be a conventional commit: type(scope)!: summary
     e.g. "feat: add poll commands", "fix(scheduler): don't double-enqueue pings".
     The title becomes the squash commit on master and drives the next version. -->

## Summary

<!-- What does this PR do, and why? -->

## Linked issues

<!-- "Closes #123" auto-closes the issue on merge. Use "Refs #123" for partial work. -->

Closes #

## Type of change

- [ ] `feat` — new functionality (minor version bump)
- [ ] `fix` — bug fix (patch version bump)
- [ ] `feat!`/`fix!` — breaking change (major version bump; explain below)
- [ ] `chore` / `docs` / `refactor` / `test` / `ci` — no release impact

## Checklist

- [ ] `dotnet test CalCrony.slnx` passes locally (Docker running for Testcontainers)
- [ ] New/changed behavior is covered by tests
- [ ] EF model changes include a migration (`dotnet ef migrations add ...`)
- [ ] Docs updated where behavior changed (README, CONTRIBUTING, aidlc-docs)
