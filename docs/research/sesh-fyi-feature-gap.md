# Feature gap analysis: sesh.fyi vs CalCrony

*Research date: 2026-08-31. Method: CalCrony features inventoried from this repo's source and README; sesh features confirmed only on sesh-owned pages (home page, manual, premium page, Time Finder page, official roadmap). Third-party sources were not used as evidence.*

**Sesh sources (first-party):**

- Home: <https://sesh.fyi/>
- Manual: <https://sesh.fyi/manual/>
- Premium / pricing: <https://sesh.fyi/premium>
- Time Finder: <https://sesh.fyi/time-finder>
- Roadmap (planned / in-progress / shipped): <https://roadmap.sesh.fyi>

**Sesh pricing context** (<https://sesh.fyi/premium>): Premium $6.99/mo (1 server), Premium Bundle $12.99/mo (3 servers), Sesh Pro $24.99/mo (adds API access, webhooks, custom branding, business license). CalCrony is MIT-licensed and self-hosted (`README.md`) — every CalCrony feature is "free", so sesh's free-tier limits (3 favorites/server, 24h server logs, basic GCal, limited AI usage) mostly don't map onto CalCrony.

---

## 1. Where CalCrony already has parity (no gap)

| Sesh feature | Sesh source | CalCrony equivalent |
|---|---|---|
| `/create` with natural-language datetime, description, duration, channel, location, image | <https://sesh.fyi/manual/> | `src/CalCrony.Bot/Modules/EventModule.cs`; `README.md` command table (same option set) |
| RSVP buttons with live attendee lists | <https://sesh.fyi/> ("Simple RSVP functionality") | `src/CalCrony.Bot/Modules/RsvpComponentModule.cs`, `src/CalCrony.Bot/EventEmbedBuilder.cs` |
| `/list`, `/delete`, edit; list filtering by channel | <https://sesh.fyi/manual/>; <https://roadmap.sesh.fyi> ("List filtering by channel") | `EventModule.cs` (`/list [channel] [limit]`, `/edit`, `/delete`) |
| `/remind` one-off reminders | <https://sesh.fyi/manual/> | `src/CalCrony.Bot/Modules/ReminderModule.cs` |
| Event notifications: reminders to any channel, custom timing + mentions (sesh premium) | <https://sesh.fyi/manual/> ("Send reminders to any channel") | `src/CalCrony.Bot/Modules/NotifyModule.cs` (`/notify`, up to 5 per event, mention + channel options) — free in CalCrony |
| `/timestamp` Discord timestamp generator | <https://sesh.fyi/manual/> | `src/CalCrony.Bot/Modules/TimestampModule.cs` |
| `/link` ICS calendar feed | <https://sesh.fyi/manual/> | `src/CalCrony.Bot/Modules/LinkModule.cs`; ICS generation in `CalCrony.Api` (README: RRULE included in feed) |
| Polls: single/multi vote, anonymous, user-added options, live bar-graph results | <https://sesh.fyi/> (availability polling section) | `src/CalCrony.Bot/Modules/PollModule.cs`, `PollComponentModule.cs`, `PollEmbedBuilder.cs` |
| Time polls (vote to pick the best time) | <https://sesh.fyi/> ("Poll support (vote to pick the best time)") | `PollModule.cs` `/poll time`; plus `/poll convert` turns the winner into an event |
| Event templates (sesh premium; unlimited) | <https://sesh.fyi/premium>, <https://roadmap.sesh.fyi> | `src/CalCrony.Bot/Modules/TemplateModule.cs` (25/server cap) — free in CalCrony |
| Attendee roles (sesh premium) | <https://sesh.fyi/premium> ("Attendee roles with access to private channels") | `src/CalCrony.Bot/AttendeeRoleManager.cs` (`/create attendee-role:`) — free in CalCrony, but see §3 |
| Event threads (sesh premium: "a Discord thread for every event") | <https://sesh.fyi/manual/> | `src/CalCrony.Bot/EventThreadManager.cs` (`/create thread:true`) — free in CalCrony |
| Discord native events mirroring | <https://sesh.fyi/manual/> ("Mirror sesh events to Discord's native events") | `src/CalCrony.Bot/NativeEventMirror.cs` (`/settings native-events`) |
| Personal + server timezone settings, timezone-aware parsing | <https://sesh.fyi/> ("Handles timezones effortlessly"); manual "User Settings" | `src/CalCrony.Bot/Modules/SettingsModule.cs`, NodaTime throughout (README) |
| Web interface for events/polls/settings | <https://sesh.fyi/> ("Fully optional web interface"), `/dashboard` | `src/CalCrony.Web/` (Blazor WASM: event/poll CRUD, RSVP, settings, templates, availability) |
| Repeating events with rolling next occurrence (sesh premium) | <https://sesh.fyi/premium> | `src/CalCrony.Bot/Modules/SeriesModule.cs`, `src/CalCrony.Contracts/Series.cs` — free in CalCrony, but see §4 for interval gaps |

---

## 2. Gaps — event creation UX

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 2.1 | **`/ai` GPT-powered command** — run sesh commands with natural language (free tier gets "limited AI command usage") | <https://sesh.fyi/manual/>; <https://sesh.fyi/premium> | **Missing.** CalCrony has natural-language *datetimes* (`README.md`, parser tests in `tests/CalCrony.Api.Tests`) but no natural-language *command* interface. |
| 2.2 | **`on_create_mentions`** — mention roles/users when an event is created | <https://sesh.fyi/manual/> (`/create` parameters) | **Missing.** `/create` (`EventModule.cs`, `src/CalCrony.Contracts/Events.cs`) has no mention option; CalCrony mentions only via `/notify mention:` pre-start pings. `on_start_mentions` is **partial**: CalCrony auto-posts a start announcement (README) but without a configurable mention. |
| 2.3 | **Poll `image` and `mentions` parameters** | <https://sesh.fyi/manual/> (`/poll` parameters) | **Missing.** `/poll create` (`PollModule.cs`, README command table) supports options/single-vote/anonymous/allow-options/closes but not an image or creation mentions. |
| 2.4 | **Favorites** (3 per server on free tier — quick-reuse of events) | <https://sesh.fyi/premium> (free-tier limits list) | **Missing.** Closest CalCrony analog is templates (`TemplateModule.cs`); no per-user favorites concept. |

## 3. Gaps — RSVP / attendance

This is CalCrony's largest gap cluster. CalCrony's RSVP options are hardcoded to three (Going ✅ / Not going / Maybe 🤔) at event creation — `src/CalCrony.Api/Endpoints/EventEndpoints.cs:112-114` — although the underlying `RsvpOption` data model (Emote/Label/SortOrder) looks extensible.

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 3.1 | **Custom RSVP options** — "Create unique RSVP options using any emoji" (premium) | <https://sesh.fyi/manual/>; <https://sesh.fyi/premium> | **Missing** (hardcoded 3 options, `EventEndpoints.cs:112-114`). |
| 3.2 | **RSVP attendee limits + waitlist management** (premium) | <https://sesh.fyi/premium> ("attendee limits/waitlists", "RSVP slot limits with waitlist management" on <https://sesh.fyi/>) | **Partial foundation.** `RsvpOptionDto` already carries an optional `Capacity` (`src/CalCrony.Contracts/Events.cs:88`) and the API rejects RSVPs to a full option (`EventEndpoints.cs:894-899`) — but nothing surfaces capacity at creation/edit in either UI, and there is no waitlist. |
| 3.3 | **Multiple RSVPs per user** — RSVP to more than one option (premium) | <https://sesh.fyi/manual/> ("Multiple RSVPs") | **Missing.** CalCrony RSVP is single-choice (switching revokes the previous choice — `RsvpComponentModule.cs`, `AttendeeRoleManager` semantics in README). |
| 3.4 | **Close RSVPs early** — close all RSVPs a set time before the event (premium) | <https://sesh.fyi/premium> | **Missing.** |
| 3.5 | **Role-based restrictions** — control who can RSVP to events / vote in polls (premium) | <https://sesh.fyi/> ("Role-based restrictions for RSVPs and voting"); <https://sesh.fyi/manual/> | **Missing.** CalCrony restricts *edit/delete* to creator/manager (`EventModule.cs`) but any member can RSVP/vote. |
| 3.6 | **Per-RSVP-option attendee roles** — "add Discord roles to users who RSVP to *specific options*" (premium) | <https://sesh.fyi/manual/> | **Partial.** CalCrony grants one role, only for "Going" (`AttendeeRoleManager.cs`, `Events.cs:32`). |

## 4. Gaps — recurrence

CalCrony recurrence (`src/CalCrony.Contracts/Series.cs`): every-N Day/Week/Month (N = 1–12), monthly by date or nth weekday, end by date/count, skip/stop/revive, occurrence-vs-series edit scope.

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 4.1 | **Yearly repeating events** | <https://sesh.fyi/manual/> ("Yearly, monthly, weekly, daily, hourly intervals"); <https://sesh.fyi/premium> | **Missing** (`RecurrenceUnit` = Day/Week/Month only). |
| 4.2 | **Hourly repeating events** | <https://sesh.fyi/manual/> | **Missing.** |
| 4.3 | **"Weekday" repeat option** (Mon–Fri) | <https://sesh.fyi/premium> ("daily, weekly, monthly, yearly, weekday") | **Missing.** Every-1-day is the nearest CalCrony rule; there is no weekday-only or multi-day-of-week schedule. |

## 5. Gaps — reminders / notifications

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 5.1 | **DM reminders to RSVPed users** ("Event reminders via DM") | <https://sesh.fyi/manual/> | **Missing.** All CalCrony deliveries are channel posts through the outbox (`src/CalCrony.Bot/DeliveryPollerService.cs`); no DM sending code exists in `src/` (grep for DM channel APIs finds nothing). |
| 5.2 | **User confirmation/warning preferences** — RSVP confirmation messages, event overlap warnings, poll vote confirmations | <https://sesh.fyi/manual/> ("User Settings") | **Partial.** A per-user `DmConfirmations` preference is already stored and editable (`SettingsModule.cs`, `src/CalCrony.Web/Pages/App/UserSettings.razor`, default on) but currently inert — no delivery code consumes it. No overlap detection or vote-confirmation toggles. |
| 5.3 | **Separate parsing timezone vs display timezone** | <https://sesh.fyi/manual/> ("Parsing timezone selection") | **Partial.** CalCrony has one personal timezone plus a server timezone (README first-steps section); the two roles aren't independently configurable per user. |

## 6. Gaps — calendar sync / web calendar

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 6.1 | **Two-way Google Calendar sync** — inbound and bidirectional (premium; free tier gets 1 outbound sync per user/server) | <https://sesh.fyi/premium> ("Full GCal sync"); <https://sesh.fyi/manual/> | **Deliberately declined (ADR 0003)** — not a backlog item. CalCrony's Google integration is deliberately least-privilege *free/busy read-only* (`src/CalCrony.Bot/Modules/CalendarModule.cs`, `AvailabilityModule.cs`; README: "CalCrony never sees event titles"). Outbound is the pull-based ICS feed (`/link`), not push sync; nothing flows inbound from Google into CalCrony events. |
| 6.2 | **Sesh Calendar** — public/private shareable web calendar view of all server events | <https://sesh.fyi/manual/> ("Easy to digest overview of all events" with public/private access) | **Partial.** CalCrony's web app shows events only behind Discord login (`src/CalCrony.Web/Pages/App/GuildEvents.razor` is a card list, not a calendar grid) and has no public/share mode. |
| 6.3 | **Live List** — auto-updating event list message in a channel (unlimited with premium) | <https://sesh.fyi/manual/> ("Automatically updating list that keeps your community informed"); <https://sesh.fyi/premium> | **Missing.** CalCrony `/list` is a one-shot response (`EventModule.cs`). |
| 6.4 | **Separate channel calendars** — restrict events per channel instead of one global calendar | <https://roadmap.sesh.fyi> (shipped item) | **Missing.** CalCrony's ICS feed is per-server (`LinkModule.cs`, README); no per-channel feed/calendar partitioning. |

## 7. Gaps — group scheduling tools

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 7.1 | **Time Finder** — members manually input availability; the tool finds "optimal time slots that work for as many people as possible"; premium adds "Time Finder advanced settings" | <https://sesh.fyi/time-finder>; <https://sesh.fyi/premium> | **Partial.** CalCrony covers the space differently: time polls with fixed creator-chosen slots (`PollModule.cs`) and a Google free/busy grid (`AvailabilityModule.cs`, `src/CalCrony.Web/Components/AvailabilityGrid.razor`). It has no manual paint-your-availability grid for members without a linked calendar, and no optimizer over open-ended ranges. |

## 8. Gaps — server administration

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 8.1 | **Server logs** — audit log of event/poll actions with filtering (24h free, unlimited premium) | <https://sesh.fyi/manual/> ("Server logs with action filtering"); <https://sesh.fyi/premium> | **Missing.** No user-facing action log; `aidlc-docs/audit.md` is a dev artifact, not a feature. |
| 8.2 | **Per-command permission management** via settings/dashboard | <https://sesh.fyi/manual/> ("Permission management for commands") | **Partial.** CalCrony relies on Discord's native slash-command permissions plus creator/server-manager checks (`EventModule.cs`); no in-bot permission configuration (`SettingsModule.cs` covers timezone/default-channel/native-events only). |
| 8.3 | **CSV export of event/attendee data** | <https://sesh.fyi/> ("Exportable event/attendee data"); <https://roadmap.sesh.fyi> ("Full Event and Poll Listings on Dashboard" with CSV export) | **Missing.** No export endpoint or UI in `CalCrony.Api`/`CalCrony.Web` beyond the ICS feed. |
| 8.4 | **`/delete` multiple events at once** | <https://sesh.fyi/manual/> ("Delete one or more events") | **Partial.** CalCrony `/delete name` deletes one event by title (`EventModule.cs`, README command table). |

## 9. Gaps — platform / Pro-tier features

| # | Sesh feature | Sesh source | CalCrony status |
|---|---|---|---|
| 9.1 | **Public API access** for automating events/integrations, with published docs (`/manual/api/`, `/api/v1/docs`) (Pro) | <https://sesh.fyi/premium>; <https://sesh.fyi/manual/> | **Partial.** CalCrony *is* an API-first system (`src/CalCrony.Api`, README architecture), and a self-hoster holds the `X-Api-Key` — but there are no per-user/per-server API tokens, no public API docs, and the hosted instance offers no user-facing API. |
| 9.2 | **Webhooks** for real-time notifications (Pro) | <https://sesh.fyi/premium> | **Missing.** Outbox deliveries go only to the Discord bot (`DeliveryPollerService.cs`); no outbound webhook targets. |
| 9.3 | **Custom bot branding** — name and avatar (Pro) | <https://sesh.fyi/premium> | **N/A via self-hosting.** Running your own bot application inherently gives your own name/avatar (README deploy steps); the hosted CalCrony bot offers no per-server branding. |
| 9.4 | **`/votepoints`** (top.gg voting rewards) | <https://sesh.fyi/manual/> | **N/A** — hosted-service growth mechanic, not a scheduling feature. |

---

## Priority read of the gaps

*Amended 2026-08-31 after the first-party hosted decision (ADR 0002) and the planning session that
followed it; the original research-time ordering is preserved in git history.*

Build order (interleaved: the quick-ship batch lands while the RSVP overhaul is designed):

1. **Quick-ship batch** — yearly recurrence (§4.1) and the auto-updating Live List (§6.3): small
   against existing machinery, visible during the top.gg listing's first weeks.
2. **RSVP v1 (§3)** — custom options with any emoji/label (§3.1), attendee limits with waitlists
   (§3.2), and close-RSVPs-early (§3.4): the "capped event" story complete. The extensible
   `RsvpOption` schema (`EventEndpoints.cs:110-115`) anticipated this. *v2 later:* role-restricted
   signup (§3.5), multiple RSVPs per user (§3.3), per-option attendee roles (§3.6).
3. **Public web calendar (§6.2)** — opt-in per server, unguessable regenerable slug URL (the ICS
   feed's "links are keys" model); vanity names possible later.
4. **Multi-day-of-week recurrence (§4.3, generalized)** — checkbox-style day sets (Tue/Thu) with
   weekdays (Mon–Fri) as a preset; touches model, parsers, both forms, and RRULE export, so it is
   scoped as its own item rather than a quick win.
5. **DM reminders (§5.1)** — strict recipient opt-in, default off, offered once after a first
   "Going" RSVP; creators and servers can never force DMs onto members.
6. **Admin logs (§8.1) + CSV export (§8.3)** — 90-day log retention matching the existing
   operational-records purge (self-host configurable); export for server managers via the web app.

**Deliberately declined:** inbound/two-way Google Calendar sync (§6.1) — permanently, per ADR 0003:
the free/busy-only scope is the product's privacy differentiator, and the ICS feed covers outbound;
and vote-gated perks (§9.4) — top.gg votes are a support ask, never a feature gate. The `/ai`
command (§2.1) and a Time Finder equivalent (§7.1) remain noted but unscheduled.
