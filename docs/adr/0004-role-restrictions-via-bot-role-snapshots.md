# Role restrictions read bot-written role snapshots, and fail closed on the web

RSVP v2 §3.5 (issue #125) lets an organizer limit who may pick an RSVP option or vote in a poll,
by Discord role. Enforcing that needs an answer to "does this user hold @Tank in guild X?" at the
moment of the click — and the API cannot answer it. OAuth scopes are `identify guilds` only, and
`UserGuildMembership` stores one derived bit, `CanManage`.

We keep ADR 0001's invariant that the API never calls Discord: the bot, which already runs the
`GuildMembers` privileged intent, writes role snapshots into the API, and the API reads them.

Two tables, both following the `Channels` model — rows exist only for what CalCrony references:

- **`GuildRoles`** — name snapshots for *watched* roles only (a role named by at least one live
  restriction). Gives the web real role names instead of the raw `#123456` the §3.6 chip prints
  today, and its absence is how a deleted role is recognised.
- **`GuildMemberRoles`** — for each member holding at least one watched role, which ones. Members
  holding none of them have no row at all, so the table stays proportional to restrictions in use,
  not to server size.

`Guild.RolesSyncedAt` plus the watched set recorded on `GuildRoles` is what makes an *absent* row
readable as "holds none of them" rather than "we have never looked". Without that marker the two
are indistinguishable, and a missing row would silently read as a denial.

## Considered Options

- **Bot snapshots (chosen).** The bot pushes on `GuildMemberUpdated`, immediately after any
  `/create` or `/edit` that names a role not yet watched (restrictions are configured bot-side, so
  the bot always knows when the watched set grows), and reconciles at Ready. Staleness is seconds
  while the bot runs.
- **Live REST lookups from the API.** Rejected for ADR 0001's reasons, and more sharply here: this
  is a synchronous check on the RSVP path, so Discord's rate limits and latency would sit in front
  of every click, and a Discord outage would take RSVPs down rather than degrade them.
- **The `guilds.members.read` OAuth scope.** Rejected: new consent, granted per guild, and it
  answers only for web callers — Discord RSVPs would still need a second mechanism.

## Consequences

**Enforcement is split by caller, and only the web depends on a snapshot.** The bot checks the
member's roles live from its socket cache before calling the API, so a Discord RSVP is correct even
when snapshots lag. The API trusts a bot-authenticated call and checks the snapshot for a web
caller. Snapshots therefore exist to serve the web, and nothing else.

**The web fails closed.** A web caller whose roles cannot be confirmed — no sync marker for the
guild, or the role is not in the watched set — is refused with "we can't confirm your roles right
now; RSVP from Discord", not admitted. This is the deliberate inverse of ADR 0001's rule for names:
a missing name is omitted and the page still renders, but a missing *authorization* fact cannot be
waved through. The cost is that a long bot outage blocks restricted RSVPs on the web while leaving
them working in Discord, which is the correct direction to fail.

**A deleted role is ignored, not enforced.** A restriction naming a role with no `GuildRoles` row
after a sync is treated as vacuous rather than unsatisfiable, so deleting a role cannot silently
lock a server out of its own events. A restriction whose roles have all been deleted is therefore
no restriction at all.

**The restriction gates entry only.** Losing the role afterwards never revokes a seat. There is no
sweep, no delivery type, and no background reconciliation of past RSVPs against present roles.

**This is the first per-user Discord fact the API stores that the user did not hand over.** Name
snapshots covered guilds and channels; `UserGuildMembership` comes from the user's own OAuth login.
Role membership is written *about* someone by the bot, so it needs disclosure in the privacy policy
alongside the name snapshots, and rows must be dropped when the restrictions referencing a role go
away, when the role is deleted, and when the bot leaves the guild.
