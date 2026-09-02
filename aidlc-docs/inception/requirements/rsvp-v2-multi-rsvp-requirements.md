# RSVP v2 §3.3 — multiple RSVPs per user: requirements analysis

**Issue**: #125 (the last unticked box). **Depth**: comprehensive — the change drops a unique index and
touches the seat/waitlist/role model. **Date**: 2026-09-02. **Baseline**: `master` b75a67b (v0.34.0),
703 tests green. **Status**: Q1–Q5 answered 2026-09-02 (all recommended options); approved.

## 1. Intent

Let a member hold an RSVP on more than one option of the same event: sign up as Tank *and* Healer
("I can do either"), or pick Dinner *and* Movie at a multi-activity night. Parity item from the
sesh.fyi gap analysis (`docs/research/sesh-fyi-feature-gap.md` §3.3). Sesh sells it as a premium
per-event switch ("Enable attendees to select multiple RSVP options for a single event") set on the
event form; its manual says nothing about how it meets waitlists, roles, or the attendee list.
CalCrony has no premium tier, so it ships free for every server.

## 2. What assumes one RSVP per user today (verified against the code)

| Site | Assumption | Under multi-RSVP |
|---|---|---|
| `CalCronyDbContext.cs:162` | `HasIndex(EventId, UserId).IsUnique()` — the hard constraint ("multi-select is a later premium-parity feature") | Replaced by a unique `(EventId, UserId, OptionId)` index |
| `EventEndpoints.PutRsvp` (~1379) | `existing = ev.Rsvps.FirstOrDefault(r => r.UserId == userId)`; a PUT **moves** that row (switch), re-click is a no-op; `seatBefore/seatAfter` is one seat; leaving the attending seat promotes | A PUT **adds** a seat and touches no other seat; the role decision becomes a set difference |
| `EventEndpoints.DeleteRsvp` (~1545) | Clears the user's single row; no option in the route | Needs an option-scoped removal; clearing everything stays possible |
| `EventEndpoints.SeatedRoles` (125) | `Dictionary<long, long>`: user → *one* role; the edit path diffs before/after | user → *set* of roles; grants/revokes are set differences |
| `AttendeeRoleSync.Decide(options, old, new)` | One seat → one role | Per-seat call must know whether another held seat still carries the same role |
| `RsvpPolicy.SeatedCount / CapacityBelowSeated / PromoteAsync / SeatWaitlist / TryApplyOptionEdit` | Count **rows** per option | Already multi-safe: a row is a seat |
| Bot `RsvpComponentModule` | Click your option = clear, click another = switch; confirmations; DM-reminder offer on the first attending seat | Multi: click = toggle that option; confirmations list what else the member holds |
| Bot `EventEmbedBuilder` (100–140) | Per-option member columns + one waitlist column | Multi-safe (a member appears in several columns); wants a meta line so clicks read as "add" |
| Web `RsvpButtons.razor` | `MyOptionId` single; one `selected` button; click held = delete | Set of held options; several `selected`; toggles |
| Web `EventDetail.razor` (279–315) | Per-option columns | Multi-safe |
| Web `EventForm.razor` (77–108) | No multi control | Checkbox |
| `Mapping.cs:36`, `RsvpDto` | One DTO per row | Multi-safe (more rows) |
| `CsvExport`, `ActionLogEndpoints` (316) | One CSV row per RSVP, keyset on `(EventId, Id)` | Multi-safe; README already says "one row per RSVP" |
| `DmReminderFanOut.cs:60`, `DmReminderEndpoints.cs:126` | Attending seated rows | Multi-safe (at most one row per user on the attending option) |
| `CalendarEndpoints.cs:218`, `AvailabilityModule.cs:84` | Attending seated users (`Distinct`) | Multi-safe |
| `LiveListEmbedBuilder.cs:36` | "N going" = attending seated count | Multi-safe |
| `EventThreadSync` | Add-only on the attending seat | Multi-safe |
| `RoleRestrictionGate` at PutRsvp, `RsvpRequest.CheckedRoleIds` | Entry-only, **per option** | Multi-safe |
| `EventSeries` (`WantsThread`, `AttendeeRoleId`, `RsvpOptionsJson`) | Per-series template fields with the Series-scope edit rule | The new flag needs the same treatment |
| `EventTemplate` | Carries no RSVP settings | The flag does not go there either |
| `ActionLog` EventEdited detail | Lists role/limit/restriction changes | Mention the flag change |
| README / web Docs | "switching revokes the previous choice" | Reword; document the switch |

