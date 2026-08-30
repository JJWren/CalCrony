# Discord names enter only as bot-written snapshots

The API deliberately knows nothing about Discord — it stores snowflakes as opaque IDs and never
calls Discord itself. When the ICS feed needed guild and channel *names* (calendar title, event
descriptions), we chose to keep that invariant: the bot writes name snapshots into the API
(`Guild.Name` via the presence machinery plus a `GuildUpdated` handler; a `Channels` table covering
only channels CalCrony references, written at the embed post sites, on `ChannelUpdated`, and by a
Ready-time reconcile) rather than having the API fetch names from Discord's REST API at
feed-generation time.

## Considered Options

- **Bot snapshots (chosen).** Staleness window is seconds while the bot runs and self-heals at the
  next Ready. Nullable columns; consumers must degrade gracefully (omit the name, never render
  placeholders or raw snowflakes).
- **Live REST lookups from the API.** Rejected: it would be the first crack in the "API never talks
  to Discord" boundary, add Discord rate limits and latency to an anonymous endpoint — and buy
  freshness nobody can observe, because Google Calendar caches URL subscriptions for 12–24 hours,
  hiding any snapshot staleness entirely.
- **Reusing `UserGuildMembership.GuildName` login snapshots.** Rejected as a primary source: those
  are per-user facts ("what this user's OAuth saw"), absent for servers nobody has web-logged into.

## Consequences

A `Guild.Name` or `Channels` row can be null/missing (pre-migration data, bot never resynced,
deleted channel) — that is expected, not a bug to "fix" with live lookups. A kicked guild's names
freeze at their last-known values, which is the desired reading for historical calendar entries.
