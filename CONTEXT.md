# CalCrony

A self-hosted Discord event & calendar suite: an API that owns all domain logic, with the Discord bot and the web app as its two clients. The API stores Discord snowflakes as opaque IDs and never calls Discord itself.

## Language

**Name Snapshot**:
A Discord display name (guild or channel) copied into CalCrony's database by the bot at gateway events. The only way the API ever knows what anything in Discord is called.
_Avoid_: cache, lookup, live name

**Login Snapshot**:
The per-user guild list captured at web login from that user's Discord OAuth. A fact about what one user's Discord sees — never a guild-level source of truth.
_Avoid_: guild name (for this data), membership name

**Metadata Block**:
The machine-added context (channel, event links) rendered below the user's own text in a feed event's description. The user's words always come first.
_Avoid_: footer, signature

**Rail**:
The collapsed desktop-sidebar state: a narrow icon-only column. Everything in it must fit the column — labels hide, the brand condenses to the d20 mark alone.
_Avoid_: mini sidebar, collapsed nav

**Live Occurrence**:
The single Scheduled-or-Started event that currently represents a recurring series. Its identity (event id, Discord message) rotates every cycle, so nothing durable may point at it.
_Avoid_: current event, active event
