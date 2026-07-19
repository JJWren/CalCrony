using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

public class TemplateApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9950;
    private const long ChannelId = 9951;
    private const long CreatorId = 9952;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Save_captures_content_notifications_and_no_rule_from_a_one_off()
    {
        var ev = await CreateEventAsync("Movie Night", description: "Bring popcorn", duration: 120, location: "The den");
        (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, "Grab seats!"))).EnsureSuccessStatusCode();
        (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(5))).EnsureSuccessStatusCode();

        var template = await SaveTemplateAsync("movies", ev.Id);

        Assert.Equal("Movie Night", template.Title);
        Assert.Equal("Bring popcorn", template.Description);
        Assert.Equal(120, template.DurationMinutes);
        Assert.Equal("The den", template.Location);
        Assert.Null(template.Recurrence);
        Assert.Equal(2, template.Notifications.Count);
        Assert.Equal(30, template.Notifications[0].MinutesBefore); // MinutesBefore desc
        Assert.Equal("Grab seats!", template.Notifications[0].Message);
    }

    [Fact]
    public async Task Save_captures_the_rule_only_from_a_live_series_occurrence()
    {
        var recurring = await CreateEventAsync("Weekly Raid", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week, 2));
        var fromSeries = await SaveTemplateAsync("raids", recurring.Id);
        Assert.NotNull(fromSeries.Recurrence);
        Assert.Equal(RecurrenceUnit.Week, fromSeries.Recurrence!.Unit);
        Assert.Equal(2, fromSeries.Recurrence.Interval);

        (await Client.PostAsync($"/series/{recurring.SeriesId}/stop", null)).EnsureSuccessStatusCode();
        var fromStopped = await SaveTemplateAsync("raids-stopped", recurring.Id);
        Assert.Null(fromStopped.Recurrence); // ended series reads as a one-off
    }

    [Fact]
    public async Task Save_validates_name_guild_and_uniqueness()
    {
        var ev = await CreateEventAsync("Validation Source");

        var blank = await Client.PostAsJsonAsync($"/guilds/{GuildId}/templates",
            new SaveTemplateRequest(CreatorId, "  ", ev.Id));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var tooLong = await Client.PostAsJsonAsync($"/guilds/{GuildId}/templates",
            new SaveTemplateRequest(CreatorId, new string('n', 65), ev.Id));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var wrongGuild = await Client.PostAsJsonAsync($"/guilds/123456/templates",
            new SaveTemplateRequest(CreatorId, "cross", ev.Id));
        Assert.Equal(HttpStatusCode.NotFound, wrongGuild.StatusCode);

        var missingEvent = await Client.PostAsJsonAsync($"/guilds/{GuildId}/templates",
            new SaveTemplateRequest(CreatorId, "ghost", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, missingEvent.StatusCode);

        await SaveTemplateAsync("Dupe Check", ev.Id);
        var ciDuplicate = await Client.PostAsJsonAsync($"/guilds/{GuildId}/templates",
            new SaveTemplateRequest(CreatorId, "dupe check", ev.Id));
        Assert.Equal(HttpStatusCode.Conflict, ciDuplicate.StatusCode);
        Assert.Contains("already exists", (await ciDuplicate.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task List_is_name_ordered_and_delete_respects_the_guard_matrix()
    {
        var ev = await CreateEventAsync("Guard Source");
        var beta = await SaveTemplateAsync("beta", ev.Id);
        await SaveTemplateAsync("alpha", ev.Id);

        var list = (await Client.GetFromJsonAsync<List<EventTemplateDto>>($"/guilds/{GuildId}/templates"))!;
        var names = list.Select(t => t.Name).ToList();
        Assert.True(names.IndexOf("alpha") < names.IndexOf("beta"));

        // Non-creator member: 403. Outsider: 404 (anti-probe). Manager: 204.
        var (member, _) = await fixture.LoginAsync(9970, (GuildId, "G", false));
        var memberDelete = await member.DeleteAsync($"/templates/{beta.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, memberDelete.StatusCode);

        var (outsider, _) = await fixture.LoginAsync(9971, (555555, "Elsewhere", true));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/templates/{beta.Id}")).StatusCode);

        var (manager, _) = await fixture.LoginAsync(9972, (GuildId, "G", true));
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/templates/{beta.Id}")).StatusCode);
    }

    [Fact]
    public async Task Create_with_template_fills_gaps_and_explicit_fields_win()
    {
        var source = await CreateEventAsync("Game Night", description: "BYO controller", duration: 90, location: "Lounge");
        var template = await SaveTemplateAsync("games", source.Id);

        // Blank title + no description → template values; explicit location wins.
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 2 hours", ChannelId, Location: "Basement", TemplateId: template.Id));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        Assert.Equal("Game Night", ev.Title);
        Assert.Equal("BYO controller", ev.Description);
        Assert.Equal(90, ev.DurationMinutes);
        Assert.Equal("Basement", ev.Location);

        // Blank title WITHOUT a template stays rejected.
        var noTemplate = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 2 hours", ChannelId));
        Assert.Equal(HttpStatusCode.BadRequest, noTemplate.StatusCode);

        // Dangling template reference → 400.
        var dangling = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "X", "in 2 hours", ChannelId, TemplateId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, dangling.StatusCode);
        Assert.Contains("no longer exists", (await dangling.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Create_with_template_copies_notifications_and_applies_the_rule()
    {
        var source = await CreateEventAsync("Raid Setup", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week));
        (await Client.PostAsJsonAsync($"/events/{source.Id}/notifications",
            new CreateEventNotificationRequest(15, "Form up!", Scope: EditScope.Series))).EnsureSuccessStatusCode();
        var template = await SaveTemplateAsync("raid-setup", source.Id);
        Assert.Single(template.Notifications);
        Assert.NotNull(template.Recurrence);

        // Template rule applies when no explicit rule is sent; RepeatCount binds to it.
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 2 hours", ChannelId, TemplateId: template.Id, RepeatCount: 4));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.NotNull(ev.SeriesId);
        Assert.Contains("of 4", ev.RecurrenceSummary);

        // Notifications landed on the event AND the series specs (with lineage → future
        // occurrences inherit them, proven via skip).
        var notifications = (await Client.GetFromJsonAsync<List<EventNotificationDto>>($"/events/{ev.Id}/notifications"))!;
        var copied = Assert.Single(notifications);
        Assert.Equal("Form up!", copied.Message);

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        var inherited = (await Client.GetFromJsonAsync<List<EventNotificationDto>>($"/events/{next.Id}/notifications"))!;
        Assert.Equal("Form up!", Assert.Single(inherited).Message);
    }

    [Fact]
    public async Task No_recurrence_suppresses_a_template_rule_and_conflicts_with_an_explicit_one()
    {
        var source = await CreateEventAsync("Repeat Source", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Day));
        var template = await SaveTemplateAsync("daily-thing", source.Id);

        var oneOff = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 2 hours", ChannelId, TemplateId: template.Id, NoRecurrence: true));
        Assert.Equal(HttpStatusCode.Created, oneOff.StatusCode);
        Assert.Null((await oneOff.Content.ReadFromJsonAsync<EventDto>())!.SeriesId);

        // Explicit rule overrides the template's rule.
        var overridden = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "", "in 2 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Month), TemplateId: template.Id));
        Assert.Equal(HttpStatusCode.Created, overridden.StatusCode);
        Assert.StartsWith("Repeats monthly", (await overridden.Content.ReadFromJsonAsync<EventDto>())!.RecurrenceSummary);

        var conflict = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "X", "in 2 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), NoRecurrence: true));
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
    }

    private async Task<EventDto> CreateEventAsync(
        string title, string? description = null, int? duration = null, string? location = null,
        RecurrenceRuleDto? recurrence = null)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 2 hours", ChannelId, description, duration, location,
            Recurrence: recurrence));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventTemplateDto> SaveTemplateAsync(string name, Guid eventId)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/templates",
            new SaveTemplateRequest(CreatorId, name, eventId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventTemplateDto>())!;
    }
}