Tests that encode the single-choice rule: `RsvpV1ApiTests`, `PerOptionRoleApiTests`, `AttendeeRoleApiTests`,
`RsvpPolicyTests`, `RsvpPromotionQueryCountTests`, bot `EventEmbedBuilderTests`, web RSVP button tests.

## 3. Functional requirements (draft — final shape follows Q1–Q5)

- **FR1** Opt-in per event, default off. Every existing event and every server keeps today's behaviour.
- **FR2** In multi mode a PUT for an option the member does not hold adds a seat (or a waitlist entry on
  the full attending option) without touching their other seats. A PUT for a held option stays the
  no-op it is today (no queue-position move).
- **FR3** Removal is per option. Clearing all of a member's RSVPs remains one call.
- **FR4** Capacity counts seats (rows), never people. The attending option's waitlist and promotion are
  unchanged.
- **FR5** Roles: a member holds the union of their seated options' roles. Adding or removing a seat
  grants or revokes only a role whose held-count crosses zero (Tank and Healer both granting `@raider`:
  dropping Tank keeps it). The edit-path diff generalizes from one role per user to a set.
- **FR6** Attending semantics (threads, availability, DM reminders, live-list count, waitlist) keep
  keying on the attending option's seat.
- **FR7** Configurable from Discord (`/create`, `/edit`) and the web form; a series template field
  (Series-scope edits, spawned occurrences inherit).
- **FR8** Bot: toggle semantics on the buttons; confirmations name the seat taken/left and the
  member's other seats; an embed meta line says clicks add rather than switch.
- **FR9** Web: buttons toggle, several can be selected, helper text explains.
- **FR10** Turning multi off while members hold several seats: per Q4.
- **FR11** Migration swaps the unique index; Down must collapse extra rows before restoring the old
  index and enqueue a revoke for each role a discarded seat carried that the kept seat does not
  (recorded in the PR body). Rolling the image back is safe only while no member holds more than one
  row — 0.34.0's PutRsvp moves a member's first row onto the clicked option, which collides with
  their other row when they already hold it (found in review, PR #154); once multi rows exist, run
  Down first.
- **FR12** CSV export unchanged in shape (more rows); README, Docs page updated; PRIVACY unaffected.

## 4. Design questions

Answer with the letter; a recommendation is marked.

**Q1 — Where does the switch live?**

- A) Per event, opt-in, default off: `/create multi-rsvp:true`, `/edit multi-rsvp:`, a web checkbox;
  a series template field. Existing events unchanged. **(Recommended — sesh's model; zero behaviour
  change for existing servers.)**
- B) Per server default (`/settings multi-rsvp on`) with a per-event override.
- C) Always on for every event: buttons become toggles everywhere. Breaks the "click another to switch"
  habit on every server.
- D) Per option: a marker on options that may be combined ("add-on" options); unmarked options stay
  mutually exclusive.
- E) Other (describe).

[Answer]: **A**

**Q2 — What does "attending" mean when a member holds several seats?**

- A) Unchanged: the one flagged option decides who is going (threads, availability, DM reminders,
  "N going", the waitlist). Other seats are extra answers. **(Recommended — nothing downstream of
  `AttendingOption()` moves. The "a Healer-only signup isn't going" gap is pre-existing since §3.6 and
  orthogonal to this feature.)**
- B) Several options may carry the attending flag (radio → checkboxes): attending = seated on any
  flagged option. Widens `AttendingOption()` at ~15 sites and needs a rule for which flagged option
  the `attendee-limit` / `attendee-role` shorthands mean.
- C) In multi mode, attending = seated on any option at all.
- D) A now; B filed as its own follow-up issue.
- E) Other (describe).

[Answer]: **A**

**Q3 — Exclusive options (the "❌ Can't make it" problem)?** With independent options a member who is
Going and clicks "Not going" would hold both; the seat is not freed.

- A) No special rule: every option is independent; the docs advise designing option sets that combine
  sensibly. **(Recommended for this PR.)**
- B) An exclusive marker (e.g. a trailing `!` in `rsvp-options`, a checkbox on the web): picking it
  clears the member's other seats, picking anything else clears it.
