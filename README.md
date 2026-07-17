# CalCrony

A self-hosted event & calendar bot for Discord, inspired by [sesh.fyi](https://sesh.fyi/), built in .NET.

**Architecture:** the backend is an API (`CalCrony.Api`); the Discord bot (`CalCrony.Bot`) is a client of that API, authenticating with an API key. The API owns all domain logic, persistence (PostgreSQL), scheduling, and ICS generation — it knows nothing about Discord.Net.

## Core features (v1)

- Events with natural-language datetimes ("tomorrow 6pm"), RSVPs via buttons, timezone-aware throughout (NodaTime)
- Reminders and per-event scheduled notifications, delivered through an outbox the bot polls
- ICS calendar feed per server (`/link`) — subscribe from Google/Apple/Outlook calendars
- Google Calendar availability: `/calendar connect` links a member's Google Calendar (least-privilege free/busy OAuth scope); `/availability role` or `/availability event` shows an on-demand, Teams-Scheduling-Assistant-style grid of who's free/busy — read-only, never blocks event creation or RSVPing

## Layout

```
src/
  CalCrony.Api/        ASP.NET Core: endpoints, EF Core, scheduler, ICS
  CalCrony.Bot/        Discord.Net worker: slash commands, RSVP buttons, delivery poller
  CalCrony.Contracts/  DTOs shared across the wire
tests/
  CalCrony.Api.Tests/
  CalCrony.Bot.Tests/
```

## Running

```
docker compose up   # postgres + api + bot (bot requires DISCORD_BOT_TOKEN)
```

Google Calendar availability additionally requires `GOOGLE_OAUTH_CLIENT_ID`/`GOOGLE_OAUTH_CLIENT_SECRET` (from a Google Cloud OAuth 2.0 Web Client) and `CALCRONY_PUBLIC_BASE_URL` set to a real, publicly-reachable HTTPS URL for the API — Google's consent screen redirects back to `{CALCRONY_PUBLIC_BASE_URL}/oauth/google/callback`, which must be registered as an authorized redirect URI on the OAuth client. Without these set, `/calendar connect` fails gracefully; every other feature is unaffected.

## Contributing & releases

All changes flow through PRs on GitHub Flow branches with conventional-commit titles; releases are cut automatically by release-please and published as GHCR images. See [CONTRIBUTING.md](CONTRIBUTING.md) for the branching strategy, PR cycle, versioning rules, and issue conventions.
