using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CalCrony.Api.Tests;

/// <summary>Request text that cannot be stored is refused with one sentence wherever it comes in —
/// every text field of a request body, and the path and query string of an address — and all other
/// text is read, stored and written back as before.</summary>
public class StorableTextApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const string Refusal = "The text contains a character that can't be saved.";

    private const string Unstorable = "\0";

    /// <summary>Unusual text that can be stored, which a member might send.</summary>
    private static readonly string[] StorableSamples =
    [
        "tab\tand\r\nline breaks",
        "control \u0001 and \u007F",
        "line\u2028separator",
        "joiner \u200D and mark \uFEFF",
        "emoji 🎉 and clef 𝄞",
        "replacement \uFFFD",
    ];

    /// <summary>Text fields the bot and a signed-in member can both send, by route.</summary>
    private static readonly string[] SharedFields =
    [
        "event title",
        "event description",
        "event location",
        "event image URL",
        "event start",
        "event RSVP cutoff",
        "event repeat end",
        "RSVP option emoji",
        "RSVP option label",
        "edited event title",
        "edited event description",
        "edited event location",
        "edited event image URL",
        "edited event start",
        "edited event RSVP cutoff",
        "edited RSVP option emoji",
        "edited RSVP option label",
        "edited series repeat end",
        "notification message",
        "notification mentions",
        "reminder text",
        "reminder time",
        "parsed date text",
        "parse time zone",
        "poll question",
        "poll option",
        "time poll option",
        "poll close",
        "added poll option",
        "added time poll option",
        "converted poll title",
        "template name",
        "edited template name",
        "edited template title",
        "edited template description",
        "edited template location",
        "edited template image URL",
        "template notification message",
        "template notification mentions",
        "user time zone",
        "user theme",
    ];

    /// <summary>Text fields only the bot sends in these tests (the member here is not a manager).</summary>
    private static readonly string[] BotFields =
    [
        "server time zone",
        "posted channel name",
        "channel name",
        "synced channel name",
        "server name",
        "live list channel name",
        "synced role name",
        "free slots day",
        "synced server name",
    ];

    // Each test case works in a server of its own, so none sees another's events or templates.
    private static long nextScene = 9_600;

    public static TheoryData<string, string> Fields => AllFields(includeServerList: true);

    // The server list is left out here: a list it accepts marks every server it leaves out absent.
    public static TheoryData<string, string> FieldsWithoutTheServerList => AllFields(includeServerList: false);

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Text_that_cannot_be_stored_is_refused(string caller, string field)
    {
        var (valid, send) = await PrepareAsync(caller, field);

        await AssertRefusedAsync(await send(valid + Unstorable));
    }

    [Theory]
    [MemberData(nameof(FieldsWithoutTheServerList))]
    public async Task The_same_request_is_accepted_when_its_text_can_be_stored(string caller, string field)
    {
        var (valid, send) = await PrepareAsync(caller, field);

        var response = await send(valid);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("browser", "sign-in start")]
    [InlineData("browser", "sign-in finish")]
    [InlineData("browser", "calendar link start")]
    [InlineData("browser", "calendar link finish")]
    [InlineData("bot", "event list")]
    [InlineData("web", "event list")]
    [InlineData("bot", "activity log")]
    [InlineData("bot", "a parameter's name")]
    public async Task Text_that_cannot_be_stored_is_refused_in_an_address(string caller, string route)
    {
        var (send, _) = await PrepareAddressAsync(caller, route);

        await AssertRefusedAsync(await send(Uri.EscapeDataString(Unstorable)));
    }

    [Theory]
    [InlineData("browser", "sign-in start")]
    [InlineData("browser", "sign-in finish")]
    [InlineData("browser", "calendar link start")]
    [InlineData("browser", "calendar link finish")]
    [InlineData("bot", "event list")]
    [InlineData("web", "event list")]
    [InlineData("bot", "activity log")]
    [InlineData("bot", "a parameter's name")]
    public async Task The_same_address_is_answered_as_before_when_its_text_can_be_stored(string caller, string route)
    {
        var (send, answered) = await PrepareAddressAsync(caller, route);

        Assert.Equal(answered, (await send("")).StatusCode);
    }

    [Fact]
    public async Task Text_that_cannot_be_stored_is_refused_in_the_path_of_an_address()
    {
        var refused = await fixture.Factory.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = "/feeds/abc" + Unstorable + ".ics";
        });
        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        var body = await new StreamReader(refused.Response.Body).ReadToEndAsync();
        Assert.Equal(Refusal, JsonSerializer.Deserialize<ErrorResponse>(body, JsonSerializerOptions.Web)!.Error);

        var answered = await fixture.Factory.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = "/feeds/abc.ics";
        });
        Assert.Equal(StatusCodes.Status404NotFound, answered.Response.StatusCode);
    }

    [Fact]
    public async Task A_request_without_credentials_is_still_answered_401()
    {
        var scene = await NewSceneAsync("bot");
        var signedOut = fixture.Factory.CreateClient();

        var body = await signedOut.PostAsJsonAsync(
            $"/guilds/{scene.GuildId}/events", NewEvent(scene) with { Title = "Title" + Unstorable });
        Assert.Equal(HttpStatusCode.Unauthorized, body.StatusCode);

        var address = await signedOut.GetAsync(
            $"/guilds/{scene.GuildId}/events?limit=5{Uri.EscapeDataString(Unstorable)}");
        Assert.Equal(HttpStatusCode.Unauthorized, address.StatusCode);
    }

    [Fact]
    public async Task The_refusal_comes_before_a_route_checks_its_caller()
    {
        var member = await NewSceneAsync("web");
        var other = await NewSceneAsync("bot");

        var elsewhere = await member.Client.PostAsJsonAsync(
            $"/guilds/{other.GuildId}/events", NewEvent(other) with { Title = "Title" + Unstorable });
        await AssertRefusedAsync(elsewhere);

        var unknown = $"/events/{Guid.NewGuid()}";
        await AssertRefusedAsync(await other.Client.PatchAsJsonAsync(
            unknown, new UpdateEventRequest(other.UserId, Title: "Title" + Unstorable)));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PatchAsJsonAsync(
            unknown, new UpdateEventRequest(other.UserId, Title: "Title"))).StatusCode);
    }

    [Fact]
    public async Task A_route_only_the_bot_may_call_still_answers_a_member_403()
    {
        var member = await NewSceneAsync("web");

        var response = await member.Client.PutAsJsonAsync(
            $"/channels/sync?source=web{Uri.EscapeDataString(Unstorable)}",
            new SyncChannelsRequest([new ChannelSnapshotDto(member.ChannelId, member.GuildId, "general")]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_refused_request_stores_nothing()
    {
        var scene = await NewSceneAsync("web");
        var kept = await CreateEventAsync(scene);

        var refusedReminder = await scene.Client.PostAsJsonAsync("/reminders", new CreateReminderRequest(
            scene.GuildId, scene.UserId, scene.ChannelId, "in 1 hour", "Stretch" + Unstorable));
        await AssertRefusedAsync(refusedReminder);

        var refusedEdit = await scene.Client.PatchAsJsonAsync(
            $"/events/{kept.Id}",
            new UpdateEventRequest(scene.UserId, Title: "Changed", Description: "Also changed" + Unstorable));
        await AssertRefusedAsync(refusedEdit);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var events = await db.Events.AsNoTracking().Where(e => e.GuildId == scene.GuildId).ToListAsync();
        var only = Assert.Single(events);
        Assert.Equal(kept.Title, only.Title);
        Assert.Null(only.Description);
        Assert.False(await db.Deliveries.AnyAsync(d => d.Type == DeliveryType.Reminder && d.ChannelId == scene.ChannelId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Other_unusual_text_is_stored_and_sent_back_as_before(int row)
    {
        var text = StorableSamples[row];
        var scene = await NewSceneAsync("web");

        var created = await scene.Client.PostAsJsonAsync(
            $"/guilds/{scene.GuildId}/events",
            NewEvent(scene) with { Title = text, Description = text, Location = text });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<EventDto>())!.Id;

        var read = (await scene.Client.GetFromJsonAsync<EventDto>($"/events/{id}"))!;
        Assert.Equal(text, read.Title);
        Assert.Equal(text, read.Description);
        Assert.Equal(text, read.Location);
    }

    [Fact]
    public void Response_text_is_written_as_before()
    {
        var api = fixture.Factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var before = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = api.Encoder };
        object[] written =
        [
            StorableSamples,
            new[] { Unstorable, "quote \" and <b>&amp;</b> 'café' +1" },
            new ErrorResponse("Unknown time zone \"x\". Use an IANA id like America/Chicago."),
        ];

        foreach (var value in written)
        {
            Assert.Equal(JsonSerializer.Serialize(value, before), JsonSerializer.Serialize(value, api));
        }
    }

    private static TheoryData<string, string> AllFields(bool includeServerList)
    {
        var data = new TheoryData<string, string>();
        foreach (var field in SharedFields)
        {
            data.Add("bot", field);
            data.Add("web", field);
        }

        foreach (var field in BotFields.Where(f => includeServerList || f != "synced server name"))
        {
            data.Add("bot", field);
        }

        return data;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Equal(Refusal, JsonSerializer.Deserialize<ErrorResponse>(body, JsonSerializerOptions.Web)!.Error);
    }

    private sealed record Scene(long GuildId, long ChannelId, long UserId, HttpClient Client);

    /// <summary>A server with a default channel, and the caller: the bot, or a member of that server
    /// who is not one of its managers.</summary>
    private async Task<Scene> NewSceneAsync(string caller)
    {
        var id = Interlocked.Increment(ref nextScene) * 10;
        var (guildId, channelId, userId) = (id, id + 1, id + 2);
        (await fixture.Client.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", channelId)))
            .EnsureSuccessStatusCode();
        var client = caller == "bot"
            ? fixture.Client
            : (await fixture.LoginAsync(userId, (guildId, "Server", false))).Client;
        return new Scene(guildId, channelId, userId, client);
    }

    private static CreateEventRequest NewEvent(Scene scene) =>
        new(scene.UserId, "Title", "in 2 hours", scene.ChannelId);

    private static async Task<T> ReadAsync<T>(Task<HttpResponseMessage> sending)
    {
        var response = await sending;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<EventDto> CreateEventAsync(Scene scene) =>
        ReadAsync<EventDto>(scene.Client.PostAsJsonAsync($"/guilds/{scene.GuildId}/events", NewEvent(scene)));

    private static async Task<EventTemplateDto> CreateTemplateAsync(Scene scene)
    {
        var ev = await CreateEventAsync(scene);
        return await ReadAsync<EventTemplateDto>(scene.Client.PostAsJsonAsync(
            $"/guilds/{scene.GuildId}/templates", new SaveTemplateRequest(scene.UserId, "Weekly", ev.Id)));
    }

    private static Task<PollDto> CreatePollAsync(Scene scene, bool timePoll) =>
        ReadAsync<PollDto>(scene.Client.PostAsJsonAsync($"/guilds/{scene.GuildId}/polls", timePoll
            ? new CreatePollRequest(scene.UserId, "When?", scene.ChannelId, ["in 5 hours", "in 7 hours"], IsTimePoll: true)
            : new CreatePollRequest(scene.UserId, "Dinner?", scene.ChannelId, ["Pizza", "Tacos"])));

    /// <summary>The request that carries the field, ready to send with any text in that field and
    /// valid text everywhere else; and the text that field takes when nothing is wrong with it.</summary>
    private async Task<(string Valid, Func<string, Task<HttpResponseMessage>> Send)> PrepareAsync(
        string caller, string field)
    {
        var scene = await NewSceneAsync(caller);
        var http = scene.Client;
        var events = $"/guilds/{scene.GuildId}/events";
        var polls = $"/guilds/{scene.GuildId}/polls";
        var user = scene.UserId;
        switch (field)
        {
            case "event title":
                return ("Title", text => http.PostAsJsonAsync(events, NewEvent(scene) with { Title = text }));
            case "event description":
                return ("About", text => http.PostAsJsonAsync(events, NewEvent(scene) with { Description = text }));
            case "event location":
                return ("Online", text => http.PostAsJsonAsync(events, NewEvent(scene) with { Location = text }));
            case "event image URL":
                return ("https://example.com/a.png", text => http.PostAsJsonAsync(events, NewEvent(scene) with { ImageUrl = text }));
            case "event start":
                return ("in 2 hours", text => http.PostAsJsonAsync(events, NewEvent(scene) with { WhenText = text }));
            case "event RSVP cutoff":
                return ("2h before", text => http.PostAsJsonAsync(
                    events, NewEvent(scene) with { WhenText = "in 3 days", RsvpCloseText = text }));
            case "event repeat end":
                return ("in 60 days", text => http.PostAsJsonAsync(events, NewEvent(scene) with
                {
                    WhenText = "in 3 days",
                    Recurrence = new RecurrenceRuleDto(RecurrenceUnit.Week),
                    RepeatUntilText = text,
                }));
            case "RSVP option emoji":
                return ("✅", text => http.PostAsJsonAsync(
                    events, NewEvent(scene) with { RsvpOptions = [new RsvpOptionSpec(text, "Going")] }));
            case "RSVP option label":
                return ("Going", text => http.PostAsJsonAsync(
                    events, NewEvent(scene) with { RsvpOptions = [new RsvpOptionSpec("✅", text)] }));
        }

        if (field.StartsWith("edited event", StringComparison.Ordinal) || field.StartsWith("edited RSVP", StringComparison.Ordinal))
        {
            var edited = $"/events/{(await CreateEventAsync(scene)).Id}";
            return field switch
            {
                "edited event title" => ("Renamed", text => http.PatchAsJsonAsync(edited, new UpdateEventRequest(user, Title: text))),
                "edited event description" => ("About", text => http.PatchAsJsonAsync(edited, new UpdateEventRequest(user, Description: text))),
                "edited event location" => ("Online", text => http.PatchAsJsonAsync(edited, new UpdateEventRequest(user, Location: text))),
                "edited event image URL" => ("https://example.com/b.png", text => http.PatchAsJsonAsync(edited, new UpdateEventRequest(user, ImageUrl: text))),
                "edited event start" => ("in 5 hours", text => http.PatchAsJsonAsync(edited, new UpdateEventRequest(user, WhenText: text))),
                "edited event RSVP cutoff" => ("1h before", text => http.PatchAsJsonAsync(edited, new UpdateEventRequest(user, RsvpCloseText: text))),
                "edited RSVP option emoji" => ("✅", text => http.PatchAsJsonAsync(
                    edited, new UpdateEventRequest(user, RsvpOptions: [new RsvpOptionSpec(text, "Coming", IsAttending: true)]))),
                "edited RSVP option label" => ("Coming", text => http.PatchAsJsonAsync(
                    edited, new UpdateEventRequest(user, RsvpOptions: [new RsvpOptionSpec("✅", text, IsAttending: true)]))),
                _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
            };
        }

        switch (field)
        {
            case "notification message":
            case "notification mentions":
            {
                var notifications = $"/events/{(await CreateEventAsync(scene)).Id}/notifications";
                return field == "notification message"
                    ? ("Soon", text => http.PostAsJsonAsync(notifications, new CreateEventNotificationRequest(30, Message: text)))
                    : ("@here", text => http.PostAsJsonAsync(notifications, new CreateEventNotificationRequest(30, Mentions: text)));
            }

            case "edited series repeat end":
            {
                var weekly = await ReadAsync<EventDto>(http.PostAsJsonAsync(
                    events, NewEvent(scene) with { WhenText = "in 3 days", Recurrence = new RecurrenceRuleDto(RecurrenceUnit.Week) }));
                return ("in 60 days", text => http.PatchAsJsonAsync(
                    $"/series/{weekly.SeriesId}", new UpdateSeriesRequest(End: SeriesEndChoice.Until, RepeatUntilText: text)));
            }

            case "reminder text":
                return ("Stretch", text => http.PostAsJsonAsync(
                    "/reminders", new CreateReminderRequest(scene.GuildId, user, scene.ChannelId, "in 1 hour", text)));
            case "reminder time":
                return ("in 1 hour", text => http.PostAsJsonAsync(
                    "/reminders", new CreateReminderRequest(scene.GuildId, user, scene.ChannelId, text, "Stretch")));
            case "parsed date text":
                return ("tomorrow 6pm", text => http.PostAsJsonAsync(
                    "/tools/parse-datetime", new ParseDateTimeRequest(text, TimeZone: "UTC")));
            case "parse time zone":
                return ("UTC", text => http.PostAsJsonAsync(
                    "/tools/parse-datetime", new ParseDateTimeRequest("tomorrow 6pm", TimeZone: text)));
            case "poll question":
                return ("Dinner?", text => http.PostAsJsonAsync(
                    polls, new CreatePollRequest(user, text, scene.ChannelId, ["Pizza", "Tacos"])));
            case "poll option":
                return ("Pizza", text => http.PostAsJsonAsync(
                    polls, new CreatePollRequest(user, "Dinner?", scene.ChannelId, [text, "Tacos"])));
            case "time poll option":
                return ("in 5 hours", text => http.PostAsJsonAsync(
                    polls, new CreatePollRequest(user, "When?", scene.ChannelId, [text, "in 7 hours"], IsTimePoll: true)));
            case "poll close":
                return ("in 1 day", text => http.PostAsJsonAsync(
                    polls, new CreatePollRequest(user, "Dinner?", scene.ChannelId, ["Pizza", "Tacos"], ClosesText: text)));
            case "added poll option":
            case "added time poll option":
            {
                var timePoll = field == "added time poll option";
                var options = $"/polls/{(await CreatePollAsync(scene, timePoll)).Id}/options";
                return (timePoll ? "in 9 hours" : "Curry", text => http.PostAsJsonAsync(options, new AddPollOptionRequest(user, text)));
            }

            case "converted poll title":
            {
                var poll = await CreatePollAsync(scene, timePoll: true);
                (await http.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();
                return ("Game night", text => http.PostAsJsonAsync(
                    $"/polls/{poll.Id}/convert", new ConvertPollRequest(user, Title: text)));
            }

            case "template name":
            {
                var ev = await CreateEventAsync(scene);
                return ("Weekly", text => http.PostAsJsonAsync(
                    $"/guilds/{scene.GuildId}/templates", new SaveTemplateRequest(user, text, ev.Id)));
            }

            case "user time zone":
                return ("UTC", text => http.PutAsJsonAsync($"/users/{user}/settings", new UserSettingsDto(text, true)));
            case "user theme":
                return ("slate", text => http.PutAsJsonAsync($"/users/{user}/settings", new UserSettingsDto("UTC", true, Theme: text)));
        }

        if (field.StartsWith("edited template", StringComparison.Ordinal) || field.StartsWith("template notification", StringComparison.Ordinal))
        {
            var template = $"/templates/{(await CreateTemplateAsync(scene)).Id}";
            return field switch
            {
                "edited template name" => ("Monthly", text => http.PatchAsJsonAsync(template, new UpdateTemplateRequest(user, Name: text))),
                "edited template title" => ("Retitled", text => http.PatchAsJsonAsync(template, new UpdateTemplateRequest(user, Title: text))),
                "edited template description" => ("About", text => http.PatchAsJsonAsync(template, new UpdateTemplateRequest(user, Description: text))),
                "edited template location" => ("Online", text => http.PatchAsJsonAsync(template, new UpdateTemplateRequest(user, Location: text))),
                "edited template image URL" => ("https://example.com/c.png", text => http.PatchAsJsonAsync(template, new UpdateTemplateRequest(user, ImageUrl: text))),
                "template notification message" => ("Soon", text => http.PatchAsJsonAsync(
                    template, new UpdateTemplateRequest(user, Notifications: [new TemplateNotificationDto(10, text, null, null)]))),
                "template notification mentions" => ("@here", text => http.PatchAsJsonAsync(
                    template, new UpdateTemplateRequest(user, Notifications: [new TemplateNotificationDto(10, null, text, null)]))),
                _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
            };
        }

        switch (field)
        {
            case "server time zone":
                return ("UTC", text => http.PutAsJsonAsync(
                    $"/guilds/{scene.GuildId}/settings", new GuildSettingsDto(text, scene.ChannelId)));
            case "posted channel name":
            {
                var ev = await CreateEventAsync(scene);
                return ("general", text => http.PutAsJsonAsync(
                    $"/events/{ev.Id}/message", new SetEventMessageRequest(scene.ChannelId, 777, text)));
            }

            case "channel name":
                return ("general", text => http.PutAsJsonAsync(
                    $"/channels/{scene.ChannelId}/name", new ChannelNameRequest(text)));
            case "synced channel name":
                return ("general", text => http.PutAsJsonAsync(
                    "/channels/sync", new SyncChannelsRequest([new ChannelSnapshotDto(scene.ChannelId, scene.GuildId, text)])));
            case "server name":
                return ("Server", text => http.PutAsJsonAsync(
                    $"/guilds/{scene.GuildId}/presence", new GuildPresenceRequest(true, text)));
            case "synced server name":
                return ("Server", text => http.PutAsJsonAsync(
                    "/guilds/presence/sync", new SyncGuildPresenceRequest([new GuildSnapshotDto(scene.GuildId, text)])));
            case "live list channel name":
                return ("events", text => http.PostAsJsonAsync(
                    $"/guilds/{scene.GuildId}/livelists", new CreateLiveListRequest(user, scene.ChannelId, 555, 5, text)));
            case "synced role name":
                (await http.PutAsJsonAsync($"/guilds/{scene.GuildId}/presence", new GuildPresenceRequest(true, "Server")))
                    .EnsureSuccessStatusCode();
                return ("Raiders", text => http.PutAsJsonAsync(
                    $"/guilds/{scene.GuildId}/roles/sync", new RoleSyncRequest([new RoleNameDto(user + 5, text)], [])));
            case "free slots day":
                return ("tomorrow", text => http.PostAsJsonAsync(
                    "/calendar/free-slots", new FreeSlotsRequest([user], user, scene.GuildId, text, 1, 30)));
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    /// <summary>The address, ready to send with any text added to one value in its query string;
    /// and how it is answered when that text is valid.</summary>
    private async Task<(Func<string, Task<HttpResponseMessage>> Send, HttpStatusCode Answered)> PrepareAddressAsync(
        string caller, string route)
    {
        var browser = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (caller == "browser")
        {
            return route switch
            {
                "sign-in start" => (extra => browser.GetAsync($"/auth/discord/start?returnUrl=/app{extra}"), HttpStatusCode.Redirect),
                "sign-in finish" => (extra => browser.GetAsync($"/auth/discord/callback?code=c&state=s{extra}"), HttpStatusCode.Redirect),
                "calendar link start" => (extra => browser.GetAsync($"/oauth/google/start?token=t{extra}"), HttpStatusCode.BadRequest),
                "calendar link finish" => (extra => browser.GetAsync($"/oauth/google/callback?code=c&state=s{extra}"), HttpStatusCode.BadRequest),
                _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
            };
        }

        var scene = await NewSceneAsync(caller);
        return route switch
        {
            "event list" => (extra => scene.Client.GetAsync($"/guilds/{scene.GuildId}/events?limit=5{extra}"), HttpStatusCode.OK),
            "activity log" => (extra => scene.Client.GetAsync($"/guilds/{scene.GuildId}/actions?action=EventCreated{extra}"), HttpStatusCode.OK),
            "a parameter's name" => (extra => scene.Client.GetAsync($"/guilds/{scene.GuildId}/events?limit{extra}=5"), HttpStatusCode.OK),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }
}
