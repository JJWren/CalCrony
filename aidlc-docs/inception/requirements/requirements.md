# CalCrony Requirements

## Intent

A self-hosted clone of sesh.fyi for Joshua's personal Discord servers, built in .NET. The backend is an API; the Discord bot is a client of that API authenticating with an API key. Personal scale (a handful of guilds), not multi-tenant SaaS.

## Decisions

| Decision | Choice |
|---|---|
| v1 core | Events + RSVPs + reminders/notifications + ICS feed export |
| Spine (assumed) | Timezone handling (NodaTime), natural-language datetime parsing |
| Bot library | Discord.Net (3.20.x) |
| Database | PostgreSQL via EF Core/Npgsql |
| Runtime | .NET 9 |
| Calendar sync (v1) | ICS subscribe URL only; Google OAuth sync deferred |
| Extras roadmap | Polls/time polls → recurring events → web dashboard |

## Functional requirements (v1)

1. **Events**: create/edit/delete/list events per guild with title, natural-language datetime, description, duration, location, image, target channel. Creator/admin-only edit and delete.
2. **RSVPs**: button-based RSVP (✅/❌/🤔 defaults), live embed updates, per-option model extensible to custom options and capacities later.
3. **Reminders/notifications**: one-off `/remind`; up to 5 scheduled notifications per event plus event-start ping; delivered via outbox the bot polls.
4. **ICS feed**: per-guild tokenized ICS URL subscribable from external calendar apps.
5. **Timezones**: per-guild and per-user timezone settings; all storage UTC (NodaTime Instant + IANA tz id).
6. **Auth**: every API request (except health and feeds) requires `X-Api-Key`; keys stored as SHA-256 hashes; bootstrap key seeded from configuration.

## Non-functional requirements

- Personal scale: single API instance, polling outbox (~15s) acceptable.
- Crash-safe scheduled sends (outbox rows acked only after Discord post succeeds).
- All times DST-correct via NodaTime; recurrence-ready data model (`SeriesId` reserved).
- Runs via docker compose (postgres + api + bot).

## Deferred (recorded, not forgotten)

Google Calendar push/two-way sync; poll→event conversion; dashboard auth (Discord OAuth2); native-event mirroring; event threads; attendee roles; templates.
