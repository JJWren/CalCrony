# Delivery posts — where the bot posts and whom a post notifies: code-generation plan

**Change**: the channel a post goes to, and the mention of a scheduled ping, are set from Discord;
the bot posts a delivery only in a channel of the server the delivery is for, and names, for every
text post it makes there, the mentions that may notify; `/notify` follows the rule for changing an
event; the scheduler's save lets go of a row that was deleted while the sweep ran, and of what
would give a series a second live occurrence; and a delivery's payload column has no length limit
of its own.
**Baseline**: the date-limit change (`fix/date-ahead-limit`) on `master` ab32e96, with 1,699 tests
green there (783 API, 418 bot, 153 web, 345 for the release-notes tool).
**Branch**: `fix/delivery-posts`, on top of that change → PR
`fix: post only in a delivery's own server, and ping only whom a post is for`.
**Status**: built on 2026-11-16 and 2026-11-17 (UTC). Joshua's answer of 2026-11-16 ("2 A") put
this change next, ahead of the date limit's push, so that one release and one rollout carry both.
It was built the way he chose for the date limit: no separate approval of a plan was asked, and he
reads this plan together with the code at the completion gate, before any push. What he asked for
was the notification's channel and mention and the payload column. The template route, the default
channel, the server check for every kind of delivery, `/notify`'s rule and the sweep's save were
added while the change was built and reviewed, and he reads those here for the first time
(section 7). At the gate, on 2026-11-17, he chose to continue. `master` was taken in after that
(steps 5.4 to 5.6).

## 1. What this change ships

1. A scheduled ping's channel and its mention are set from Discord. A notification added from the
   web goes to the event's channel and carries no mention, and a template edited from the web
   keeps, for each reminder, the channel and mention a reminder of that template already has, and
   takes no other.
2. A server's default channel is set from Discord. A settings save from the web leaves it as it
   is.
3. The bot knows the server of every delivery it posts in a channel — the payload names it, or
   it is the server of the event or the poll the delivery is about — and posts the delivery, and
   re-renders an event's or a poll's posted embed, only in a channel of that server. A delivery
   for a channel anywhere else is logged, and nothing is posted or re-rendered there.
4. Every text post the bot makes in a server's channel from a delivery names the mentions it
   allows. A reminder notifies the member it is for, a waitlist promotion the promoted member, a
   notification the one role or member — or everyone — in its mention field, and a start
   announcement nobody. A mention written into a title, a message or a reminder is shown as text.
5. A delivery's payload column, `Deliveries.PayloadJson`, is `text`: a payload is bounded by the
   limits of the fields written into it, and the column has none of its own.
6. In Discord, `/notify` is for the event's creator or a server manager, as editing and deleting
   an event are.
7. The scheduler's save lets go of a row that was deleted while the sweep ran, and of what
   would give a series a second live occurrence, and saves the rest. When a sweep cannot be
   saved, the log names the rows the failure names, by entity, key and state.
8. The Docs page says where a ping goes, whom it mentions and who may add one; the README's row
   for `/notify` says who may; and the release summary has one line (step 5.12).

In the API, the bot and Contracts (one optional field on three payloads), with one migration and
one paragraph of the Docs page. No configuration. The web app's pages are not changed.

## 2. Carried in

| From | Decision |
|---|---|
| Joshua, 2026-11-16 | This change is built next, and the date limit is pushed together with it, so that one release and one rollout carry both. |
| The date limit, 2026-11-16 | Building starts without a separate approval of a plan; the written plan is read with the code at the completion gate, before any push. Taken over for this change, as said under Status. |
| The code as it is | A web caller picks no channel for an event, a poll or a reminder: those go to the server's default channel, and the routes say so. |
| The code as it is | The waitlist promotion already names the one member it may notify, because the text around the mention is written by members. |
| The code as it is | Editing and deleting an event are for its creator or a server manager, in Discord and on the web. The API applies that to web callers and takes the bot's word for callers in Discord. |
| Standing practice | Two review agents, one on security and one on code quality, read each commit before it is pushed, and the pushed head stays the commit they read. The push, the pull request, the merge, the release and the rollout each wait for Joshua's go-ahead. |
| Joshua, 2026-11-18, after the push | The desktop app's Auto-fix is on for the two pull requests: what it reports on them (a failed check, a conflict with `master`, a review comment) is answered, and a fix pushed to the branch, without asking him first. The two review agents still read each fix before it is pushed. |

In this plan a Discord server is called a server, as the Docs page does. The code calls it a
guild, as the rest of the code does.

## 3. Rules

### 3.1 A notification added through the API, and through `/notify`

1. From the bot, the channel and the mention are stored as given, as before. `/notify` takes them
   from Discord's own pickers: one role or member, and a text channel of the server.
2. From the web, neither is taken: the notification is stored without a channel, which means the
   event's channel, and without a mention. That holds for a notification added for a whole series
   too — the series' own copy gets neither.
3. The lead time and the message are taken from both, as before, and the length checks, the
   mention's among them, apply to every caller as before.
4. In Discord, `/notify` is for the event's creator and for a holder of Manage Server
   (`EventAccess.CanManage`), the rule that `/edit` and `/delete` follow and that the API applies
   to web callers. Another member is told so, and nothing is added.

### 3.2 A template edited through the API

1. From the bot, a list of reminders is stored as given, as before.
2. From the web, a reminder keeps its channel and mention only as a pair that a reminder of the
   template already has. Each stored pair carries over to one reminder of the new list — the list
   is taken longest lead time first — and a reminder with any other pair, or with half of a stored
   one, is stored without a channel and without a mention. Its lead time and message are free.
3. Saving a template from an event, and making an event from a template, copy the reminders with
   the channel and mention they hold, as before. A repeating event made from a
   template hands them on to its series, and the series to each occurrence, as before.

### 3.3 A server's default channel

1. From the bot, a settings save stores the default channel it is given, as before:
   `/settings default-channel` takes it from Discord's picker of the server's text channels.
2. From the web, a settings save leaves the default channel as it is, whatever the request holds:
   it is not changed, not cleared, and not set where none is stored. The rest of the save — the
   timezone and the native-events switch — is taken as before, and the server's log names only
   what changed.

### 3.4 The server a delivery is for

1. The payloads of a notification, a reminder and a waitlist promotion name the server
   (`GuildId`, optional on each): the event's server, or the server a reminder was made in. A
   start announcement's payload already names it.
2. The delivery that posts the embed of an event or a poll made on the web, and the one that
   re-renders a posted embed, carry the event's or the poll's id. The bot reads the event or the
   poll from the API, and its server with it.

### 3.5 Where the bot posts

1. The bot finds the channel for a delivery in one place, `DeliveryChannels`, which hands out a
   channel only when it is a message channel of the server the delivery is for. A channel of
   another server, or of no server, is not handed out.

   | Delivery | The server comes from | In a channel outside it |
   |---|---|---|
   | Notification, reminder, waitlist promotion | the payload | logged, dropped |
   | Start announcement | the payload | logged, not posted; the mirrored native event is still set to started |
   | The embed of an event or a poll made on the web | the event or the poll | logged, dropped: nothing is posted |
   | Re-rendering an event's or a poll's posted embed | the event or the poll | logged; nothing is re-rendered |
   | Deleting a posted embed | — | as before: the bot removes its own message wherever it is |

