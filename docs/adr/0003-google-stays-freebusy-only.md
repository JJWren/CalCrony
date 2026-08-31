# Google Calendar integration stays free/busy-only

CalCrony will never read users' Google Calendar event data. The Google integration keeps its
least-privilege, read-only **free/busy** scope permanently: CalCrony asks "is this person busy in
this window?" and nothing else. Sesh headlines "Full GCal sync" (two-way) as a premium feature;
CalCrony declines that direction deliberately — "the event bot that can't read your calendar" is
the product's privacy differentiator, made credible precisely because the scope makes reading
impossible rather than merely promised against.

## Considered Options

- **Free/busy-only, permanently (chosen).** Inbound sync (importing users' Google events) is
  declined for good. Outbound *push* (CalCrony writing its own events into a user's Google
  calendar) reads nothing and stays a possible future opt-in, but is not planned: the tokenized
  ICS feed already delivers CalCrony's schedule to Google/Apple/Outlook via pull.
- **Two-way sync for parity with sesh.** Rejected: it requires calendar-read scope, which
  collapses the privacy story the hosted instance markets (ADR 0002) and turns the Google
  verification burden from a lightweight free/busy review into a restricted-scope audit.
- **Inbound-only import.** Rejected for the same scope reason — reading is reading.

## Consequences

- The feature-gap analysis marks §6.1 (two-way GCal sync) declined, not pending; it must not
  resurface as a roadmap item without superseding this ADR.
- The privacy policy's "the free/busy scope makes that impossible" claim stays literally true and
  remains citable in listings and marketing.
- Users who want CalCrony events inside Google Calendar are pointed at the ICS subscribe URL
  (`/link`); its 12–24h Google refresh latency (see ADR 0001) is accepted rather than solved with
  push sync.
