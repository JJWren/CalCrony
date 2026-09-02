# Docs review — readable feature docs: code generation plan

**Request** (2026-09-02): "let's go ahead and do a docs review and update … the Features section of our README is a bit
difficult to read." **Depth**: minimal (documentation only; user stories, design and units skipped — one unit).
**Baseline**: `master` 12f2a19 (v0.35.0). **Branch**: `feat/readable-docs` → PR `feat: readable feature docs —
grouped README and an in-depth web docs page` (`feat:` because `Docs.razor` and `Landing.razor` are product surface
per CONTRIBUTING, so the change ships in a release; README and the research doc ride along).

## Decisions (locked 2026-09-02, all recommended options)

| # | Question | Decision |
|---|---|---|
| Q1 | README Features shape | Six themed groups of one-line bullets (≤ 25 words each, the command or flag in code), Features ≤ 400 words, closing link to the long form |
| Q2 | Where the long form lives | The web Docs page (`calcrony.app/docs`) is the single long form; the README links to it |
| Q3 | Web Docs page | Same treatment in this PR: a grouped "Features in depth" section absorbs the README detail, command rows shortened |
| Q4 | "self-hosted" intro | "Hosted at calcrony.app, or run your own" (ADR 0002) |
| Q5 | Gap-analysis research doc | Fix the §3.3/§3.5/§3.6 rows to the shipped versions and add a dated status note at the top |

## Findings the plan answers

README Features: 17 bullets, ~1,430 words; RSVPs 166, Web app 157, Role-restricted signup 156, Attendee roles 136
words; every bullet mixes capability, invocation, required permission and caveats. The web Docs page has the same
shape in its "The web app" list (Custom RSVPs 155 words) and its `/create` command row (~120 words). The README
Commands table rows carry the same semantics again (`/create` ~100 words, `/edit` ~80).

## Groups (README bullets and Docs page headings share them)

1. **Events & RSVPs** — natural-language events; RSVP buttons and custom options; limits and waitlist; close early;
   multiple RSVPs per member; event threads; templates; Discord native events
2. **Schedules** — recurring events (day sets, nth weekday, DST-safe, rolling occurrence, occurrence vs series edits);
   live list
3. **Reminders & calendars** — `/notify` pings and start announcement; `/remind`; DM reminders (opt-in); ICS feed;
   public web calendar (opt-in); Google free/busy availability
4. **Polls** — standard and time polls; convert a winner into an event
5. **Roles & access** — attendee roles (event-level and per option); role-restricted signup for events and polls
6. **Web app & admin** — Discord sign-in scope; what the browser can do; themes; activity log and CSV export

## Execution plan

**1. README.md**
- [ ] 1.1 Intro line: "Hosted at calcrony.app, or run your own — an event & calendar suite for Discord …"; the architecture paragraph and diagram unchanged.
- [ ] 1.2 Features: the six groups above as `###`-less bold group leads with one-line bullets; opt-in features say "opt-in" inline; the section ends with one line linking https://calcrony.app/docs for permissions, defaults and caveats. Budget ≤ 400 words.
- [ ] 1.3 Commands table: each row keeps "what it does" plus its flag names in code and drops the semantics sentences (they live on the Docs page); the first-steps block above the table stays.
- [ ] 1.4 Configuration, Running locally, Deploying, Community, Contributing sections untouched.

**2. `src/CalCrony.Web/Pages/Docs.razor`**
- [ ] 2.1 New "Features in depth" section after the worked example: the same six groups as `h2`s, one short `h3` per feature carrying the long-form text migrated from the README bullets and today's "The web app" bullets — what it does, how to invoke it in Discord and on the web, the Discord permission it needs, its default (opt-in or not) and caveats. Nothing said today is lost; duplication between the two pages ends.
- [ ] 2.2 "The web app" section shrinks to sign-in scope, what the browser can do, themes and the server re-sync link, pointing into the features section for the rest.
- [ ] 2.3 Slash commands table rows trimmed the same way as the README's.
- [ ] 2.4 First steps, interest-role guidance, the worked example and "Good to know" unchanged.

**3. `src/CalCrony.Web/Pages/Landing.razor`**
- [ ] 3.1 Tagline "self-hosted · open source · for your Discord server" → "open source · use calcrony.app or run your own · for your Discord server"; the purpose sentence with "free/busy availability" (test-pinned) stays.

**4. `docs/research/sesh-fyi-feature-gap.md`**
- [ ] 4.1 Dated status note at the top: RSVP v2 shipped (§3.6 v0.32.0, §3.5 v0.34.0, §3.3 v0.35.0); the three rows' status cells say so instead of "Missing"/"Partial".

**5. Tests**
- [ ] 5.1 bUnit: the Docs page renders the six group headings and the RSVP v2 features by name; Landing tests still pass.
- [ ] 5.2 Full web test project green; README rendered once through a markdown check (no broken links or fences).

**6. Bookkeeping and delivery**
- [ ] 6.1 `audit.md` entries throughout; `aidlc-state.md` gains a line for the docs pass.
- [ ] 6.2 PR opened with a short body (what changed, why `feat:`, before/after word counts); Copilot loop to zero comments; REST merge.
- [ ] 6.3 release-please cuts the release (approve its runs, REST merge); test stack pulls `:main`; prod bumps the tag (no migration in this release, backup taken anyway as the standing procedure); `/health` verified.
