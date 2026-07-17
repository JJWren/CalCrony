# Security Policy

CalCrony is a self-hosted Discord event/calendar bot. It handles Discord user IDs, event data, and — when the calendar feature is used — encrypted Google OAuth tokens, so security reports are taken seriously even though this is a personal-scale project.

## Supported versions

Only the latest release receives fixes.

| Version | Supported |
|---|---|
| Latest release (see [Releases](https://github.com/JJWren/CalCrony/releases)) | ✅ |
| Anything older | ❌ — upgrade first |

## Reporting a vulnerability

**Please do not open a public issue for security problems.** Instead, use either channel:

- **GitHub private vulnerability reporting** (preferred): [Report a vulnerability](https://github.com/JJWren/CalCrony/security/advisories/new)
- **Email**: joshua.j.wren@gmail.com — include "CalCrony security" in the subject

Include what you can: affected endpoint/command, reproduction steps, impact, and any suggested fix. Proof-of-concept detail is welcome; please don't test against instances you don't operate.

## What to expect

- Acknowledgement within **7 days** (this is a solo-maintained project).
- An assessment of severity and, for confirmed issues, a fix targeted at the next release with credit in the release notes if you'd like it.
- No bug bounty is offered.

## Scope notes

- Secrets are expected to arrive via environment variables; anything that causes secret material (API keys, OAuth tokens, Data Protection keys) to be logged, returned, or stored unencrypted is in scope and high priority.
- The anonymous surfaces (`/health`, `/feeds/{token}.ics`, `/oauth/*`) and the API-key boundary are the most interesting attack surface — reports there are especially appreciated.
- Vulnerabilities in dependencies (Discord.Net, Npgsql, etc.) should go upstream, but a report here is still useful if CalCrony's usage makes them exploitable.