2. A dropped delivery is acknowledged, so it is not tried again: another try would find the same
   channel. It is logged as a warning with the delivery's id and kind, the channel's id and the
   server's id.
3. A channel that does not exist, or takes no messages, is handled as before: a delivery to be
   posted stays pending and is tried again, and a re-render has nothing to do.
4. A delivery whose payload names no server is given its channel without the comparison.

### 3.6 Whom a post may notify

1. What each text post in a server's channel may notify:

   | Post | May notify |
   |---|---|
   | Reminder | the member it is for |
   | Waitlist promotion | the promoted member (as before) |
   | Notification | what its mention field names (rule 2) |
   | Start announcement | nobody |

2. A notification's mention field is what `/notify`'s `mention` option stores: one role, one
   member, or `@everyone`. A field that is exactly one role mention or one member mention allows
   that one; `@everyone` allows everyone; white space around it is ignored. A field holding
   anything else allows nothing — `@here` among it, which the option cannot store. The field's
   text is shown in the post as stored. With `@everyone` in the field, Discord also honours an
   `@everyone` or `@here` written elsewhere in that post, which reaches nobody the field does not
   already name.
3. Each post says outright which kinds of mention Discord may read from its text: none, or for
   `@everyone` that one kind. None is sent as an empty list, the form Discord documents for it,
   beside the one member or role the post allows.
4. Whether Discord then notifies a role or everyone still depends on what the server lets the bot
   mention, as before.
5. The texts of the posts are unchanged. They are built in one place, `DeliveryPosts`, each with
   its mentions. `DeliveryPosts.PostAsync` posts a delivery: it takes the post and the server for
   the delivery's kind, finds the channel (3.5), and sends the post with its mentions.

### 3.7 The payload column

1. `Deliveries.PayloadJson` is `text`. The migration `WidenDeliveryPayload` changes the column's
   type and rewrites no row.
2. An older image reads and writes the column as before, so rolling the image back needs no
   `Down`. `Down` puts the 8192-character limit back, and first deletes the deliveries whose
   payload would not fit under it.

### 3.8 The sweep's save

1. A row the sweep changed can have been deleted while the sweep ran: a notification removed, an
   event or a poll deleted. The save lets go of such a row and saves the rest as the sweep worked
   it out from the rows it read: a ping the sweep had queued for a notification stays queued,
   once. The log says so in a warning that names the rows.
2. All the rows that went are let go of together. When a save meets the first of them, every
   notification, event and poll the sweep changed is looked for, and the ones still there are
   held until the sweep is saved. One more save is then enough, however many rows went.
3. Where the row is an occurrence of a series, the next occurrence the sweep made for that
   series is let go of with it, and the sweep leaves the series as it found it. Deleting a live
   occurrence stops its series, and the next sweep takes the series as it then stands.
4. A series has one live occurrence at a time, and the database holds it to that. Where
   something the sweep writes would give a series a second one by the time it is saved, the
   save lets go of it and saves the rest. That is the next occurrence the sweep made for a
   series that has a live one, with the series left as the sweep found it; and it is the sweep's
   own change of an occurrence to a live status where another occurrence of the series stands
   live as it is stored: what is stored then stands, and what the sweep queued for the
   occurrence stays queued, as under rule 1. To tell, the sweep looks at the stored occurrences
   of every series concerned — for an occurrence it is itself changing, the status it
   writes decides — and holds them until it is saved. Where nothing stands against
   what the sweep writes by then, the save is tried again as it is. The log says so in a warning
   that names what is let go of. Should the save fail on a live occurrence a second time, every
   next occurrence the sweep still has is let go of, and the next sweep makes them again; where
   it has none left, and at a third time, the failure is handed on under rule 5. A next
   occurrence that is let go of, under this rule or rule 3, takes the post queued for it along,
   and the number of deliveries the sweep reports leaves that post out.
5. When the save fails for any other reason, the scheduler writes one error line naming the rows
   the failure names: each by its entity, its key and what was being done to it, up to five, the
   rest counted. Rows that were being changed or removed are named before rows that were being
   added, which are stored nowhere once the save has failed. Nothing a row holds is in the line,
   and the failure itself is not attached to it: that goes on to the caller as before, which logs
   it and tries again on the next tick. Nothing of that sweep is saved.

A row that is still there is written as the sweep worked it out, as before: a status the sweep
writes stands over one written between its reads and its save, the change that rule 4 lets go of
apart.

## 4. What changes, file by file

| File | Change |
|---|---|
| `src/CalCrony.Api/Endpoints/NotificationEndpoints.cs` | 3.1 rules 1 to 3. |
| `src/CalCrony.Api/Endpoints/TemplateEndpoints.cs` | 3.2. |
| `src/CalCrony.Api/Endpoints/SettingsEndpoints.cs` | 3.3. |
| `src/CalCrony.Api/Services/DeliveryScheduler.cs` | The notification payload names the server (3.4); the sweep saves through `SweepSave`, and takes off its count what that let go of (3.8). |
| `src/CalCrony.Api/Services/SweepSave.cs` (new) | 3.8: the sweep's save, made once for each sweep. |
| `src/CalCrony.Api/Services/SeriesMaterializer.cs` | Its summary says what a sweep does where the next occurrence it made cannot be stored (3.8 rule 4). No code changes. |
| `src/CalCrony.Api/Endpoints/DeliveryEndpoints.cs`, `Services/RsvpPolicy.cs` | The reminder's and the promotion's payload name the server (3.4). In the first, also a comment that called the payload column bounded. |
| `src/CalCrony.Api/Services/SaveFailure.cs` (new) | The rows a failed save names, as text for the log lines of 3.8 rules 1, 4 and 5. |
| `src/CalCrony.Api/Data/CalCronyDbContext.cs`, `Migrations/20261116225655_WidenDeliveryPayload.cs` and its designer file, `Migrations/CalCronyDbContextModelSnapshot.cs` | 3.7. The migration was generated with `dotnet ef migrations add`; its summary and the delete in `Down` are written by hand. The context also gives a name to the index that keeps one live occurrence to a series, which 3.8 rule 4 reads; the index is as it was. |
| `src/CalCrony.Contracts/Deliveries.cs`, `Templates.cs`, `Events.cs` | `GuildId` on three payloads; the request summaries say which fields are set from Discord. |
| `src/CalCrony.Bot/DeliveryChannels.cs` (new) | 3.5: the channel for a post, the channel for a re-render, and the line in the log. |
| `src/CalCrony.Bot/DeliveryPosts.cs` (new) | 3.6, and 3.4 rule 1 on the bot's side. The four texts move here from the poller unchanged. |
| `src/CalCrony.Bot/DeliveryPollerService.cs` | Posts the four text kinds through `DeliveryPosts.PostAsync`, and finds the channel for an embed, and for a re-render, through `DeliveryChannels` (3.5, 3.6). |
| `src/CalCrony.Bot/EventAccess.cs` (new), `Modules/NotifyModule.cs` | 3.1 rule 4. |
| `src/CalCrony.Web/Pages/Docs.razor`, `README.md` | The entry on scheduled pings says where a ping goes, whom it mentions and who may add one; the README's row for `/notify` says who may. |
| `tests/CalCrony.Api.Tests/NotificationPostTests.cs` (new) | 3.1 rules 1 to 3; 3.2; the notification's payload (3.4). |
| `tests/CalCrony.Api.Tests/DefaultChannelTests.cs` (new) | 3.3; the reminder's payload (3.4). |
| `tests/CalCrony.Api.Tests/RsvpV1ApiTests.cs` | The promotion's payload names the server (3.4): one assertion in an existing test. |
| `tests/CalCrony.Api.Tests/DeliveryPayloadTests.cs` (new) | 3.7 rule 1; the text that names a failed save's rows (3.8 rules 1, 4 and 5). |
| `tests/CalCrony.Api.Tests/DeliveryPayloadMigrationTests.cs` (new) | 3.7 rule 2: the migration taken back and applied again. |
| `tests/CalCrony.Api.Tests/SchedulingTests.cs` | 3.8 rule 5 in a sweep: a save the database refuses. An older test of the class now looks for its own event's ping. |
| `tests/CalCrony.Bot.Tests/DeliveryChannelsTests.cs` (new) | 3.5. |
| `tests/CalCrony.Bot.Tests/DeliveryPostsTests.cs` (new) | 3.6, the four texts, and the posting of each kind. |
| `tests/CalCrony.Bot.Tests/DeliveryStandIns.cs` (new) | What those two classes stand a channel, a logger and a delivery in with. |
| `tests/CalCrony.Bot.Tests/EventAccessTests.cs` (new) | 3.1 rule 4: the rule, and where the command applies it. |
| `tests/CalCrony.Web.Tests/DocsPageTests.cs` | The Docs page's new sentences. |
| `docs/release-notes/0.37.2.md` | One line for 3.6 and 3.1 rule 4 (until step 5.12, a line for each, and one step of how to use the mention option). |

