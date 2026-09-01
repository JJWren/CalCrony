using System.Net;
using System.Net.Http.Json;
using System.Text;
using CalCrony.Contracts;

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
        Assert.Contains("Europe/Berlin", page.Entries[4].DetailsJson);
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

        var seated = lines.Single(l => l.Contains(",13192,"));
        Assert.StartsWith($"{raid.Id},\"Raid, \"\"Night\"\"\",", seated);
        Assert.Contains(",⚔️,Raider,true,13192,false,", seated);
        var waitlisted = lines.Single(l => l.Contains(",13193,"));
        Assert.Contains(",⚔️,Raider,true,13193,true,", waitlisted);
        var noRsvps = lines.Single(l => l.StartsWith(quiet.Id.ToString()));
        Assert.EndsWith($",{ChannelId},13191,,,,,,", noRsvps);
        Assert.Contains(",Quiet One,", noRsvps);

        // The download itself is a management action other managers can see.
        var page = await ListAsync(guildId, client: manager, query: "action=EventsExported");
        var export = Assert.Single(page.Entries);
        Assert.Equal(session.UserId, export.ActorUserId);
        Assert.Contains("2 events", export.Summary);
    }

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
