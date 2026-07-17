# CalCrony

A self-hosted event & calendar bot for Discord, inspired by [sesh.fyi](https://sesh.fyi/), built in .NET.

**Architecture:** the backend is an API (`CalCrony.Api`); the Discord bot (`CalCrony.Bot`) is a client of that API, authenticating with an API key. The API owns all domain logic, persistence (PostgreSQL), scheduling, and ICS generation — it knows nothing about Discord.Net.

## Core features (v1)

- Events with natural-language datetimes ("tomorrow 6pm"), RSVPs via buttons, timezone-aware throughout (NodaTime)
- Reminders and per-event scheduled notifications, delivered through an outbox the bot polls
- ICS calendar feed per server (`/link`) — subscribe from Google/Apple/Outlook calendars

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

## Contributing & releases

All changes flow through PRs on GitHub Flow branches with conventional-commit titles; releases are cut automatically by release-please and published as GHCR images. See [CONTRIBUTING.md](CONTRIBUTING.md) for the branching strategy, PR cycle, versioning rules, and issue conventions.