- C) Multi mode refuses the default Going / Not going / Maybe set — custom options required.
- D) A now; B as a follow-up.
- E) Other (describe).

[Answer]: **A**

**Q4 — Turning multi off while members already hold several seats?**

- A) Refuse with 409 naming the count ("3 members hold more than one RSVP — keep it on, or ask them to
  pick one"). Same posture as "capacity can't go below seated" and "an option with RSVPs can't be
  removed". **(Recommended.)**
- B) Collapse silently to each member's earliest RSVP (role revokes and waitlist effects ride the outbox).
- C) Collapse to the attending seat when held, else the earliest.
- D) Allow the switch and leave existing multi-holders as they are; they can only remove seats until
  they hold one.
- E) Other (describe).

[Answer]: **A**

**Q5 — Capacity on non-attending options?** Today only the attending option queues; a full "Maybe"
refuses outright ("nothing to wait for on a decline"). Under multi, non-attending options become real
choices ("🎬 Movie x10").

- A) Unchanged: only the attending option has a waitlist; a full non-attending option still refuses
  ("Healer is full"). **(Recommended — keeps promotion, pings, and the waitlist column single-queue.)**
- B) Per-option waitlists: every capped option queues and promotes; the waitlist column, promotion
  pings, `EventDto.Waitlist`, and the position text become per option. Roughly doubles the PR.
- C) A now; B filed as its own issue.
- D) Other (describe).

[Answer]: **A**

## 5. Assumptions (accepted unless Joshua says otherwise)

- Name: `AllowMultipleRsvps` on `Event`, `EventSeries`, `CreateEventRequest`, `UpdateEventRequest`
  (`bool?`, null = unchanged) and `EventDto`. Bot option `multi-rsvp` on `/create` and `/edit`; web
  checkbox "Allow more than one RSVP per member".
- The web may set it: unlike roles and restrictions it hands out nothing, so it is not a security
  boundary and needs no strip/carry-over.
- Removal route: `DELETE /events/{id}/rsvps/{userId}/options/{optionId}`; the existing bare DELETE
  keeps clearing every RSVP the member holds (single mode's one row is the degenerate case).
- Unique index becomes `(EventId, UserId, OptionId)`; single mode is enforced by `PutRsvp` under the
  event's FOR UPDATE lock, as capacity is today.
- Roles: `SeatedRoles` returns user → role set; PutRsvp/DeleteRsvp compute the same set difference for
  one user, so one rule covers both paths (grant when a role's held-count goes 0→1, revoke on 1→0).
  Rapid-toggle coalescing in `AttendeeRoleSync` is reused unchanged.
- Attending waitlist unchanged: a PUT on the full attending option waitlists the member even when they
  are seated elsewhere.
- Embed: one meta line in multi mode ("☑️ Pick every option that applies"); bot confirmations read
  "Added 🍕 Dinner to your RSVPs for **X** (you're also marked: 🎬 Movie)" / "Removed …".
- Series: `EventSeries.AllowMultipleRsvps` copied to spawned occurrences; a Series-scope edit updates
  the template only when the request carried the flag (the role/limit rule).
- Migration Down keeps each `(EventId, UserId)`'s earliest row before restoring the old unique index;
  Up and Down executed against `postgres:17-alpine` over seeded multi rows and recorded in the PR body.
- Polls untouched (they already support multi-vote via `SingleVote = false`).
- `CheckedRoleIds` and the restriction gate untouched (both are already per option).
- Action log: EventEdited detail says "multiple RSVPs on/off" where it lists limit/role changes.

## 6. Out of scope (candidates for follow-up issues)

- Multiple attending options (Q2 B) — a pre-existing §3.6 gap.
- Per-option waitlists (Q5 B).
- Exclusive options (Q3 B).

## 7. Risks

- **Not additive**: the migration swaps a unique index. Rolling the prod image back to 0.34.0 is safe
  only while no member holds more than one row (old code ignores the columns and never inserts a second
  row, but its PutRsvp would move a member's first row onto an option they already hold and hit the new
  index — found in review, PR #154); once multi rows exist, run Down first — it collapses members' extra
  seats to the earliest and enqueues the revokes for roles those seats carried. Say so in the PR body
  and the rollout note.
- Slightly more outbox traffic on toggle-happy events (each seat change is its own role delivery);
  coalescing already nets never-served pairs to zero.
- Larger embeds (a member can appear in every column); the existing per-list budget bounds it.