## 5. Execution plan

- [x] 0.1 Branch `fix/delivery-posts`, on the date-limit change.
- [x] 1.1 A notification added from the web (3.1). `NotificationPostTests`: one added from the web
      is stored and queued for the event's channel without a mention; one added by the bot keeps
      both; the same two for a whole series.
- [x] 1.2 A template edited from the web (3.2). `NotificationPostTests`: a stored pair is kept
      with the lead time and the message changed; five pairs the template does not have — another
      channel, another mention, either half alone, neither stored — are each stored without
      both; one stored pair asked for three times goes to the first reminder only; the bot's list
      is stored as given; an event made from the template gets what the template holds, and a
      repeating one hands it on to its series and its next occurrence.
- [x] 1.3 A settings save from the web (3.3). `DefaultChannelTests`: another channel, and none,
      each leave the stored one, in the answer, in what is stored and in the server's log; where
      none is stored the web sets none, and still cannot post an event; the bot sets, changes
      and clears it; an event and a reminder made on the web after such a save go to the stored
      channel.
- [x] 1.4 `/notify` asks who may change the event (3.1 rule 4). `EventAccessTests`, with stand-ins
      for members: the creator may; a holder of Manage Server may, for anyone's event; another
      member may not; outside a server only the creator may. The command itself cannot be run
      without Discord, so one test reads its source: the check stands between finding the event
      and adding the notification, and refuses with its sentence.
- [x] 2.1 The payloads name the server (3.4). `NotificationPostTests`, `DefaultChannelTests`,
      `RsvpV1ApiTests`: the queued payload of a notification, of a reminder made on the web and
      one made by the bot, and of a waitlist promotion carries the server; each payload written
      without the field reads as having none.
- [x] 2.2 `DeliveryChannels` (3.5). `DeliveryChannelsTests`, with stand-ins for channels: a text
      channel and a thread of the server are handed out; a channel of another server and one of
      no server are not, and are told apart from a channel that is not there or takes no
      messages; a delivery that names no server gets its channel; the channel is read by the
      id asked for; the comparison itself, pair by pair; a delivery to be
      posted gets its channel, is dropped with the warning when the channel lies outside its
      server, and is left to be tried again when the channel is not there; an embed is
      re-rendered in its server's channel, not outside it, which is logged, and not where the
      channel is gone, which is not.
- [x] 2.3 `DeliveryPosts` (3.6). `DeliveryPostsTests`: each post's text, and exactly what it may
      notify, with mention text of every kind in the title, the message and the reminder; the
      mention field's readings, one by one; each kind of delivery gets its own post and its
      payload's server; a delivery is posted with its text and its mentions; each of the four
      kinds is posted in a channel of its server and dropped in any other; one whose channel is
      not there is not posted.
- [x] 2.4 The poller posts through `DeliveryPosts.PostAsync` and finds its other channels through
      `DeliveryChannels`.
- [x] 3.1 The column and the migration (3.7). `DeliveryPayloadTests`: the model and the database
      agree that the column is `text` without a limit; a payload of 20,000 characters is stored
      and served whole. `DeliveryPayloadMigrationTests`: going back puts the limit back, keeps a
      payload of 8,192 characters and removes one of 8,193, and applying the migration again
      takes the limit off.
- [x] 3.2 The sweep's save (3.8), in a class of its own, `SweepSave`. `SchedulingTests`: a sweep
      whose save the database refuses writes the one error line, without the failure attached,
      saves nothing, and the next sweep does the work. `DeliveryPayloadTests`: the text for no
      rows, for a row with a single key and one with a double key, past five rows, and with rows
      being added among rows being changed and removed.
