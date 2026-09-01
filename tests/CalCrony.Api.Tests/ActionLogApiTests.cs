using System.Net;
using System.Net.Http.Json;
using System.Text;
using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The server action log (issue #124): every user-initiated mutation writes an entry
/// naming the real actor (bot body ids or the actor header; web sessions), nameless bot calls
/// write nothing, the list is manager-only with filters and keyset paging, and the CSV export
/// renders one row per RSVP with RFC 4180 quoting and a BOM.</summary>
public class ActionLogApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long ChannelId = 13001;

    private HttpClient Bot => fixture.Client;

    [Fact]
    public async Task Bot_event_lifecycle_logs_creator_editor_and_header_actor()
    {
        const long guildId = 13100;
        var ev = await CreateEventAsync(guildId, 13101, "Raid Night");

        var edit = await Bot.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(13102, Title: "Raid Night II", WhenText: "in 4 hours"));
        edit.EnsureSuccessStatusCode();
        var delete = await SendAsActorAsync(HttpMethod.Delete, $"/events/{ev.Id}", 13103);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var page = await ListAsync(guildId);
        Assert.Equal(
            [ActionLogAction.EventDeleted, ActionLogAction.EventEdited, ActionLogAction.EventCreated],
            page.Entries.Select(e => e.Action));
        Assert.All(page.Entries, e =>
        {
            Assert.Equal(ActionSource.Discord, e.Source);
            Assert.Equal(ActionTargetType.Event, e.TargetType);
            Assert.Equal(ev.Id, e.TargetId);
            Assert.Null(e.ActorName); // never signed in to the web app
        });

        var (deleted, edited, created) = (page.Entries[0], page.Entries[1], page.Entries[2]);
        Assert.Equal(13103, deleted.ActorUserId);
        Assert.Contains("Raid Night II", deleted.Summary);
        Assert.Equal(13102, edited.ActorUserId);
        Assert.Contains("title, start", edited.Summary);
        Assert.Contains("\"title\"", edited.DetailsJson);
        Assert.Equal(13101, created.ActorUserId);
        Assert.Equal("Created “Raid Night”", created.Summary);
    }

    [Fact]
    public async Task Web_mutations_log_the_session_user_with_web_source_and_name()
    {
        const long guildId = 13110;
        await SeedGuildAsync(guildId);
        var (manager, session) = await fixture.LoginAsync(13111, (guildId, "G", true));

        var create = await manager.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, "Web Made", "in 3 hours", 0));
        create.EnsureSuccessStatusCode();
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        (await manager.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode();

        var page = await ListAsync(guildId, client: manager);
        Assert.Equal(2, page.Entries.Count);
        Assert.All(page.Entries, e =>
        {
            Assert.Equal(ActionSource.Web, e.Source);
            Assert.Equal(session.UserId, e.ActorUserId);
            Assert.Equal(session.Username, e.ActorName);
        });
    }

    [Fact]
    public async Task Series_skip_edit_stop_are_logged_and_a_repeat_stop_is_not()
    {
        const long guildId = 13120;
        var ev = await CreateEventAsync(guildId, 13121, "Weekly", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week));
        var seriesId = ev.SeriesId!.Value;

        (await SendAsActorAsync(HttpMethod.Post, $"/events/{ev.Id}/skip", 13122)).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Patch, $"/series/{seriesId}", 13123, new UpdateSeriesRequest(Interval: 2))).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Post, $"/series/{seriesId}/stop", 13124)).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Post, $"/series/{seriesId}/stop", 13124)).EnsureSuccessStatusCode();

        var page = await ListAsync(guildId);
        Assert.Equal(
            [ActionLogAction.SeriesStopped, ActionLogAction.SeriesEdited, ActionLogAction.EventSkipped, ActionLogAction.EventCreated],
            page.Entries.Select(e => e.Action));
        Assert.Equal(13124, page.Entries[0].ActorUserId);
        Assert.Equal(seriesId, page.Entries[0].TargetId);
        Assert.Equal(ActionTargetType.Series, page.Entries[0].TargetType);
        Assert.Contains("interval", page.Entries[1].Summary);
        Assert.Equal(13122, page.Entries[2].ActorUserId);
        Assert.Equal(ev.Id, page.Entries[2].TargetId);
        Assert.Contains("repeating event", page.Entries[3].Summary);
    }

    [Fact]
    public async Task Poll_lifecycle_is_logged_including_conversion()
    {
        const long guildId = 13130;
        await SeedGuildAsync(guildId);
        var create = await Bot.PostAsJsonAsync($"/guilds/{guildId}/polls",
            new CreatePollRequest(13131, "When do we raid?", ChannelId, ["tomorrow 6pm", "tomorrow 8pm"], IsTimePoll: true));
        create.EnsureSuccessStatusCode();
        var poll = (await create.Content.ReadFromJsonAsync<PollDto>())!;

        (await SendAsActorAsync(HttpMethod.Post, $"/polls/{poll.Id}/close", 13132)).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Post, $"/polls/{poll.Id}/close", 13132)).EnsureSuccessStatusCode(); // idempotent, unlogged
        var convert = await Bot.PostAsJsonAsync($"/polls/{poll.Id}/convert", new ConvertPollRequest(13133, "Raid"));
        convert.EnsureSuccessStatusCode();
        var converted = (await convert.Content.ReadFromJsonAsync<EventDto>())!;
        (await SendAsActorAsync(HttpMethod.Delete, $"/polls/{poll.Id}", 13134)).EnsureSuccessStatusCode();

        var page = await ListAsync(guildId);
        Assert.Equal(
            [ActionLogAction.PollDeleted, ActionLogAction.PollConverted, ActionLogAction.PollClosed, ActionLogAction.PollCreated],
            page.Entries.Select(e => e.Action));
        Assert.All(page.Entries, e => Assert.Equal(poll.Id, e.TargetId));
        Assert.Equal(13133, page.Entries[1].ActorUserId);
        Assert.Contains(converted.Id.ToString(), page.Entries[1].DetailsJson);
        Assert.Contains("“Raid”", page.Entries[1].Summary);
        Assert.Contains("time poll", page.Entries[3].Summary);
    }

    [Fact]
    public async Task Template_lifecycle_and_notification_changes_are_logged()
    {
        const long guildId = 13140;
        var ev = await CreateEventAsync(guildId, 13141, "Templated");

        var save = await Bot.PostAsJsonAsync($"/guilds/{guildId}/templates", new SaveTemplateRequest(13142, "Standard", ev.Id));
        save.EnsureSuccessStatusCode();
        var template = (await save.Content.ReadFromJsonAsync<EventTemplateDto>())!;
        (await Bot.PatchAsJsonAsync($"/templates/{template.Id}", new UpdateTemplateRequest(13143, Title: "New Title"))).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Delete, $"/templates/{template.Id}", 13144)).EnsureSuccessStatusCode();

        var addReminder = await SendAsActorAsync(
            HttpMethod.Post, $"/events/{ev.Id}/notifications", 13145, new CreateEventNotificationRequest(30));
        addReminder.EnsureSuccessStatusCode();
        var reminder = (await addReminder.Content.ReadFromJsonAsync<EventNotificationDto>())!;
        (await SendAsActorAsync(HttpMethod.Delete, $"/events/{ev.Id}/notifications/{reminder.Id}", 13146)).EnsureSuccessStatusCode();

        var page = await ListAsync(guildId);
        Assert.Equal(
            [
                ActionLogAction.EventEdited, ActionLogAction.EventEdited,
                ActionLogAction.TemplateDeleted, ActionLogAction.TemplateEdited, ActionLogAction.TemplateCreated,
                ActionLogAction.EventCreated,
            ],
            page.Entries.Select(e => e.Action));
        Assert.Equal(13146, page.Entries[0].ActorUserId);
        Assert.StartsWith("Removed a reminder", page.Entries[0].Summary);
        Assert.Equal(13145, page.Entries[1].ActorUserId);
        Assert.Contains("30 min before", page.Entries[1].Summary);
        // Reminder details carry the change kind and scope, not the lead time or message.
        Assert.Equal("""{"fields":["reminder added"],"scope":null}""", page.Entries[1].DetailsJson);
        Assert.Equal("""{"fields":["reminder removed"],"scope":null}""", page.Entries[0].DetailsJson);
        Assert.Equal(13144, page.Entries[2].ActorUserId);
        Assert.All(page.Entries.Skip(2).Take(3), e => Assert.Equal(template.Id, e.TargetId));
        Assert.Contains("“Standard”", page.Entries[3].Summary);
    }

    [Fact]
    public async Task Settings_and_public_calendar_log_only_real_changes()
    {
        const long guildId = 13150;
        var settings = new GuildSettingsDto("Europe/Berlin", ChannelId, false);
        (await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/settings", 13151, settings)).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/settings", 13151, settings)).EnsureSuccessStatusCode(); // no-op
        (await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/settings", 13152, settings with { MirrorNativeEvents = true })).EnsureSuccessStatusCode();

        (await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/public-calendar", 13153, new PublicCalendarRequest(true))).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/public-calendar", 13153, new PublicCalendarRequest(true))).EnsureSuccessStatusCode(); // already on
        (await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/public-calendar", 13153, new PublicCalendarRequest(true, Regenerate: true))).EnsureSuccessStatusCode();
        (await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/public-calendar", 13154, new PublicCalendarRequest(false))).EnsureSuccessStatusCode();

        var page = await ListAsync(guildId);
        Assert.Equal(5, page.Entries.Count);
        Assert.All(page.Entries, e =>
        {
            Assert.Equal(ActionLogAction.SettingsChanged, e.Action);
            Assert.Equal(ActionTargetType.Guild, e.TargetType);
            Assert.Null(e.TargetId);
        });
        Assert.Equal("Turned the public calendar off", page.Entries[0].Summary);
        Assert.Equal(13154, page.Entries[0].ActorUserId);
        Assert.StartsWith("Generated a new public calendar link", page.Entries[1].Summary);
        Assert.Equal("Turned the public calendar on", page.Entries[2].Summary);
        Assert.Equal("Changed server settings — native events", page.Entries[3].Summary);
        Assert.Equal(13152, page.Entries[3].ActorUserId);
        Assert.Equal("Changed server settings — timezone, default channel", page.Entries[4].Summary);
        // Details name the changed fields only — never the submitted values (the log's contract
        // and the privacy statement): no zone id, channel id, or flag value anywhere.
        Assert.Equal("""{"fields":["timezone","default channel"]}""", page.Entries[4].DetailsJson);
        Assert.Equal("""{"fields":["native events"]}""", page.Entries[3].DetailsJson);
        Assert.All(page.Entries, e =>
        {
            Assert.DoesNotContain("Europe/Berlin", e.DetailsJson ?? "");
            Assert.DoesNotContain(ChannelId.ToString(), e.DetailsJson ?? "");
            Assert.DoesNotContain("true", e.DetailsJson ?? "");
        });
    }

    [Fact]
    public async Task Bot_calls_naming_nobody_write_no_entry()
    {
        const long guildId = 13160;
        var ev = await CreateEventAsync(guildId, 13161, "Orphan");

        // A system-style bot delete with neither a body id nor the actor header.
        Assert.Equal(HttpStatusCode.NoContent, (await Bot.DeleteAsync($"/events/{ev.Id}")).StatusCode);

        var page = await ListAsync(guildId);
        Assert.Equal([ActionLogAction.EventCreated], page.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task Log_is_manager_only_with_filters_paging_and_friendly_400s()
    {
        const long guildId = 13170;
        await SeedGuildAsync(guildId);
        for (var i = 0; i < 3; i++)
        {
            var ev = await CreateEventAsync(guildId, 13171 + i, $"Paged {i}");
            (await SendAsActorAsync(HttpMethod.Delete, $"/events/{ev.Id}", 13171 + i)).EnsureSuccessStatusCode();
        }

        var (manager, _) = await fixture.LoginAsync(13180, (guildId, "G", true));
        var (member, _) = await fixture.LoginAsync(13181, (guildId, "G", false));
        var (outsider, _) = await fixture.LoginAsync(13182);

        var memberResponse = await member.GetAsync($"/guilds/{guildId}/actions");
        Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
        Assert.Contains("server managers", (await memberResponse.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/guilds/{guildId}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/guilds/{guildId}/export/events.csv")).StatusCode);

        // Filters: by action, by actor, and both.
        var created = await ListAsync(guildId, client: manager, query: "action=EventCreated");
        Assert.Equal(3, created.Entries.Count);
        Assert.All(created.Entries, e => Assert.Equal(ActionLogAction.EventCreated, e.Action));
        var byUser = await ListAsync(guildId, client: manager, query: "userId=13172");
        Assert.Equal(2, byUser.Entries.Count);
        Assert.All(byUser.Entries, e => Assert.Equal(13172, e.ActorUserId));
        var both = await ListAsync(guildId, client: manager, query: "userId=13172&action=eventdeleted");
        Assert.Single(both.Entries);

        // Keyset paging one entry at a time walks every entry exactly once, newest first.
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(guildId, client: manager, query: $"limit=1{(cursor is null ? "" : $"&before={Uri.EscapeDataString(cursor)}")}");
            Assert.Single(page.Entries);
            seen.Add(page.Entries[0].Id);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        var all = await ListAsync(guildId, client: manager);
        Assert.Null(all.NextCursor);
        Assert.Equal(all.Entries.Select(e => e.Id), seen);
        Assert.Equal(6, seen.Distinct().Count());

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"/guilds/{guildId}/actions?action=Sneezed")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"/guilds/{guildId}/actions?before=garbage")).StatusCode);
    }

    [Fact]
    public async Task Csv_export_has_bom_quoting_custom_options_waitlist_and_rsvp_less_events()
    {
        const long guildId = 13190;
        await SeedGuildAsync(guildId);
        var raid = await CreateEventAsync(guildId, 13191, "Raid, \"Night\"", rsvpOptions:
        [
            new RsvpOptionSpec("⚔️", "Raider", Capacity: 1, IsAttending: true),
            new RsvpOptionSpec("❌", "Out"),
        ]);
        var raider = raid.Options.Single(o => o.IsAttending);
        (await Bot.PutAsJsonAsync($"/events/{raid.Id}/rsvps/13192", new RsvpRequest(raider.Id))).EnsureSuccessStatusCode();
        (await Bot.PutAsJsonAsync($"/events/{raid.Id}/rsvps/13193", new RsvpRequest(raider.Id))).EnsureSuccessStatusCode(); // waitlisted
        var quiet = await CreateEventAsync(guildId, 13191, "Quiet One", whenText: "in 2 days");

        var (manager, session) = await fixture.LoginAsync(13194, (guildId, "G", true));
        var response = await manager.GetAsync($"/guilds/{guildId}/export/events.csv");
        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains($"calcrony-events-{guildId}-", response.Content.Headers.ContentDisposition.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            "event_id,title,starts_at_utc,time_zone,duration_minutes,location,status,series_id,channel_id,creator_id," +
            "rsvp_option_emote,rsvp_option_label,rsvp_option_is_attending,rsvp_user_id,rsvp_waitlisted,rsvp_created_utc",
            lines[0]);
        Assert.Equal(4, lines.Length); // header + two RSVP rows + one RSVP-less row

        var seated = lines.Single(l => l.Contains(Snowflake(13192)));
        Assert.StartsWith($"{raid.Id},\"Raid, \"\"Night\"\"\",", seated);
        Assert.Contains($",⚔️,Raider,true,{Snowflake(13192)},false,", seated);
        var waitlisted = lines.Single(l => l.Contains(Snowflake(13193)));
        Assert.Contains($",⚔️,Raider,true,{Snowflake(13193)},true,", waitlisted);
        var noRsvps = lines.Single(l => l.StartsWith(quiet.Id.ToString()));
        Assert.EndsWith($",{Snowflake(ChannelId)},{Snowflake(13191)},,,,,,", noRsvps);
        Assert.Contains(",Quiet One,", noRsvps);

        // The download itself is a management action other managers can see.
        var page = await ListAsync(guildId, client: manager, query: "action=EventsExported");
        var export = Assert.Single(page.Entries);
        Assert.Equal(session.UserId, export.ActorUserId);
        Assert.Equal("Exported the events CSV", export.Summary);
    }

    [Fact]
    public async Task Entries_report_whether_their_target_still_exists_at_read_time()
    {
        const long guildId = 13210;
        var ev = await CreateEventAsync(guildId, 13211, "Fleeting");

        var before = await ListAsync(guildId);
        Assert.True(Assert.Single(before.Entries).TargetExists);

        (await SendAsActorAsync(HttpMethod.Delete, $"/events/{ev.Id}", 13211)).EnsureSuccessStatusCode();

        // The older "created" entry outlives its event — existence is a fact about now, not
        // about the entry's own action.
        var after = await ListAsync(guildId);
        Assert.Equal(2, after.Entries.Count);
        Assert.All(after.Entries, e => Assert.False(e.TargetExists));
        var settings = await SendAsActorAsync(HttpMethod.Put, $"/guilds/{guildId}/settings", 13211, new GuildSettingsDto("Europe/Oslo", null));
        settings.EnsureSuccessStatusCode();
        Assert.True((await ListAsync(guildId, query: "action=SettingsChanged")).Entries.Single().TargetExists);
    }

    [Fact]
    public async Task Csv_export_streams_a_few_hundred_events_across_chunk_boundaries_without_loss_or_repeats()
    {
        const long guildId = 13220;
        const int eventCount = ActionLogEndpoints.ExportChunkSize + 150; // crosses one chunk boundary
        var baseStart = Instant.FromUtc(2026, 9, 1, 18, 0);
        var seeded = new List<Event>();
        await using (var seed = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.Guilds.Add(new Guild { Id = guildId });
            for (var i = 0; i < eventCount; i++)
            {
                var going = new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", IsAttending = true };
                var ev = new Event
                {
                    Id = Guid.NewGuid(),
                    GuildId = guildId,
                    CreatorId = 13221,
                    Title = $"Bulk {i:000}",
                    // Five events share each start instant so the keyset tiebreak on Id is exercised.
                    StartsAt = baseStart.Plus(Duration.FromHours(i / 5)),
                    TimeZone = "UTC",
                    ChannelId = ChannelId,
                    Status = EventStatus.Scheduled,
                    CreatedAt = baseStart,
                    Options = [going, new RsvpOption { Id = Guid.NewGuid(), Emote = "❌", Label = "Out", SortOrder = 1 }],
                    // Every third event has two RSVPs; the rest have none.
                    Rsvps = i % 3 == 0
                        ?
                        [
                            new Rsvp { Id = Guid.NewGuid(), UserId = 20000 + i, OptionId = going.Id, CreatedAt = baseStart },
                            new Rsvp { Id = Guid.NewGuid(), UserId = 30000 + i, OptionId = going.Id, CreatedAt = baseStart.Plus(Duration.FromMinutes(1)) },
                        ]
                        : [],
                };
                seeded.Add(ev);
                db.Events.Add(ev);
            }

            await db.SaveChangesAsync();
        }

        var (manager, _) = await fixture.LoginAsync(13222, (guildId, "G", true));
        var response = await manager.GetAsync($"/guilds/{guildId}/export/events.csv");
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var expectedRows = seeded.Sum(e => Math.Max(1, e.Rsvps.Count));
        Assert.Equal(1 + expectedRows, lines.Length);

        // Every event appears exactly as often as its RSVP count (or once), in event-id order —
        // no chunk boundary dropped or repeated anything.
        var rowsByEvent = lines.Skip(1).GroupBy(l => Guid.Parse(l[..36])).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal(eventCount, rowsByEvent.Count);
        foreach (var ev in seeded)
        {
            var rows = rowsByEvent[ev.Id];
            Assert.Equal(Math.Max(1, ev.Rsvps.Count), rows.Count);
            foreach (var rsvp in ev.Rsvps)
            {
                Assert.Contains(rows, r => r.Contains($",✅,Going,true,{Snowflake(rsvp.UserId)},false,"));
            }
        }

        // Output is in event-id order — the immutable keyset the walk pages by (see CsvExport).
        var expectedOrder = seeded.OrderBy(e => e.Id).Select(e => e.Id).ToList();
        Assert.Equal(expectedOrder, lines.Skip(1).Select(l => Guid.Parse(l[..36])).Distinct().ToList());
        Assert.Equal("Exported the events CSV", (await ListAsync(guildId, client: manager, query: "action=EventsExported")).Entries.Single().Summary);
    }

    [Fact]
    public async Task Csv_export_is_exactly_once_when_start_times_move_between_chunks()
    {
        const long guildId = 13230;
        const int eventCount = ActionLogEndpoints.ExportChunkSize + 20; // two chunks
        var baseStart = Instant.FromUtc(2026, 10, 1, 18, 0);
        var seeded = new List<Event>();
        await using (var seed = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.Guilds.Add(new Guild { Id = guildId });
            for (var i = 0; i < eventCount; i++)
            {
                var ev = new Event
                {
                    Id = Guid.NewGuid(), GuildId = guildId, CreatorId = 13231, Title = $"Mover {i:000}",
                    StartsAt = baseStart.Plus(Duration.FromHours(i)), TimeZone = "UTC", ChannelId = ChannelId,
                    Status = EventStatus.Scheduled, CreatedAt = baseStart,
                    Options = [new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", IsAttending = true }],
                };
                seeded.Add(ev);
                db.Events.Add(ev);
            }

            await db.SaveChangesAsync();
        }

        // The walk pages by id, so "first chunk" = the 200 smallest ids. Between the two chunks
        // the hook moves an already-exported event far into the future and a not-yet-exported one
        // far into the past — the exact edits that would double or drop a row under a StartsAt
        // cursor.
        var byId = seeded.OrderBy(e => e.Id).ToList();
        var exported = byId[0];
        var pending = byId[^1];
        var movedForward = baseStart.Plus(Duration.FromDays(400));
        var movedBack = baseStart.Minus(Duration.FromDays(400));
        // The production singleton is the seam: set the delegate for this request, clear it after
        // (a second host via WithWebHostBuilder would rebuild the fixture's fakes under the base
        // server's feet).
        var hook = fixture.Factory.Services.GetRequiredService<ExportChunkHook>();
        var hookRan = false;
        hook.AfterChunkFlushed = async (chunk, ct) =>
        {
            if (chunk != 0)
            {
                return;
            }

            hookRan = true;
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Events.Where(e => e.Id == exported.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, movedForward), ct);
            await db.Events.Where(e => e.Id == pending.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, movedBack), ct);
        };

        string[] lines;
        try
        {
            var (manager, _) = await fixture.LoginAsync(13232, (guildId, "G", true));
            var response = await manager.GetAsync($"/guilds/{guildId}/export/events.csv");
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            hook.AfterChunkFlushed = null;
        }

        Assert.True(hookRan);
        var ids = lines.Skip(1).Select(l => Guid.Parse(l[..36])).ToList();
        Assert.Equal(eventCount, ids.Count);
        Assert.Equal(eventCount, ids.Distinct().Count());
        Assert.Equal(byId.Select(e => e.Id), ids); // every event exactly once, id order
        // The early event went out before its edit (its original start); the late one carries
        // the start it was moved to — under a StartsAt cursor the first would have repeated at
        // the end and the second would never have appeared.
        Assert.Contains(lines, l => l.StartsWith(exported.Id.ToString()) && l.Contains(NodaTime.Text.InstantPattern.General.Format(exported.StartsAt)));
        Assert.Contains(lines, l => l.StartsWith(pending.Id.ToString()) && l.Contains(NodaTime.Text.InstantPattern.General.Format(movedBack)));
    }

    [Fact]
    public async Task Series_scope_reminder_removal_retires_spec_reminder_and_logs_in_one_request()
    {
        const long guildId = 13240;
        var ev = await CreateEventAsync(guildId, 13241, "Reminded Weekly", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week));
        var add = await SendAsActorAsync(
            HttpMethod.Post, $"/events/{ev.Id}/notifications", 13241, new CreateEventNotificationRequest(45, Scope: EditScope.Series));
        add.EnsureSuccessStatusCode();
        var reminder = (await add.Content.ReadFromJsonAsync<EventNotificationDto>())!;

        Guid specId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            specId = (await db.EventNotifications.SingleAsync(n => n.Id == reminder.Id)).SeriesNotificationId!.Value;
            Assert.True(await db.SeriesNotifications.AnyAsync(s => s.Id == specId));
        }

        var remove = await SendAsActorAsync(
            HttpMethod.Delete, $"/events/{ev.Id}/notifications/{reminder.Id}?scope=Series", 13242);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        // All three effects of the one SaveChanges: the reminder row, its series spec, and the
        // audit entry (the spec is removed through the tracker, never a separate ExecuteDelete).
        await using (var verify = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            Assert.False(await db.EventNotifications.AnyAsync(n => n.Id == reminder.Id));
            Assert.False(await db.SeriesNotifications.AnyAsync(s => s.Id == specId));
            var entry = await db.ActionLogEntries.SingleAsync(a =>
                a.GuildId == guildId && a.TargetId == ev.Id && a.Action == ActionLogAction.EventEdited && a.ActorUserId == 13242);
            Assert.Contains("Removed a reminder", entry.Summary);
            Assert.Equal("""{"fields":["reminder removed"],"scope":"Series"}""", entry.DetailsJson);
        }
    }

    [Fact]
    public async Task Live_list_lifecycle_logs_the_person_but_not_the_bots_own_cleanup()
    {
        const long guildId = 13280;
        const long listChannel = 13281;

        var created = await Bot.PostAsJsonAsync(
            $"/guilds/{guildId}/livelists", new CreateLiveListRequest(13282, listChannel, 900, 5));
        created.EnsureSuccessStatusCode();
        var list = (await created.Content.ReadFromJsonAsync<LiveListDto>())!;

        var createdEntry = (await ListAsync(guildId, query: "action=LiveListCreated")).Entries.Single();
        Assert.Equal(13282, createdEntry.ActorUserId);
        Assert.Equal(ActionTargetType.LiveList, createdEntry.TargetType);
        Assert.Equal(list.Id, createdEntry.TargetId);
        Assert.Equal("Created a live list (showing up to 5 events)", createdEntry.Summary);
        Assert.Equal($$"""{"channelId":{{listChannel}}}""", createdEntry.DetailsJson);

        // The bot's own cleanup of a hand-deleted message carries no actor header, and nameless
        // entries are dropped — the log shows removals people made, not the bot tidying up.
        (await Bot.DeleteAsync($"/livelists/{list.Id}")).EnsureSuccessStatusCode();
        Assert.Empty((await ListAsync(guildId, query: "action=LiveListRemoved")).Entries);

        // A person running /livelist remove is named.
        var second = await Bot.PostAsJsonAsync(
            $"/guilds/{guildId}/livelists", new CreateLiveListRequest(13282, listChannel, 901, 5));
        second.EnsureSuccessStatusCode();
        var secondList = (await second.Content.ReadFromJsonAsync<LiveListDto>())!;
        (await SendAsActorAsync(HttpMethod.Delete, $"/livelists/{secondList.Id}", 13283)).EnsureSuccessStatusCode();

        var removed = (await ListAsync(guildId, query: "action=LiveListRemoved")).Entries.Single();
        Assert.Equal(13283, removed.ActorUserId);
        Assert.Equal(secondList.Id, removed.TargetId);
        Assert.Equal("Removed a live list", removed.Summary);
    }

    [Fact]
    public async Task Logged_scope_is_the_one_applied_not_the_one_requested()
    {
        const long guildId = 13270;

        // A one-off event accepts a stray Scope=Series that governs nothing: the entry must not
        // claim a series edit, or the machine-readable audit contradicts what happened.
        var oneOff = await CreateEventAsync(guildId, 13271, "Standalone");
        (await SendAsActorAsync(
            HttpMethod.Patch, $"/events/{oneOff.Id}", 13272,
            new UpdateEventRequest(13272, Title: "Standalone II", Scope: EditScope.Series)))
            .EnsureSuccessStatusCode();
        (await SendAsActorAsync(
            HttpMethod.Post, $"/events/{oneOff.Id}/notifications", 13272,
            new CreateEventNotificationRequest(30, Scope: EditScope.Series)))
            .EnsureSuccessStatusCode();

        // Only scope-bearing entries are asserted; creation carries no scope at all.
        var standalone = (await ListAsync(guildId, query: "action=EventEdited")).Entries;
        Assert.All(standalone, e => Assert.Contains("\"scope\":null", e.DetailsJson));
        Assert.DoesNotContain(standalone, e => e.Summary.Contains("whole series"));

        // The same request on a live series occurrence does apply, and is logged as Series.
        var repeating = await CreateEventAsync(
            guildId, 13273, "Repeats", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week));
        (await SendAsActorAsync(
            HttpMethod.Patch, $"/events/{repeating.Id}", 13274,
            new UpdateEventRequest(13274, Title: "Repeats II", Scope: EditScope.Series)))
            .EnsureSuccessStatusCode();

        var applied = (await ListAsync(guildId, query: "action=EventEdited")).Entries[0];
        Assert.Contains("\"scope\":\"Series\"", applied.DetailsJson);
        Assert.Contains("whole series", applied.Summary);

        // Occurrence scope on the same event logs Occurrence, not null.
        (await SendAsActorAsync(
            HttpMethod.Patch, $"/events/{repeating.Id}", 13275,
            new UpdateEventRequest(13275, Title: "Repeats III", Scope: EditScope.Occurrence)))
            .EnsureSuccessStatusCode();
        Assert.Contains(
            "\"scope\":\"Occurrence\"",
            (await ListAsync(guildId, query: "action=EventEdited")).Entries[0].DetailsJson);
    }

    [Fact]
    public async Task Day_set_only_series_edit_names_the_field_it_changed()
    {
        const long guildId = 13250;
        var ev = await CreateEventAsync(guildId, 13251, "Gym", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week));

        var edit = await SendAsActorAsync(
            HttpMethod.Patch, $"/series/{ev.SeriesId}", 13252,
            new UpdateSeriesRequest(DaysOfWeek: RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday));
        edit.EnsureSuccessStatusCode();

        var entry = (await ListAsync(guildId, query: "action=SeriesEdited")).Entries.Single();
        Assert.Equal(13252, entry.ActorUserId);
        Assert.Equal("Changed the schedule of “Gym” — days of week", entry.Summary);
        Assert.Equal("""{"fields":["days of week"]}""", entry.DetailsJson);
    }

    [Fact]
    public async Task Csv_export_streams_an_event_with_more_rsvps_than_one_rsvp_page_once_each_in_id_order()
    {
        const long guildId = 13260;
        const int rsvpCount = ActionLogEndpoints.ExportRsvpPageSize + 50; // spans two RSVP pages
        var baseStart = Instant.FromUtc(2026, 11, 1, 18, 0);
        var going = new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", IsAttending = true };
        var crowded = new Event
        {
            Id = Guid.NewGuid(), GuildId = guildId, CreatorId = 13261, Title = "Crowded", StartsAt = baseStart,
            TimeZone = "UTC", ChannelId = ChannelId, Status = EventStatus.Scheduled, CreatedAt = baseStart,
            Options = [going],
            Rsvps = [.. Enumerable.Range(0, rsvpCount).Select(i => new Rsvp
            {
                Id = Guid.NewGuid(), UserId = 40000 + i, OptionId = going.Id, CreatedAt = baseStart.Plus(Duration.FromSeconds(i)),
            })],
        };
        // A second, RSVP-less event so the merge has to settle an event after the big one.
        var lonely = new Event
        {
            Id = Guid.NewGuid(), GuildId = guildId, CreatorId = 13261, Title = "Lonely", StartsAt = baseStart,
            TimeZone = "UTC", ChannelId = ChannelId, Status = EventStatus.Scheduled, CreatedAt = baseStart,
            Options = [new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", IsAttending = true }],
        };
        await using (var seed = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.Guilds.Add(new Guild { Id = guildId });
            db.Events.AddRange(crowded, lonely);
            await db.SaveChangesAsync();
        }

        var (manager, _) = await fixture.LoginAsync(13262, (guildId, "G", true));
        var response = await manager.GetAsync($"/guilds/{guildId}/export/events.csv");
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1 + rsvpCount + 1, lines.Length);
        var crowdedRows = lines.Skip(1).Where(l => l.StartsWith(crowded.Id.ToString())).ToList();
        Assert.Equal(rsvpCount, crowdedRows.Count);
        // Every RSVP exactly once, in RSVP-id order (the page keyset) — no page boundary repeated
        // or dropped a member. Column 13 is rsvp_user_id; the title has no comma, so a split is safe.
        var actualUsers = crowdedRows.Select(l => long.Parse(l.Split(',')[13].Trim('"', '='))).ToList();
        Assert.Equal(crowded.Rsvps.OrderBy(r => r.Id).Select(r => r.UserId), actualUsers);
        Assert.Equal(rsvpCount, actualUsers.Distinct().Count());
        Assert.Single(lines.Skip(1), l => l.StartsWith(lonely.Id.ToString()) && l.EndsWith(",,,,,,"));
    }

    /// <summary>A snowflake cell as it appears in a row: the text-literal formula, RFC 4180-quoted.</summary>
    private static string Snowflake(long id) => $"\"=\"\"{id}\"\"\"";

    private async Task SeedGuildAsync(long guildId)
    {
        var response = await Bot.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        response.EnsureSuccessStatusCode();
    }

    private async Task<EventDto> CreateEventAsync(
        long guildId,
        long creatorId,
        string title,
        string whenText = "in 3 hours",
        RecurrenceRuleDto? recurrence = null,
        IReadOnlyList<RsvpOptionSpec>? rsvpOptions = null)
    {
        var response = await Bot.PostAsJsonAsync(
            $"/guilds/{guildId}/events",
            new CreateEventRequest(creatorId, title, whenText, ChannelId, Recurrence: recurrence, RsvpOptions: rsvpOptions));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    /// <summary>A bot call carrying the actor header, the way the bot names the user behind a
    /// body-less mutation.</summary>
    private async Task<HttpResponseMessage> SendAsActorAsync(HttpMethod method, string path, long actorId, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(ActionLogHeaders.ActorUserId, actorId.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await Bot.SendAsync(request);
    }

    private async Task<ActionLogPageDto> ListAsync(long guildId, HttpClient? client = null, string? query = null)
    {
        var response = await (client ?? Bot).GetAsync($"/guilds/{guildId}/actions{(query is null ? "" : "?" + query)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ActionLogPageDto>())!;
    }
}
