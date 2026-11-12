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

**Community Server**:
The public CalCrony Discord guild — support, announcements, and a playground for trying the bot. To the product it is an ordinary guild: the production bot serves it exactly like any other server it is installed in.
_Avoid_: main server, home guild

**Test Guild**:
The private Discord guild used during development, where the bot registers slash commands guild-scoped (instant) instead of globally. Never the Community Server — pointing test registration at the public guild would leak test commands to users.
_Avoid_: dev server, staging guild

**Free Slot**:
A stretch of time in which every Counted Member is inside their Available Hours with nothing on a linked calendar. Computed live for one reply and never stored; it belongs to the caller's calendar day it starts on.
_Avoid_: opening, gap, free block

**Counted Member**:
A member whose availability actually shaped a free-slots answer. Everyone else who was asked about is accounted for in the reply under the reason they were left out — by name, or in a count once a list grows long.
_Avoid_: included member, participant

**Available Hours**:
The weekly wall-clock hours, read in a member's own timezone, inside which a Free Slot may fall — 8 AM–11 PM every day by default. What a calendar cannot say: an empty one reads as free at 3 AM.
_Avoid_: working hours, office hours, schedule