- [x] 4.1 The Docs page and its test; the README's row; the release summary, passed by `dotnet
      run --project tools/ReleaseNotes -- check`.
- [x] 5.1 `dotnet build CalCrony.slnx -c Release` with no new warnings; `dotnet test CalCrony.slnx
      -c Release` green. Result (2026-11-17, on `master` ab32e96 with the date limit under it): 0
      errors and 22 warnings, the number before the change; 1,803 tests green — 814 API, 490
      bot, 154 web and 345 for the release-notes tool — and 17 of that tool's tests skipped,
      which this machine cannot run. The change adds 31 to the API's 783, 72 to the bot's 418 and
      1 to the web's 153.
- [x] 5.2 Each rule taken out in turn, to see which tests fail (section 7 has the table, and what
      no test pins).
- [x] 5.3 The whole diff read against section 3, rule by rule; `aidlc-state.md` and `audit.md`
      brought up to date; this plan's boxes ticked.
- [x] 5.4 `master` taken in at `916ecd1` (pull requests #180, #183, #182 and #186), after the
      completion gate, with the date limit under it. Those pull requests change no file under
      `src` or `tests` that this change touches. Result on that tree: 0 errors and 22
      warnings; 1,911 tests green — 922 API, 490 bot, 154 web and 345 for the release-notes
      tool — and 17 of the tool's skipped.
- [x] 5.5 `master` taken in again at `723696d` (pull request #184), with the date limit under
      it. #184 changes `RsvpPolicy`, where this change adds the server to the waitlist
      promotion's payload; git merges the two without a conflict, and this change's lines are
      as they were. Result on that tree: 0 errors and 22 warnings; 1,949 tests green — 960
      API, 490 bot, 154 web and 345 for the release-notes tool — and 17 of the tool's skipped.
- [x] 5.6 `master` taken in at `162fd42`, with the date limit under it: 0.37.1 was released
      without these changes (pull request #185), and #188 and #187 followed. This change's two
      lines and its step move to `docs/release-notes/0.37.2.md`, after the date limit's line;
      `0.37.1.md` stays as 0.37.1 shipped it. No code under `src` differs from step 5.5's.
      Result on that tree: 0 errors and 22 warnings; 1,953 tests green — 960 API, 490 bot,
      154 web and 349 for the release-notes tool — and 17 of the tool's skipped.
- [x] 5.7 The date limit's merges of `master` merged in on 2026-11-18, after the push, each in a
      merge commit: that of `04beae8` (pull request #191, records only), then that of `47209ab`
      (pull request #194, records only). The first time only `aidlc-docs/aidlc-state.md` conflicted:
      it keeps the date limit's lines as that merge has them, and this change's section after them;
      git merged the audit log by itself, with the entries in the order of their timestamps. The
      second time only `aidlc-docs/audit.md` conflicted, and it is the date limit's merge with this
      change's entries put in by their timestamps. Nothing outside `aidlc-docs` differs from the
      tree of step 5.6, so its result stands for this one.
- [x] 5.8 The date limit's merge of `5fc9239` (pull requests #198, #193 and #195) merged in on
      2026-11-18 in the same way. `aidlc-docs/audit.md` and `aidlc-docs/aidlc-state.md` conflicted:
      the audit log is the date limit's merge with this change's entries put in by their timestamps,
      and the state file is the date limit's merge with this change's section after the date
      limit's. Git merges the rest by itself: `README.md`, which #193 and this change both change,
      in different sections; `TemplateEndpoints.cs` and `RsvpPolicy.cs`, which #198 and this change
      both change, in different places, with #198's refusal of a list with an entry missing before
      any line of this change that reads the entries. Result on the merged tree, built from scratch:
      0 errors and 22 warnings; 2,206 tests green — 1,213 API, 490 bot, 154 web and 349 for the
      release-notes tool — and 17 of the tool's skipped.
- [x] 5.9 `master` merged in at `3e35071`, the date limit's squash merge (pull request #189,
      2026-11-18T19:21:34Z), once GitHub had moved this pull request to `master` (step 6.2). Its
      tree is the date limit's `0addf9e`, which this branch holds since step 5.8, so the merge's
      result is this branch's own tree: where git found conflicts, in the records, in
      `docs/release-notes/0.37.2.md` and in `SchedulingTests.cs`, this branch's side is kept, which
      has the date limit's lines already. Nothing outside `aidlc-docs` differs from the tree of step
      5.8, so its result stands for this one. From here the pull request shows this change alone.
- [x] 5.10 `master` merged in again at `6b85491` (pull request #202, records only,
      2026-11-18T19:27:56Z), which reached `master` just before the merge of step 5.9 was pushed, so
      that the pull request conflicted with it again. It is merged in on top, in a second merge
      commit. Only `aidlc-docs/audit.md` conflicts: it is `master`'s entries with this change's put
      in by their timestamps; git merges the state file by itself. Nothing outside `aidlc-docs`
      differs from the tree of step 5.8, so its result stands for this one.
- [x] 5.11 `master` merged in again at `6cb41c8` (pull request #199, merged 2026-11-18T19:40:26Z),
      which changes how date text is read and adds its lines to `docs/release-notes/0.37.2.md`. The
      records and the release summary conflict: the audit log is `master`'s entries with this
      change's put in by their timestamps, the two entries this branch made exact keeping its
      wording; the state file is `master`'s, with the date limit's build-and-test line as this
      branch has it and this change's section after #199's; and the release summary keeps `master`'s
      lines, with this change's two lines after them and its step as step 3 under "How to use it"
      (1,595 of the 1,800 characters; the repository's check passes it). Git merges the code by
      itself: #199 changes `NaturalDateTimeParser` and its tests, which this change does not touch.
      Result on the merged tree, built from scratch: 0 errors and 22 warnings; 2,510 tests green —
      1,517 API, 490 bot, 154 web and 349 for the release-notes tool — and 17 of the tool's skipped.
- [x] 5.12 `master` merged in again at `1fce915` (pull request #196, merged 2026-11-18T19:55:10Z),
      which changes the public calendar and adds a line to `docs/release-notes/0.37.2.md`. The
      records and the release summary conflict: the audit log is `master`'s entries with this
      change's put in by their timestamps, the two entries this branch made exact keeping its
      wording; the state file is `master`'s, with the date limit's build-and-test line as this
      branch has it and this change's section after #196's; and the release summary keeps `master`'s
      lines. With this change's two lines and its step as well, the summary would run to 1,967
      characters, over the 1,800 its check allows; so this change's two lines are made one that says
      the same, and its step of how to use the mention option is left out: 1,744 of the 1,800
      characters, and the repository's check passes it. The other changes' lines are as they wrote
      them. Git merges the code by itself: #196 changes the public calendar's route, its builder and
      `SeriesProjection`, with their tests, none of which this change touches. Result on the merged
      tree, built from scratch: 0 errors and 22 warnings; 2,533 tests green — 1,540 API, 490 bot,
      154 web and 349 for the release-notes tool — and 17 of the tool's skipped.
- [x] 5.13 `master` merged in again at `c97c15e` (pull request #201, records only,
      2026-11-18T20:50:59Z). Only `aidlc-docs/audit.md` conflicts: it is `master`'s entries with
      this change's put in by their timestamps, the two entries this branch made exact keeping its
      wording; git merges the state file by itself. Nothing outside `aidlc-docs` differs from the
      tree of step 5.12, so its result stands for this one.

## 6. Delivery

- [x] 6.1 Both review agents read each commit before any push; what they find is answered in a
      commit they read again.
- [x] 6.2 *Go-ahead:* push, together with the date limit. Pushed on 2026-11-18, on Joshua's go-ahead
      of 2026-11-17 ("A, push both"), at `92fd33b`, and opened as pull request #190 with the date
      limit's branch (#189) as its base: while that is its base it shows this change alone, and CI
      and CodeQL, which run on pull requests into `master`, wait until it is moved there. Once the
      date limit is squash-merged and its branch deleted, GitHub moves this pull request to
      `master`; until `master` is merged into the branch, it shows the date limit's change as well.
      `master` is then merged in, rather than the branch being put onto the new `master`, since the
      branch is on GitHub: the date limit's own lines are the same on both sides, and only records
      added since can conflict. The agents read that merge before it is pushed.
- [x] 6.3 *Go-ahead:* squash merge. Squash-merged on 2026-11-18 at 22:38:40Z, on Joshua's go-ahead
      ("Yes, merge #190"), at the head both agents read (`a84a97f`), as `aeff22d`; its tree is that
      head's. Before the merge, three parts of the pull request's text, which became the commit's
      message, were brought up to date: that it stood on the date limit's branch, the test count,
      and its release summary, now one line. The test stack picks up `:main`. One thing is looked at
      there before the release, since no test here can see Discord's side of it: whom a start
      announcement, a reminder and a scheduled ping notify. Looked at on 2026-11-18, on his go-ahead
      ("Yes, look now"), with the test stack's app services on `0.0.0-main.5b30e5b` (the database
      left as it was; the migration applied at the API's start): in the test server he made an
      event, a scheduled ping with himself in its mention option, and a reminder, and Discord's own
      record of each post shows that the ping and the reminder mentioned him alone and the start
      announcement nobody, with no role and not everyone. The text he typed into the title, the
      message and the reminder held no mention, so that rule was not seen on Discord's side; the
      tests hold it.
- [x] 6.4 *Go-ahead:* the release (a patch release, opened by release-please). Released on
      2026-11-18 as v0.37.2, with the date limit, on Joshua's go-ahead ("Yes, release now"): release
      pull request #200 merged at 23:08:43Z as `6e0d8b2`, the tag and the GitHub release at
      23:08:57Z with the release summary on top of its notes, the three images by 23:13:02Z and the
      announcement at 23:13:06Z; every job of the run passed.
- [x] 6.5 *Go-ahead:* prod — a backup first, then the pinned tag, app services only. The API applies
      the migration when it starts. Rolled out on 2026-11-18, with the date limit, on Joshua's
      go-ahead ("Yes, roll out now"): no delivery was pending; the database was backed up first; the
      tag was pinned to 0.37.2 and the API, the bot and the web were restarted on it, the database
      container left as it was. The API started at 23:17:25Z and applied the migration
      `WidenDeliveryPayload`; `/health` and `/health/ready` report 0.37.2, and the logs since show
      only the known start-up notices.

## 7. Notes from generation

### What was added while the change was built

Joshua's answer asked for two things: a notification's channel and mention (3.1 rules 1 to 3, and
3.6), and the payload column (3.7). The rest came in as the routes and the bot were read and as
the review agents read the change:

- 3.2 and 3.3 — as the routes were read.
- 3.4 and 3.5 for every kind of delivery that is posted in a channel, rather than for the
  notification alone.
- 3.1 rule 4 and 3.8 rules 1 to 3 — from the security agent's reads.
- 3.8 rule 4, and the save in a class of its own — after the confirmation round: the
  rule as the save was read once more, the class at the quality agent's word.
- 3.8 rule 5 — built with the payload column: a save that fails says which rows it could not
  take.

### Tests added

104, and one assertion in a test that was there.

| Class | Tests | What they hold |
|---|---|---|
| `NotificationPostTests` (API) | 16 | 3.1 rules 1 to 3; 3.2; the notification's payload (3.4) |
| `DefaultChannelTests` (API) | 7 | 3.3; the reminder's payload (3.4) |
| `DeliveryPayloadTests` (API) | 6 | 3.7 rule 1; the text that names a failed save's rows (3.8 rules 1, 4 and 5) |
| `DeliveryPayloadMigrationTests` (API) | 1 | 3.7 rule 2 |
| `SchedulingTests` (API) | 1 of its 12 | 3.8 rule 5 |
| `RsvpV1ApiTests` (API) | one assertion | the promotion's payload (3.4) |
| `DeliveryChannelsTests` (bot) | 23 | 3.5 |
| `DeliveryPostsTests` (bot) | 44 | 3.6, the four texts, 3.4 on the bot's side, and the posting |
| `EventAccessTests` (bot) | 5 | 3.1 rule 4 |
| `DocsPageTests` (web) | 1 of its 8 | the Docs page's sentences |

Two kinds of stand-in are used, neither of them a mocking library, which the test projects do
not have. A channel and a member are made with `DispatchProxy`: each answers the one or two questions
the code under test asks of it — a channel also keeps what is sent on it — and throws at anything
else. One scheduler test adds a constraint to the database for its own length, so that a save is
refused.
And one test reads `/notify`'s source, because the command cannot be run without Discord: it
holds the order — find the event, ask who may change it, refuse or call the API.

### Rules taken out, one at a time (2026-11-17)

Each change below was made on its own to the built code, and the code was put back after it. The
changes to the API were each followed by the same eight test classes — `NotificationPostTests`,
`DefaultChannelTests`, `DeliveryPayloadTests`, `DeliveryPayloadMigrationTests`, `SchedulingTests`,
`TemplateApiTests`, `WebMutationTests` and `RsvpV1ApiTests`, 97 tests — those to the bot by its
three new classes, 72 tests, and those to the Docs page by `DocsPageTests`, 8 tests; all green
with nothing taken out. Eighty changes, one run each, on the code and the tests as they are in
this commit. Every change failed the tests named, and no others.

| Rule taken out | Tests that failed |
|---|---|
| 3.1 rule 2 — the web's channel is taken; the web's mention is taken (two changes) | 2 each: `A_notification_added_from_the_web_for_a_whole_series_carries_neither_onto_the_series`, `A_notification_added_from_the_web_goes_to_the_events_channel_without_mentions` |
| 3.1 rule 2 — the event's own row takes the request's channel; its mention (two changes) | 2 each: `A_notification_added_from_the_web_for_a_whole_series_carries_neither_onto_the_series`, `A_notification_added_from_the_web_goes_to_the_events_channel_without_mentions` |
| 3.1 rule 2 — the series' copy takes the request's channel; its mention (two changes) | 1 each: `A_notification_added_from_the_web_for_a_whole_series_carries_neither_onto_the_series` |
| 3.1 rule 1 — the bot's channel is not taken; the bot's mention is not taken (two changes) | 11 each: the two tests of a notification added by the bot, and the nine template tests of `NotificationPostTests`, which start from a pair the bot stored |
| 3.1 rule 4 — a holder of Manage Server may not | 1: `A_holder_of_manage_server_may_change_anyones_event` |
| 3.1 rule 4 — the creator may not | 2: `Outside_a_server_only_the_creator_may`, `The_creator_may_change_the_event` |
| 3.1 rule 4 — anyone may | 2: `Another_member_may_not`, `Outside_a_server_only_the_creator_may` |
| 3.1 rule 4 — `/notify` does not ask | 1: `The_notify_command_refuses_before_it_calls_the_api` |
| 3.2 rule 2 — the web is treated as the bot | 6: `A_template_edited_from_the_web_keeps_each_stored_pair_for_one_reminder`, `A_template_edited_from_the_web_takes_no_channel_or_mention_its_reminders_did_not_have` (5 cases) |
| 3.2 rule 1 — the bot is treated as the web | 1: `A_template_edited_by_the_bot_takes_the_channel_and_mentions_it_is_given` |
| 3.2 rule 2 — a stored pair is kept for every reminder that asks for it | 1: `A_template_edited_from_the_web_keeps_each_stored_pair_for_one_reminder` |
| 3.2 rule 2 — only the channel is cleared; only the mention is cleared (two changes) | 5: `A_template_edited_from_the_web_keeps_each_stored_pair_for_one_reminder`, `A_template_edited_from_the_web_takes_no_channel_or_mention_its_reminders_did_not_have` (4 cases); 5: `A_template_edited_from_the_web_keeps_each_stored_pair_for_one_reminder`, `A_template_edited_from_the_web_takes_no_channel_or_mention_its_reminders_did_not_have` (4 cases) |
| 3.2 rule 2 — a pair is matched by its channel alone; by its mention alone (two changes) | 2: `A_template_edited_from_the_web_takes_no_channel_or_mention_its_reminders_did_not_have` (2 cases); 2: `A_template_edited_from_the_web_takes_no_channel_or_mention_its_reminders_did_not_have` (2 cases) |
| 3.3 rule 2 — the web's default channel is taken | 4: `A_settings_save_from_the_web_leaves_the_default_channel_as_it_is` (2 cases), `A_settings_save_from_the_web_sets_no_default_channel_where_none_is_stored`, `What_the_web_posts_goes_to_the_stored_default_channel_after_such_a_save` |
| 3.3 rule 2 — the server's log is written from the request's value | 2: `A_settings_save_from_the_web_leaves_the_default_channel_as_it_is` (2 cases) |
| 3.3 rule 1 — the bot's default channel is not taken | 22: 5 in `DefaultChannelTests`, 11 in `NotificationPostTests`, 6 in `WebMutationTests` |
| 3.4 — the notification's payload without the server | 1: `A_notification_delivery_names_its_events_server` |
| 3.4 — the reminder's payload without the server | 1: `A_reminder_delivery_names_the_server_it_was_made_in` |
| 3.4 — the promotion's payload without the server | 1: `Dropping_an_attendee_promotes_the_first_waitlisted_user_with_a_ping` |
| 3.4 — the server is not handed on from the payload of a reminder; a notification; a promotion; a start announcement (four changes) | 2: `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild`; 2: `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild`; 2: `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild`; 2: `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild` |
| 3.5 rule 1 — every channel is handed out | 8: `A_channel_of_another_guild_is_not_handed_out`, `A_channel_of_no_guild_is_not_handed_out_to_a_delivery_that_has_one`, `A_delivery_whose_channel_lies_outside_its_guild_is_dropped_with_a_warning`, `An_embed_in_a_channel_outside_its_guild_is_not_re_rendered_and_that_is_logged`, `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases) |
| 3.5 rule 1 — the channel's own server is not read | 9: 4 in `DeliveryChannelsTests`, 5 in `DeliveryPostsTests` |
| 3.5 rule 1 — a negative server id is compared like any other | 1: `A_channel_is_in_a_deliverys_guild_when_the_two_are_the_same_guild` |
| 3.5 rule 1 — the channel is read by another id, when looking; when posting; when re-rendering (three changes) | 3: `A_delivery_is_posted_in_the_channel_it_names_when_that_is_in_its_guild`, `An_embed_is_re_rendered_in_a_channel_of_its_guild`, `The_channel_is_read_by_the_id_that_is_asked_for`; 1: `A_delivery_is_posted_in_the_channel_it_names_when_that_is_in_its_guild`; 1: `An_embed_is_re_rendered_in_a_channel_of_its_guild` |
| 3.5 rule 1 — posting does not hand the delivery's server on | 4: `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases) |
| 3.5 rule 2 — a channel outside the server is reported as not there | 8: `A_channel_of_another_guild_is_not_handed_out`, `A_channel_of_no_guild_is_not_handed_out_to_a_delivery_that_has_one`, `A_delivery_whose_channel_lies_outside_its_guild_is_dropped_with_a_warning`, `An_embed_in_a_channel_outside_its_guild_is_not_re_rendered_and_that_is_logged`, `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases) |
| 3.5 rule 2 — a channel that is handed out is reported as outside | 4: `A_channel_of_the_deliverys_guild_is_found`, `A_delivery_that_names_no_guild_is_given_the_channel` (2 cases), `An_embed_is_re_rendered_in_a_channel_of_its_guild` |
| 3.5 rule 2 — a delivery outside its server is tried again and one whose channel is missing is dropped | 7: `A_delivery_whose_channel_is_not_there_is_left_to_be_tried_again`, `A_delivery_whose_channel_lies_outside_its_guild_is_dropped_with_a_warning`, `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases), `A_delivery_whose_channel_is_not_there_is_not_posted_and_is_left_to_be_tried_again` |
| 3.5 rule 2 — no line in the log for a dropped post; for an embed not re-rendered (two changes) | 5: `A_delivery_whose_channel_lies_outside_its_guild_is_dropped_with_a_warning`, `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases); 1: `An_embed_in_a_channel_outside_its_guild_is_not_re_rendered_and_that_is_logged` |
| 3.5 rule 2 — the line is written below a warning | 6: `A_delivery_whose_channel_lies_outside_its_guild_is_dropped_with_a_warning`, `An_embed_in_a_channel_outside_its_guild_is_not_re_rendered_and_that_is_logged`, `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases) |
| 3.5 rule 2 — a dropped delivery is reported as posted | 4: `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases) |
| 3.5 rule 3 — a channel that is not there is reported as outside | 5: `A_channel_that_is_not_there_is_missing_and_not_outside`, `A_channel_that_takes_no_messages_is_missing_and_not_outside`, `A_delivery_whose_channel_is_not_there_is_left_to_be_tried_again`, `An_embed_whose_channel_is_gone_is_not_re_rendered_and_nothing_is_said`, `A_delivery_whose_channel_is_not_there_is_not_posted_and_is_left_to_be_tried_again` |
| 3.5 rule 4 — a delivery that names no server gets no channel | 5: `A_channel_is_in_a_deliverys_guild_when_the_two_are_the_same_guild` (2 cases), `A_delivery_that_names_no_guild_is_given_the_channel` (2 cases), `A_delivery_that_names_no_guild_is_posted_in_its_channel` |
| 3.6 rule 1 — a reminder allows every mention; a reminder allows none (two changes) | 3: `A_delivery_is_posted_in_its_channel_with_its_text_and_its_mentions`, `A_reminder_pings_the_member_it_is_for_and_nobody_else`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild`; 2: `A_delivery_is_posted_in_its_channel_with_its_text_and_its_mentions`, `A_reminder_pings_the_member_it_is_for_and_nobody_else` |
| 3.6 rule 1 — a waitlist promotion allows every mention | 2: `A_waitlist_promotion_pings_the_promoted_member_and_nobody_else`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild` |
| 3.6 rule 1 — a start announcement allows every mention | 2: `A_start_announcement_pings_nobody`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild` |
| 3.6 rule 1 — a notification allows every mention | 3: `A_notification_pings_the_role_in_its_mention_field_and_nothing_written_in_its_text`, `A_notification_without_mention_or_message_is_the_title_and_the_time`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild` |
| 3.6 rule 2 — `@everyone` is not read, and `@here` is | 3: `A_mention_field_holding_anything_else_allows_nothing`, `A_mention_field_of_everyone_allows_everyone` (2 cases) |
| 3.6 rule 2 — `@here` is read as well | 1: `A_mention_field_holding_anything_else_allows_nothing` |
| 3.6 rule 2 — everyone allows every kind of mention | 2: `A_mention_field_of_everyone_allows_everyone` (2 cases) |
| 3.6 rule 2 — white space around the field is not ignored | 3: `A_mention_field_naming_a_member_allows_that_member`, `A_mention_field_naming_a_role_allows_that_role`, `A_mention_field_of_everyone_allows_everyone` |
| 3.6 rule 2 — a role mention is found anywhere in the field | 4: `A_mention_field_holding_anything_else_allows_nothing` (4 cases) |
| 3.6 rule 2 — a member mention is found anywhere in the field | 1: `A_mention_field_holding_anything_else_allows_nothing` |
| 3.6 rule 2 — a member mention written with `!` is not read | 1: `A_mention_field_naming_a_member_allows_that_member` |
| 3.6 rule 2 — a role is allowed as a member | 4: `A_mention_field_naming_a_role_allows_that_role` (3 cases), `A_notification_pings_the_role_in_its_mention_field_and_nothing_written_in_its_text` |
| 3.6 rule 2 — a member is allowed as a role | 3: `A_mention_field_naming_a_member_allows_that_member` (3 cases) |
| 3.6 rule 2 — a field holding anything else allows every mention | 21: `A_mention_field_holding_anything_else_allows_nothing` (20 cases), `A_notification_without_mention_or_message_is_the_title_and_the_time` |
| 3.6 rule 3 — the kinds of mention are left unsaid where nobody is allowed; where one member is; where one role is (three changes) | 23: `A_mention_field_holding_anything_else_allows_nothing` (20 cases), `A_notification_without_mention_or_message_is_the_title_and_the_time`, `A_start_announcement_pings_nobody`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild`; 7: `A_delivery_is_posted_in_its_channel_with_its_text_and_its_mentions`, `A_mention_field_naming_a_member_allows_that_member` (3 cases), `A_reminder_pings_the_member_it_is_for_and_nobody_else`, `A_waitlist_promotion_pings_the_promoted_member_and_nobody_else`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild`; 5: `A_mention_field_naming_a_role_allows_that_role` (3 cases), `A_notification_pings_the_role_in_its_mention_field_and_nothing_written_in_its_text`, `Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild` |
| 3.6 rule 5 — a post is sent without its mentions | 2: `A_delivery_is_posted_in_its_channel_with_its_text_and_its_mentions`, `A_post_is_sent_with_its_text_and_the_mentions_it_allows` |
| 3.6 rule 5 — posting a delivery sends nothing | 5: `A_delivery_is_posted_in_its_channel_with_its_text_and_its_mentions`, `A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other` (4 cases) |
| 3.7 rule 1 — the model keeps the limit | 97: 7 in `DefaultChannelTests`, 1 in `DeliveryPayloadMigrationTests`, 6 in `DeliveryPayloadTests`, 16 in `NotificationPostTests`, 32 in `RsvpV1ApiTests`, 12 in `SchedulingTests`, 11 in `TemplateApiTests`, 12 in `WebMutationTests` |
| 3.7 rule 1 — the migration leaves the column's type as it was | 3: `Going_back_puts_the_limit_back_and_removes_only_the_payloads_that_would_not_fit`, `A_long_payload_is_stored_and_served_whole`, `The_payload_column_has_no_length_limit_of_its_own` |
| 3.7 rule 2 — `Down` without its delete; deleting from 8,192 characters; deleting only above 8,193 (three changes) | 1 each: `Going_back_puts_the_limit_back_and_removes_only_the_payloads_that_would_not_fit` |
| 3.8 rule 5 — no line for another failure; the failure is not handed on (two changes) | 1 each: `A_sweep_that_cannot_be_saved_names_the_rows_in_the_log` |
| 3.8 rule 5 — a row is named without its state; without its key (two changes) | 4 each: `A_failed_save_names_each_row_by_entity_key_and_state_and_not_by_what_it_holds`, `A_failed_save_names_five_rows_and_counts_the_rest`, `A_failed_save_names_the_rows_being_changed_or_removed_before_the_rows_being_added`, `A_sweep_that_cannot_be_saved_names_the_rows_in_the_log` |
| 3.8 rule 5 — fifty rows are named; the rest are not counted (two changes) | 2 each: `A_failed_save_names_five_rows_and_counts_the_rest`, `A_failed_save_names_the_rows_being_changed_or_removed_before_the_rows_being_added` |
| 3.8 rule 5 — nothing is said where no row is named | 1: `A_failed_save_that_names_no_rows_says_so` |
| 3.8 rule 5 — rows are named in the failure's own order | 2: `A_failed_save_names_each_row_by_entity_key_and_state_and_not_by_what_it_holds`, `A_failed_save_names_the_rows_being_changed_or_removed_before_the_rows_being_added` |
| Docs — the sentence about the one mention; who may add a ping (two changes) | 1 each: `Docs_page_says_where_a_scheduled_ping_goes_and_whom_it_mentions` |

What no test pins:

- What is left in the poller. No test runs the poller, which needs Discord. It hands each text
  delivery to `DeliveryPosts.PostAsync` and acknowledges it; hands the server of an event or a
  poll, read from the API, to `DeliveryChannels` for an embed and for a re-render; and sets the
  native event to started after a start announcement, posted or not. Those lines are read, not
  run. What they call is pinned.
- Discord's side of the mentions a post allows. The tests hold what is handed to the Discord
  library, down to the kinds of mention being said outright. What Discord then does is looked at
  on the test stack (step 6.3).
- `/notify` as a command. Its rule is pinned and its order is read from its source; it is not run.
- In the reading of the mention field, the digits are written `[0-9]`, the length `{1,20}` and
  the end `\z`. Writing `\d`, a longer length or `$` changes no result: the number is parsed
  afterwards and anything it cannot hold is refused there, and the field is trimmed first.
- The sweep's save beyond rule 5 (3.8 rules 1 to 4): what it lets go of, what it looks for and
  holds, and its limit.
- The web's template page and settings page are not changed, and no test here runs them.

### Review round 1 (2026-11-17)

Both agents read the first commit of the change, each in a copy of its own in which it built and
ran it, and neither found anything that should hold up a push.

Security found the rules of 3.1 to 3.7 to hold for every caller it tried, the migration sound in
both directions, and what the bot hands to the Discord library to be what Discord's rules ask
for. It ran the two routes of 3.2 and 3.3 as well as reading them. Two rules came from its read
and are in the change: 3.1 rule 4, and 3.8 rule 1.

Quality found the code to do what section 3 says, rule by rule, and the tests to pin the API's
rules and the two new bot classes closely. Its findings, all minor, and their answers:

- Nothing reached the poller, where 3.5 and the sending half of 3.6 were carried out: fifteen
  changes there left every test green. The posting of the four text kinds, and the choice
  between dropping a delivery and trying it again, moved into `DeliveryPosts.PostAsync` and
  `DeliveryChannels`, where stand-ins drive them; what is left in the poller is named above.
- "Nobody" was handed to the Discord library without the kinds of mention being said, a form the
  library documents and Discord's own reference does not. Every post now says them outright
  (3.6 rule 3), the tests require it, and step 6.3 has the look at Discord's side.
- Statements that said more than the code did, in the commit message, in section 1 and in the
  pull request's text: that every delivery names its server (3.4 rule 2 now says which do), that
  every text post names its mentions (a reminder sent to a member directly is a text post too,
  and has nobody else to notify), and that a delivery outside its server is always logged (a
  re-render was not, and is now).
- With `@here` in the mention field, a post would have let an `@everyone` written in its text
  through. `/notify` cannot store `@here`, so the field no longer honours it (3.6 rule 2).
- The line for a failed save named the rows of the failed batch in the batch's order, in which
  rows being added come first; those are stored nowhere afterwards. Rows being changed or
  removed are now named first (3.8 rule 5).
- Smaller points, taken: the failure is no longer attachable to that line without a test
  failing; a notification's test used the same role in its field and in its text, and now does
  not; the migration's test is in a file of its own; a repeating event made from a template is
  followed to its series and its next occurrence (3.2 rule 3); the summaries of the poller, of
  the two generated patterns and of the settings request say what they now do; the bot's new
  classes say guild where the rest of the code does.

### The confirmation round, and after it (2026-11-17)

Both agents read the result of round 1, and the date limit's commit on the new `master` beside
it. Neither found anything in the code that should hold up a push, and both confirmed the date
limit's commit against the one they had confirmed before.

Security ran the sweep's save against its own attempts and found it to hold. Two rules came of
its read: all the rows that went are let go of together (3.8 rule 2), and a series whose
occurrence went gets no next occurrence from that sweep (3.8 rule 3). Its other points are
taken.

Quality found every finding of round 1 answered and the counts exact. Its findings, and their
answers:

- An older test of `SchedulingTests` counted every ping queued in the class's channel, and so
  held only while no other test of the class queues one. It now looks for its own event's ping.
- Step 6.2 did not say that this branch has to be put onto the new `master` after the date limit
  is squash-merged. It does now.
- The sentence that the save leaves things as if the sweep had run just before the delete was
  not true of a series' occurrence. 3.8 rules 1 and 3 now say what happens in each case, and the
  code does it.
- Smaller points, taken: the warning no longer says the rest is saved before it is; the save's
  summary says why a change that finds no row means the row is gone; `EventAccess`'s summary
  says which command asks it; two slips in the state file; the rule numbers in two rows of the
  table; and `\z` beside the other two spellings that change no result.

After that round the save was read once more, and one rule was added to it: 3.8 rule 4. With
it the number of deliveries the sweep reports leaves out what was let go of, for rule 3 as well.
The failure that goes on to the caller is rule 5, worded as it was. At the quality agent's word
the whole save moved out of the scheduler into a class of its own, `SweepSave`, made once for
each sweep.

Both agents then read that code, and neither found anything in it that should hold up a push.
From the quality read, taken: summaries in the save and one of its log lines are reworded; the
answer of the look for rows that went is kept as a set; and the summary in `SeriesMaterializer`
points at the class. With the reworded line, a failure for which the save has nothing left to
let go of is handed on at once. Both agents read this commit before any push.

### `master` taken in (2026-11-17 and 2026-11-18)

After the completion gate `master` moved from `ab32e96` to `916ecd1`, by pull requests #180, #183,
#182 and #186. The date limit is taken onto it first (its plan, step 5.6), and this change is
again one commit on top of the date limit. Under `src` and `tests` those pull requests change
the date parser and the public calendar's builder, with their tests: no file this change
touches. So this change's own code is the code both agents confirmed, and the table above
stands as it was run. `docs/release-notes/0.37.1.md` holds every entry, this change's two
bullets and its step after those already there. The audit entries are in the order of their
timestamps, and the state file keeps every section. Step 5.4 has the result.

Later that day `master` moved to `723696d` by pull request #184 (date text that is missing or
cannot be read). It is taken in the same way, with the date limit under it. Under `src` #184
changes `NaturalDateTimeParser` and `RsvpPolicy`. This change touches `RsvpPolicy` in one
place, the waitlist promotion's payload, away from #184's lines, and git merges the two
without a conflict. Step 5.5 has the result.

0.37.1 was then released without these changes (pull request #185), and #188 and #187
followed; `master` is taken in at `162fd42`, with the date limit under it. The summary's lines
move to the next version's file: `docs/release-notes/0.37.2.md` holds the date limit's line,
this change's two lines and, under a heading of its own, its step. No code under `src` differs
from the take-in before. Step 5.6 has the result.

After the push, on 2026-11-18, `master` moved to `04beae8` by pull request #191 (records) and then
to `47209ab` by #194 (records), and the date limit merges both in (its plan, step 5.9). This branch
merges in the date limit's merges, so that its pull request does not conflict with its base, which
now holds `master`'s records, and still shows this change alone. The audit entries of both sides are
in the order of their timestamps. Nothing outside `aidlc-docs` differs. Step 5.7 has it.

Later that day `master` moved to `5fc9239` by pull requests #198, which changes the API as well as
records, #193, which adds a code of conduct and records, and #195, records only, and the date limit
merges it in (its plan, step 5.10); this branch merges in that merge in the same way. The code
merges without a conflict, and the whole suite is run on the merged tree. Step 5.8 has the result.

On 2026-11-18 at 19:21:34Z the date limit was squash-merged (#189, `3e35071`), and GitHub moved this
pull request to `master`. `master` is merged in as step 6.2 says; since its tree is the date limit's
last commit, which this branch holds, nothing outside the records changes. Step 5.9 has it.

#202 (records) reached `master` just before that merge was pushed, and is merged in on top in the
same way. Step 5.10 has it.

`master` then moved to `6cb41c8` by pull request #199, which changes how date text is read and adds
its lines to `docs/release-notes/0.37.2.md`. It is merged in; the summary keeps #199's lines, with
this change's after them. The code merges without a conflict, and the whole suite is run on the
merged tree. Step 5.11 has the result.

`master` then moved to `1fce915` by pull request #196, which changes the public calendar and adds a
line to `docs/release-notes/0.37.2.md`. It is merged in the same way. With every change's lines the
summary ran over its 1,800 characters, so this change's two lines are made one and its step is left
out; the other changes' lines are as they wrote them. The code merges without a conflict, and the
whole suite is run on the merged tree. Step 5.12 has the result.

#201 (records) then reached `master`, and is merged in the same way. Step 5.13 has it.
