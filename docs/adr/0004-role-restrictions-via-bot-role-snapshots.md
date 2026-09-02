# Role restrictions read bot-written role snapshots, and fail closed on the web

RSVP v2 §3.5 (issue #125) lets an organizer limit who may pick an RSVP option or vote in a poll,
by Discord role. Enforcing that needs an answer to "does this user hold @Tank in guild X?" at the
moment of the click — and the API cannot answer it. OAuth scopes are `identify guilds` only, and
`UserGuildMembership` stores one derived bit, `CanManage`.

We keep ADR 0001's invariant that the API never calls Discord: the bot, which already runs the
`GuildMembers` privileged intent, writes role snapshots into the API, and the API reads them.

Two tables, both following the `Channels` model — rows exist only for what CalCrony references:

- **`GuildRoles`** — name snapshots for *watched* roles only (a role named by at least one live
  restriction, or — since #167 — granted as an attendee role by a live event or running series).
  Gives the web real role names instead of the raw `#123456` the §3.6 chip printed before. A row with no name is the bot's tombstone for a role it checked and found deleted; no
  row at all means the role has not been checked since it became watched (see the refinement
  below — the two must not be confused).
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
guild, a marker older than its 30-minute lease, or a role with no checked row — is refused with "we
can't confirm your roles right now; RSVP from Discord", not admitted. This is the deliberate
inverse of ADR 0001's rule for names: a missing name is omitted and the page still renders, but a
missing *authorization* fact cannot be waved through. The bot renews the marker by reconciling
every watched guild every 10 minutes while it runs, so an expired marker means the bot has been
gone long enough that member changes may have been missed. The cost is that a long bot outage
blocks restricted RSVPs on the web while leaving them working in Discord, which is the correct
direction to fail.

**A deleted role is ignored, not enforced.** A restriction naming a role the bot has checked and
found gone (a `GuildRoles` row with no name) is treated as vacuous rather than unsatisfiable, so
deleting a role cannot silently lock a server out of its own events. A restriction whose roles have
all been deleted is therefore no restriction at all.

**The restriction gates entry only.** Losing the role afterwards never revokes a seat. There is no
sweep, no delivery type, and no background reconciliation of past RSVPs against present roles.

**This is the first per-user Discord fact the API stores that the user did not hand over.** Name
snapshots covered guilds and channels; `UserGuildMembership` comes from the user's own OAuth login.
Role membership is written *about* someone by the bot, so it needs disclosure in the privacy policy
alongside the name snapshots. A guild's rows must go when its last watched role — a restriction
or, since #167, a granted attendee role — ends and when the bot leaves; while any remain, the rows
are trimmed to the roles still named or granted, and a role deleted in Discord keeps only its
nameless tombstone (see below) with no member associations.

## Refinements made during implementation (PR #150)

**Tombstones, not absence.** The design pass read a role's *absence* from `GuildRoles` after a
sync as "deleted, therefore vacuous". That cannot be told apart from "restricted after the last
sync and not yet checked", and reading the latter as vacuous would admit web callers without the
role until the next sync — the fail-open this ADR rejects. So the bot reports every watched role it
was asked about, and a role it finds gone is stored as a row with a null name. A named row is
known, a tombstone is deleted (vacuous), and no row is unchecked (unverifiable). The bot also
re-syncs a guild when a watched role is deleted or renamed, so a deletion becomes vacuous within
seconds rather than at the next reconcile.

**A bounded lease on the sync marker.** "Staleness is seconds while the bot runs" only holds while
it runs. Without a bound, a bot that has been down for a day would still answer for members who
lost a role in the meantime. The marker therefore expires 30 minutes after the last sync; the bot's
periodic reconcile keeps a live bot well inside it, and retention trims each guild's rows to the
roles its live restrictions still name.

**Attendee roles are watched too (#167).** The first cut watched restriction roles only, so an
event that merely *granted* a role kept printing `role #123456` on the web — the snapshot is the
web's only source of role names. Attendee roles on live events and running series templates now
join the watched set (`RoleWatchList.NamedBy`), the API invalidates a newly granted role's leftover
rows the way it does a newly restricted one, and the bot's immediate post-`/create` and `/edit`
sync fires for a named attendee role as well. The cost is a wider per-user fact: `GuildMemberRoles`
now also records who holds a granted role — including members who hold it independently of
CalCrony — so the privacy policy's role-snapshot bullet names granted roles alongside restricted
ones, and the same retention rules (last watched role ends, bot leaves, trim to the roles still
named or granted) apply. The write load is nothing new: the bot grants attendee roles itself, and
each grant is the existing per-member push.
