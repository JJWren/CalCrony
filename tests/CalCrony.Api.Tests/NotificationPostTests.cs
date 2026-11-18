using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>Where a notification is posted and whom it pings: its channel and its mentions are
/// set from Discord. A web caller sets neither, and a template edited from the web keeps the
/// pairs its reminders already have.</summary>
public class NotificationPostTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long DefaultChannelId = 9001;
    private const long OtherChannelId = 9002;
    private const string RoleMention = "<@&9003>";

    private HttpClient Bot => fixture.Client;

    [Fact]
    public async Task A_notification_added_from_the_web_goes_to_the_events_channel_without_mentions()
    {
        var guildId = await SeedGuildAsync(9100);
        var (creator, _) = await fixture.LoginAsync(9101, (guildId, "G", false));
        var ev = await WebCreateAsync(creator, guildId, "From the web");

        var response = await creator.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, "Doors open", RoleMention, OtherChannelId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<EventNotificationDto>())!;
        Assert.Null(dto.ChannelId);
        Assert.Null(dto.Mentions);
        Assert.Equal("Doors open", dto.Message);
        Assert.Equal(30, dto.MinutesBefore);

        var row = await NotificationRowAsync(dto.Id);
        Assert.Null(row.ChannelId);
        Assert.Null(row.Mentions);

        // Once due it is queued for the event's channel, with no mention text.
        var delivery = await SweepAndFindAsync(ev, minutesBeforeStart: 29);
        Assert.Equal(DefaultChannelId, delivery.ChannelId);
        Assert.Null(PayloadOf(delivery).Mentions);
    }

    [Fact]
    public async Task A_notification_added_by_the_bot_keeps_its_channel_and_its_mention()
    {
        var guildId = await SeedGuildAsync(9110);
        var ev = await BotCreateAsync(guildId, "From Discord");

        var response = await Bot.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, "Doors open", RoleMention, OtherChannelId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<EventNotificationDto>())!;
        Assert.Equal(OtherChannelId, dto.ChannelId);
        Assert.Equal(RoleMention, dto.Mentions);

        var delivery = await SweepAndFindAsync(ev, minutesBeforeStart: 29);
        Assert.Equal(OtherChannelId, delivery.ChannelId);
        Assert.Equal(RoleMention, PayloadOf(delivery).Mentions);
    }

    [Fact]
    public async Task A_notification_added_from_the_web_for_a_whole_series_carries_neither_onto_the_series()
    {
        var guildId = await SeedGuildAsync(9120);
        var (creator, _) = await fixture.LoginAsync(9121, (guildId, "G", false));
        var ev = await WebCreateAsync(creator, guildId, "Weekly from the web", new RecurrenceRuleDto(RecurrenceUnit.Week));

        var response = await creator.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, "Doors open", RoleMention, OtherChannelId, EditScope.Series));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<EventNotificationDto>())!;
        Assert.Null(dto.ChannelId);
        Assert.Null(dto.Mentions);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var spec = await db.SeriesNotifications.SingleAsync(s => s.SeriesId == ev.SeriesId);
        Assert.Null(spec.ChannelId);
        Assert.Null(spec.Mentions);
        Assert.Equal("Doors open", spec.Message);
    }

    [Fact]
    public async Task A_notification_added_by_the_bot_for_a_whole_series_carries_both_onto_the_series()
    {
        var guildId = await SeedGuildAsync(9130);
        var ev = await BotCreateAsync(guildId, "Weekly from Discord", new RecurrenceRuleDto(RecurrenceUnit.Week));

        var response = await Bot.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, null, RoleMention, OtherChannelId, EditScope.Series));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var spec = await db.SeriesNotifications.SingleAsync(s => s.SeriesId == ev.SeriesId);
        Assert.Equal(OtherChannelId, spec.ChannelId);
        Assert.Equal(RoleMention, spec.Mentions);
    }

    [Fact]
    public async Task A_template_edited_from_the_web_keeps_the_channel_and_mention_a_reminder_had()
    {
        var guildId = await SeedGuildAsync(9140);
        var (creator, _) = await fixture.LoginAsync(9141, (guildId, "G", false));
        var template = await TemplateWithReminderAsync(creator, guildId, "Keeps", OtherChannelId, RoleMention);

        // The web sends the pair back as it read it, with the lead time and the message changed.
        var updated = await WebUpdateAsync(creator, template.Id,
            new TemplateNotificationDto(45, "Changed on the web", RoleMention, OtherChannelId));

        var kept = Assert.Single(updated.Notifications);
        Assert.Equal(45, kept.MinutesBefore);
        Assert.Equal("Changed on the web", kept.Message);
        Assert.Equal(OtherChannelId, kept.ChannelId);
        Assert.Equal(RoleMention, kept.Mentions);
    }

    [Theory]
    [InlineData(1, 9004L, RoleMention)] // another channel with the stored mention
    [InlineData(2, OtherChannelId, "<@&9005>")] // the stored channel with another mention
    [InlineData(3, OtherChannelId, null)] // the stored channel alone
    [InlineData(4, null, RoleMention)] // the stored mention alone
    [InlineData(5, 9004L, "<@&9005>")] // neither of them stored
    public async Task A_template_edited_from_the_web_takes_no_channel_or_mention_its_reminders_did_not_have(
        int row, long? channelId, string? mentions)
    {
        var guildId = await SeedGuildAsync(9150 + row);
        var (creator, _) = await fixture.LoginAsync(9160 + row, (guildId, "G", false));
        var template = await TemplateWithReminderAsync(creator, guildId, $"Takes none {row}", OtherChannelId, RoleMention);

        var updated = await WebUpdateAsync(creator, template.Id,
            new TemplateNotificationDto(30, "Doors open", mentions, channelId));

        var stored = Assert.Single(updated.Notifications);
        Assert.Null(stored.ChannelId);
        Assert.Null(stored.Mentions);
        Assert.Equal("Doors open", stored.Message);
    }

    [Fact]
    public async Task A_template_edited_from_the_web_keeps_each_stored_pair_for_one_reminder()
    {
        var guildId = await SeedGuildAsync(9170);
        var (creator, _) = await fixture.LoginAsync(9171, (guildId, "G", false));
        var template = await TemplateWithReminderAsync(creator, guildId, "Once each", OtherChannelId, RoleMention);

        // One reminder is stored with the pair; the new list asks for it three times, and adds
        // a reminder without either.
        var updated = await WebUpdateAsync(creator, template.Id,
            new TemplateNotificationDto(10, "third", RoleMention, OtherChannelId),
            new TemplateNotificationDto(60, "first", RoleMention, OtherChannelId),
            new TemplateNotificationDto(30, "second", RoleMention, OtherChannelId),
            new TemplateNotificationDto(5, "plain", null, null));

        Assert.Equal(4, updated.Notifications.Count);
        Assert.Equal(new[] { "first", "second", "third", "plain" }, updated.Notifications.Select(n => n.Message));
        // The list is taken longest lead time first, and the one stored pair goes to the first.
        Assert.Equal(OtherChannelId, updated.Notifications[0].ChannelId);
        Assert.Equal(RoleMention, updated.Notifications[0].Mentions);
        Assert.All(updated.Notifications.Skip(1), n =>
        {
            Assert.Null(n.ChannelId);
            Assert.Null(n.Mentions);
        });
    }

    [Fact]
    public async Task A_template_edited_by_the_bot_takes_the_channel_and_mentions_it_is_given()
    {
        var guildId = await SeedGuildAsync(9180);
        var ev = await BotCreateAsync(guildId, "Source");
        var save = await Bot.PostAsJsonAsync($"/guilds/{guildId}/templates", new SaveTemplateRequest(9181, "From the bot", ev.Id));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var template = (await save.Content.ReadFromJsonAsync<EventTemplateDto>())!;

        var response = await Bot.PatchAsJsonAsync($"/templates/{template.Id}", new UpdateTemplateRequest(
            9181, Notifications: [new TemplateNotificationDto(30, null, RoleMention, OtherChannelId)]));
        response.EnsureSuccessStatusCode();

        var stored = Assert.Single((await response.Content.ReadFromJsonAsync<EventTemplateDto>())!.Notifications);
        Assert.Equal(OtherChannelId, stored.ChannelId);
        Assert.Equal(RoleMention, stored.Mentions);
    }

    [Fact]
    public async Task An_event_made_from_a_template_gets_the_channel_and_mention_the_template_holds()
    {
        var guildId = await SeedGuildAsync(9190);
        var (creator, _) = await fixture.LoginAsync(9191, (guildId, "G", false));
        var template = await TemplateWithReminderAsync(creator, guildId, "Carries", OtherChannelId, RoleMention);

        // Made from the web: what the template holds was set from Discord, and is copied as it is.
        var create = await creator.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, "", "in 3 hours", 0, TemplateId: template.Id));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        var copied = Assert.Single(
            (await creator.GetFromJsonAsync<List<EventNotificationDto>>($"/events/{ev.Id}/notifications"))!);
        Assert.Equal(OtherChannelId, copied.ChannelId);
        Assert.Equal(RoleMention, copied.Mentions);
    }

    [Fact]
    public async Task A_repeating_event_made_from_a_template_hands_the_pair_on_to_its_series_and_its_next_occurrence()
    {
        var guildId = await SeedGuildAsync(9195);
        var (creator, _) = await fixture.LoginAsync(9196, (guildId, "G", false));
        var template = await TemplateWithReminderAsync(creator, guildId, "Carries on", OtherChannelId, RoleMention);

        var create = await creator.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, "", "in 3 hours", 0, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), TemplateId: template.Id));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var first = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        // The series keeps the pair for the occurrences to come…
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var spec = await db.SeriesNotifications.SingleAsync(s => s.SeriesId == first.SeriesId);
            Assert.Equal(OtherChannelId, spec.ChannelId);
            Assert.Equal(RoleMention, spec.Mentions);
        }

        // …and the next one has it: skipping the first makes the series spawn it.
        var skip = await creator.PostAsync($"/events/{first.Id}/skip", null);
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.NotEqual(first.Id, next.Id);
        var inherited = Assert.Single(
            (await creator.GetFromJsonAsync<List<EventNotificationDto>>($"/events/{next.Id}/notifications"))!);
        Assert.Equal(OtherChannelId, inherited.ChannelId);
        Assert.Equal(RoleMention, inherited.Mentions);
    }

    [Fact]
    public async Task A_notification_delivery_names_its_events_server()
    {
        var guildId = await SeedGuildAsync(9200);
        var ev = await BotCreateAsync(guildId, "Names its server");
        (await Bot.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(30)))
            .EnsureSuccessStatusCode();

        var delivery = await SweepAndFindAsync(ev, minutesBeforeStart: 29);

        Assert.Equal(guildId, PayloadOf(delivery).GuildId);
    }

    [Fact]
    public void A_notification_payload_written_without_a_server_reads_as_having_none()
    {
        // A payload written without the field.
        var eventId = Guid.NewGuid();
        var json = $$"""{"EventId":"{{eventId}}","Title":"Without the field","StartsAtUnix":1800000000,"Message":null,"Mentions":"@here"}""";

        var payload = JsonSerializer.Deserialize<EventNotificationPayload>(json)!;

        Assert.Null(payload.GuildId);
        Assert.Equal(eventId, payload.EventId);
        Assert.Equal("@here", payload.Mentions);
    }

    private static EventNotificationPayload PayloadOf(Delivery delivery) =>
        JsonSerializer.Deserialize<EventNotificationPayload>(delivery.PayloadJson)!;

    private async Task<long> SeedGuildAsync(long guildId)
    {
        var response = await Bot.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", DefaultChannelId));
        response.EnsureSuccessStatusCode();
        return guildId;
    }

    private static async Task<EventDto> WebCreateAsync(
        HttpClient creator, long guildId, string title, RecurrenceRuleDto? recurrence = null)
    {
        var create = await creator.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, title, "in 3 hours", 0, Recurrence: recurrence));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> BotCreateAsync(long guildId, string title, RecurrenceRuleDto? recurrence = null)
    {
        var create = await Bot.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(guildId + 1, title, "in 3 hours", DefaultChannelId, Recurrence: recurrence));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<EventDto>())!;
    }

    /// <summary>A template the web member owns, with one reminder whose channel and mention were
    /// set from Discord: the bot adds the reminder to the member's event, and the member saves
    /// the template from it.</summary>
    private async Task<EventTemplateDto> TemplateWithReminderAsync(
        HttpClient owner, long guildId, string name, long channelId, string mentions)
    {
        var ev = await WebCreateAsync(owner, guildId, $"Source of {name}");
        (await Bot.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(15, "Stored", mentions, channelId))).EnsureSuccessStatusCode();

        var save = await owner.PostAsJsonAsync($"/guilds/{guildId}/templates", new SaveTemplateRequest(0, name, ev.Id));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var template = (await save.Content.ReadFromJsonAsync<EventTemplateDto>())!;
        var stored = Assert.Single(template.Notifications);
        Assert.Equal(channelId, stored.ChannelId);
        Assert.Equal(mentions, stored.Mentions);
        return template;
    }

    private static async Task<EventTemplateDto> WebUpdateAsync(
        HttpClient owner, Guid templateId, params TemplateNotificationDto[] notifications)
    {
        var response = await owner.PatchAsJsonAsync($"/templates/{templateId}",
            new UpdateTemplateRequest(0, Notifications: notifications));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventTemplateDto>())!;
    }

    private async Task<EventNotification> NotificationRowAsync(Guid notificationId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.EventNotifications.AsNoTracking().SingleAsync(n => n.Id == notificationId);
    }

    /// <summary>Runs one sweep at a moment the given number of minutes before the event starts,
    /// and returns the notification delivery it queued for that event.</summary>
    private async Task<Delivery> SweepAndFindAsync(EventDto ev, int minutesBeforeStart)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        var at = Instant.FromDateTimeOffset(ev.StartsAtUtc).Minus(Duration.FromMinutes(minutesBeforeStart));
        await scheduler.SweepAsync(at, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var deliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.Type == DeliveryType.EventNotification)
            .ToListAsync();
        return Assert.Single(deliveries, d => PayloadOf(d).EventId == ev.Id);
    }
}
