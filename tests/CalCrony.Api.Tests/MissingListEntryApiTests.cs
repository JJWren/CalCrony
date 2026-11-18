using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Services;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>A list with an entry that is missing altogether is refused with a sentence — an event's
/// RSVP options, new or edited, and a template's notifications — and complete lists are read as
/// before.</summary>
public class MissingListEntryApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const string OptionsRefusal = "Every RSVP option needs an emoji and a label.";

    private const string NotificationsRefusal = "Every notification needs minutesBefore.";

    // Each test case works in a server of its own.
    private static long nextScene = 9_700;

    public static TheoryData<string, string, int> OptionCases
    {
        get
        {
            var data = new TheoryData<string, string, int>();
            foreach (var caller in new[] { "bot", "web" })
            {
                foreach (var action in new[]
                         {
                             "create", "edit", "edit clearing the attendee role", "edit clearing the signup restriction",
                         })
                {
                    for (var shape = 0; shape < 3; shape++)
                    {
                        data.Add(caller, action, shape);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(OptionCases))]
    public async Task An_rsvp_option_list_with_an_entry_missing_is_refused(string caller, string action, int shape)
    {
        var scene = await NewSceneAsync(caller);
        var options = OptionsWithAnEntryMissing(shape);

        HttpResponseMessage response;
        if (action == "create")
        {
            response = await scene.Client.PostAsJsonAsync(
                $"/guilds/{scene.GuildId}/events", NewEvent(scene) with { RsvpOptions = options });
        }
        else
        {
            var ev = await CreateEventAsync(scene);
            response = await scene.Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
                scene.UserId,
                RsvpOptions: options,
                ClearAttendeeRole: action == "edit clearing the attendee role",
                ClearAllowedRoles: action == "edit clearing the signup restriction"));
        }

        await AssertRefusedAsync(response, OptionsRefusal);
    }

    [Fact]
    public async Task A_missing_entry_is_answered_first_among_the_options()
    {
        var scene = await NewSceneAsync("bot");
        var events = $"/guilds/{scene.GuildId}/events";
        var elevenWithOneMissing = ElevenEntries(withOneMissing: true);

        await AssertRefusedAsync(
            await scene.Client.PostAsJsonAsync(events, NewEvent(scene) with { RsvpOptions = elevenWithOneMissing }),
            OptionsRefusal);
        await AssertRefusedAsync(
            await scene.Client.PostAsJsonAsync(events, NewEvent(scene) with { RsvpOptions = [null!], AttendeeLimit = 0 }),
            OptionsRefusal);

        var ev = await CreateEventAsync(scene);
        await AssertRefusedAsync(
            await scene.Client.PatchAsJsonAsync(
                $"/events/{ev.Id}", new UpdateEventRequest(scene.UserId, RsvpOptions: elevenWithOneMissing)),
            OptionsRefusal);
    }

    [Fact]
    public async Task Checks_an_edit_makes_before_it_reaches_the_options_keep_their_place()
    {
        var scene = await NewSceneAsync("bot");
        var ev = await CreateEventAsync(scene);

        var response = await scene.Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(scene.UserId, RsvpOptions: [null!], AttendeeLimit: 0));

        await AssertRefusedAsync(response, "The attendee limit must be at least 1.");
    }

    [Fact]
    public async Task Checks_a_new_event_makes_before_it_reaches_the_options_keep_their_place()
    {
        var member = await NewSceneAsync("web");
        var other = await NewSceneAsync("bot");

        await AssertRefusedAsync(
            await member.Client.PostAsJsonAsync(
                $"/guilds/{member.GuildId}/events", NewEvent(member) with { WhenText = "flurble", RsvpOptions = [null!] }),
            @"Could not understand ""flurble"" as a date/time.");

        var elsewhere = await member.Client.PostAsJsonAsync(
            $"/guilds/{other.GuildId}/events", NewEvent(other) with { RsvpOptions = [null!] });
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
    }

    [Fact]
    public void The_option_builder_refuses_a_list_with_an_entry_missing()
    {
        Assert.Null(RsvpPolicy.TryBuildOptions([null!], null, null, null, out var alone));
        Assert.Equal(OptionsRefusal, alone);

        Assert.Null(RsvpPolicy.TryBuildOptions(OptionsWithAnEntryMissing(1), null, null, null, out var after));
        Assert.Equal(OptionsRefusal, after);

        // How many entries there are is still checked first.
        Assert.Null(RsvpPolicy.TryBuildOptions(ElevenEntries(withOneMissing: true), null, null, null, out var count));
        Assert.Equal($"RSVP options must have between 1 and {RsvpPolicy.MaxOptions} entries.", count);
    }

    [Fact]
    public void A_stored_option_list_with_an_entry_missing_gives_the_default_options()
    {
        var options = RsvpPolicy.OptionsFromTemplate("[null]");

        Assert.Equal(["Going", "Not going", "Maybe"], options.OrderBy(o => o.SortOrder).Select(o => o.Label));
    }

    [Theory]
    [InlineData("bot", 0)]
    [InlineData("bot", 1)]
    [InlineData("bot", 2)]
    [InlineData("web", 0)]
    [InlineData("web", 1)]
    [InlineData("web", 2)]
    public async Task A_template_notification_list_with_an_entry_missing_is_refused(string caller, int shape)
    {
        var scene = await NewSceneAsync(caller);
        var template = await CreateTemplateAsync(scene);
        var soon = new TemplateNotificationDto(10, "Soon", null, null);
        IReadOnlyList<TemplateNotificationDto> notifications = shape switch
        {
            0 => [null!],
            1 => [soon, null!],
            _ => [null!, soon],
        };

        var response = await scene.Client.PatchAsJsonAsync(
            $"/templates/{template.Id}", new UpdateTemplateRequest(scene.UserId, Notifications: notifications));

        await AssertRefusedAsync(response, NotificationsRefusal);
    }

    [Fact]
    public async Task How_many_notifications_there_are_is_still_checked_first()
    {
        var scene = await NewSceneAsync("web");
        var template = await CreateTemplateAsync(scene);

        var response = await scene.Client.PatchAsJsonAsync(
            $"/templates/{template.Id}",
            new UpdateTemplateRequest(scene.UserId, Notifications:
                [null!, .. Enumerable.Range(1, 5).Select(i => new TemplateNotificationDto(i * 10, null, null, null))]));

        await AssertRefusedAsync(response, "A template can carry at most 5 notifications.");
    }

    [Fact]
    public async Task A_missing_notification_is_answered_before_each_entry_is_checked()
    {
        var scene = await NewSceneAsync("web");
        var template = await CreateTemplateAsync(scene);

        var response = await scene.Client.PatchAsJsonAsync(
            $"/templates/{template.Id}",
            new UpdateTemplateRequest(scene.UserId, Notifications: [new TemplateNotificationDto(-1, null, null, null), null!]));

        await AssertRefusedAsync(response, NotificationsRefusal);
    }

    [Fact]
    public async Task Complete_lists_are_accepted_as_before()
    {
        var scene = await NewSceneAsync("web");

        var created = await ReadAsync<EventDto>(scene.Client.PostAsJsonAsync(
            $"/guilds/{scene.GuildId}/events",
            NewEvent(scene) with { RsvpOptions = [new RsvpOptionSpec("✅", "Yes"), new RsvpOptionSpec("❌", "No")] }));
        Assert.Equal(["Yes", "No"], created.Options.OrderBy(o => o.SortOrder).Select(o => o.Label));

        var edited = await ReadAsync<EventDto>(scene.Client.PatchAsJsonAsync(
            $"/events/{created.Id}",
            new UpdateEventRequest(scene.UserId, RsvpOptions:
            [
                new RsvpOptionSpec("✅", "Yes"), new RsvpOptionSpec("🤔", "Maybe"), new RsvpOptionSpec("❌", "No"),
            ])));
        Assert.Equal(["Yes", "Maybe", "No"], edited.Options.OrderBy(o => o.SortOrder).Select(o => o.Label));

        var template = await CreateTemplateAsync(scene);
        var saved = await ReadAsync<EventTemplateDto>(scene.Client.PatchAsJsonAsync(
            $"/templates/{template.Id}",
            new UpdateTemplateRequest(scene.UserId, Notifications:
            [
                new TemplateNotificationDto(10, "Soon", null, null), new TemplateNotificationDto(60, null, "@here", null),
            ])));
        Assert.Equal([60, 10], saved.Notifications.Select(n => n.MinutesBefore));
    }

    [Fact]
    public async Task Lists_refused_today_keep_their_sentences()
    {
        var scene = await NewSceneAsync("bot");
        var events = $"/guilds/{scene.GuildId}/events";
        var countSentence = $"RSVP options must have between 1 and {RsvpPolicy.MaxOptions} entries.";

        await AssertRefusedAsync(
            await scene.Client.PostAsJsonAsync(events, NewEvent(scene) with { RsvpOptions = [] }), countSentence);
        await AssertRefusedAsync(
            await scene.Client.PostAsJsonAsync(events, NewEvent(scene) with { RsvpOptions = ElevenEntries(withOneMissing: false) }),
            countSentence);
        await AssertRefusedAsync(
            await scene.Client.PostAsJsonAsync(events, NewEvent(scene) with { RsvpOptions = [new RsvpOptionSpec(null!, null!)] }),
            OptionsRefusal);
        await AssertRefusedAsync(
            await scene.Client.PostAsJsonAsync(events, NewEvent(scene) with
            {
                RsvpOptions = [new RsvpOptionSpec("✅", "Yes", IsAttending: true), new RsvpOptionSpec("❌", "No", IsAttending: true)],
            }),
            "Only one RSVP option can be the attending option.");

        var template = $"/templates/{(await CreateTemplateAsync(scene)).Id}";
        await AssertRefusedAsync(
            await scene.Client.PatchAsJsonAsync(template, new UpdateTemplateRequest(scene.UserId, Notifications:
                [.. Enumerable.Range(1, 6).Select(i => new TemplateNotificationDto(i * 10, null, null, null))])),
            "A template can carry at most 5 notifications.");
        await AssertRefusedAsync(
            await scene.Client.PatchAsJsonAsync(template, new UpdateTemplateRequest(scene.UserId, Notifications:
                [new TemplateNotificationDto(-1, null, null, null)])),
            "minutesBefore must be between 0 and 40320 (4 weeks).");
    }

    private static IReadOnlyList<RsvpOptionSpec> OptionsWithAnEntryMissing(int shape) => shape switch
    {
        0 => [null!],
        1 => [new RsvpOptionSpec("✅", "Going"), null!],
        _ => [null!, new RsvpOptionSpec("✅", "Going")],
    };

    private static IReadOnlyList<RsvpOptionSpec> ElevenEntries(bool withOneMissing) =>
    [
        .. Enumerable.Range(1, withOneMissing ? 10 : 11).Select(i => new RsvpOptionSpec("✅", $"Option {i}")),
        .. withOneMissing ? new RsvpOptionSpec[] { null! } : [],
    ];

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string sentence)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Equal(sentence, JsonSerializer.Deserialize<ErrorResponse>(body, JsonSerializerOptions.Web)!.Error);
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
}
