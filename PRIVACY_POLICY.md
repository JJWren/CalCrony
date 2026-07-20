# CalCrony — Privacy Policy

**Last updated: 2026-07-20**

This policy describes what the **CalCrony** instance operated by this repository's maintainer
stores and why. CalCrony is open-source and self-hostable; a self-hosted instance is governed by
whoever runs it, not by this document.

The short version: CalCrony stores the scheduling data you give it, plus the Discord identifiers
needed to make that work. It has no analytics, no ads, no tracking, and sells nothing.

## 1. What we store

**From Discord** (via the bot and, if you sign in, the web app):

- Discord snowflake IDs: your user ID, server (guild) IDs, channel/message/thread/role IDs the
  features touch. CalCrony never sees your Discord password or token.
- Content you create: event titles/descriptions/locations, poll questions and options, RSVP
  choices, reminders, notification messages, and templates.
- Preferences: per-user and per-server timezone, DM-confirmation setting, server settings.
- Web sign-in (optional, Discord OAuth with `identify` + `guilds` scopes only): your username,
  display name, avatar hash, and a snapshot of which servers you're in (used to scope what the
  web app shows you). Session security data: hashed refresh tokens and short-lived login-state
  records.

**From Google** (optional, only if you link a calendar): an encrypted OAuth token with the
read-only **free/busy** scope. CalCrony never sees event titles, details, or attendees from your
Google calendar — only busy/free time blocks, fetched on demand and not stored.

**Message content**: CalCrony only processes the slash-command inputs and component interactions
sent to it. It does not read your server's messages.

## 2. What we don't do

- No sale, rental, or sharing of data with third parties.
- No advertising, analytics, or cross-service tracking.
- No reading of Google calendar event details (the free/busy scope makes that impossible).

Data goes to exactly two external services, both at your direction: **Discord** (to run the bot)
and **Google** (only for the free/busy lookups you enable).

## 3. Security

- Google OAuth tokens are encrypted at rest (ASP.NET Core Data Protection).
- API keys and web refresh tokens are stored as SHA-256 hashes, never in plain text.
- The web session uses short-lived tokens with rotate-on-use refresh cookies.

## 4. Retention & deletion

- **Scheduling content** (events, polls, templates, RSVPs) lives until you or a server manager
  delete it, or until the bot is removed and the operator prunes the server's data on request.
- **Operational records** (delivered notifications, consumed login/link tokens, expired session
  tokens) are automatically purged after **90 days**.
- **Google connection**: disconnecting (`/calendar disconnect` or the web app) deletes the
  stored tokens immediately.
- **Removal requests**: to have a server's or your own data deleted, open a
  [GitHub issue](https://github.com/JJWren/CalCrony/issues) or contact the operator; deletion is
  manual but honored.

## 5. Children

CalCrony is used through Discord and follows Discord's age requirements (13+, or higher where
local law says so). It is not directed at children.

## 6. Changes

Updates are committed to this repository with the date above refreshed; the
[commit history](https://github.com/JJWren/CalCrony/commits/master/PRIVACY_POLICY.md) is the
change log.

## 7. Contact

Privacy questions or deletion requests: open a
[GitHub issue](https://github.com/JJWren/CalCrony/issues) on this repository.
