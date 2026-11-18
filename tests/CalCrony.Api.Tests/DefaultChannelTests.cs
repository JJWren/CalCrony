using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>A server's default channel is set from Discord: a settings save from the web leaves
/// it as it is. And the deliveries that go to a channel say which server they are for.</summary>
public class DefaultChannelTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long StoredChannelId = 9501;
    private const long OtherChannelId = 9502;

    private HttpClient Bot => fixture.Client;

    [Theory]
    [InlineData(1, OtherChannelId)] // another channel
    [InlineData(2, null)] // none
    public async Task A_settings_save_from_the_web_leaves_the_default_channel_as_it_is(int row, long? sent)
    {
        var guildId = await SeedGuildAsync(9510 + row, StoredChannelId);
        var (manager, _) = await fixture.LoginAsync(9520 + row, (guildId, "G", true));

        var response = await manager.PutAsJsonAsync($"/guilds/{guildId}/settings",
            new GuildSettingsDto("America/Chicago", sent, MirrorNativeEvents: true));

        // The rest of the save is taken; the default channel is as it was, in the answer and in
        // what is stored.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new GuildSettingsDto("America/Chicago", StoredChannelId, true),
            await response.Content.ReadFromJsonAsync<GuildSettingsDto>());
        Assert.Equal(
            new GuildSettingsDto("America/Chicago", StoredChannelId, true),
            await Bot.GetFromJsonAsync<GuildSettingsDto>($"/guilds/{guildId}/settings"));

        // And the server's log names what changed, which the default channel did not.
        var entry = Assert.Single(await SettingsChangesAsync(guildId), e => e.Source == ActionSource.Web);
        Assert.Contains("timezone", entry.Summary);
        Assert.Contains("native events", entry.Summary);
        Assert.DoesNotContain("default channel", entry.Summary);
    }

    [Fact]
    public async Task A_settings_save_from_the_web_sets_no_default_channel_where_none_is_stored()
    {
        var guildId = await SeedGuildAsync(9530, null);
        var (manager, _) = await fixture.LoginAsync(9531, (guildId, "G", true));

        var response = await manager.PutAsJsonAsync($"/guilds/{guildId}/settings",
            new GuildSettingsDto("UTC", OtherChannelId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<GuildSettingsDto>())!.DefaultChannelId);

        // So the web still has nowhere to post an event to.
        var create = await manager.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, "Nowhere to post", "in 3 hours", 0));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task A_settings_save_from_the_bot_sets_changes_and_clears_the_default_channel()
    {
        var guildId = await SeedGuildAsync(9540, StoredChannelId);

        var changed = await Bot.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", OtherChannelId));
        Assert.Equal(OtherChannelId, (await changed.Content.ReadFromJsonAsync<GuildSettingsDto>())!.DefaultChannelId);

        var cleared = await Bot.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", null));
        Assert.Null((await cleared.Content.ReadFromJsonAsync<GuildSettingsDto>())!.DefaultChannelId);
        Assert.Null((await Bot.GetFromJsonAsync<GuildSettingsDto>($"/guilds/{guildId}/settings"))!.DefaultChannelId);
    }

    [Fact]
    public async Task What_the_web_posts_goes_to_the_stored_default_channel_after_such_a_save()
    {
        var guildId = await SeedGuildAsync(9550, StoredChannelId);
        var (manager, _) = await fixture.LoginAsync(9551, (guildId, "G", true));
        (await manager.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", OtherChannelId)))
            .EnsureSuccessStatusCode();

        var create = await manager.PostAsJsonAsync($"/guilds/{guildId}/events",
            new CreateEventRequest(0, "Posted where the server said", "in 3 hours", 0));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(StoredChannelId, ev.ChannelId);

        var remind = await manager.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(guildId, 0, OtherChannelId, "in 2 hours", "Bring the dice"));
        Assert.Equal(HttpStatusCode.Created, remind.StatusCode);
        Assert.Equal(StoredChannelId, (await remind.Content.ReadFromJsonAsync<ReminderDto>())!.ChannelId);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var post = await db.Deliveries.AsNoTracking().SingleAsync(d =>
            d.Type == DeliveryType.PostEventMessage && d.PayloadJson.Contains(ev.Id.ToString()));
        Assert.Equal(StoredChannelId, post.ChannelId);
    }

    [Fact]
    public async Task A_reminder_delivery_names_the_server_it_was_made_in()
    {
        var guildId = await SeedGuildAsync(9560, StoredChannelId);
        var (member, session) = await fixture.LoginAsync(9561, (guildId, "G", false));

        var fromTheWeb = await member.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(guildId, 0, 0, "in 2 hours", "From the web"));
        var fromTheBot = await Bot.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(guildId, 9562, OtherChannelId, "in 2 hours", "From Discord"));
        Assert.Equal(HttpStatusCode.Created, fromTheWeb.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fromTheBot.StatusCode);

        var web = await ReminderAsync((await fromTheWeb.Content.ReadFromJsonAsync<ReminderDto>())!.Id);
        Assert.Equal(new ReminderPayload(session.UserId, "From the web", guildId), web.Payload);
        Assert.Equal(StoredChannelId, web.ChannelId);

        var bot = await ReminderAsync((await fromTheBot.Content.ReadFromJsonAsync<ReminderDto>())!.Id);
        Assert.Equal(new ReminderPayload(9562, "From Discord", guildId), bot.Payload);
        Assert.Equal(OtherChannelId, bot.ChannelId);
    }

    [Fact]
    public void A_reminder_or_promotion_payload_written_without_a_server_reads_as_having_none()
    {
        // Payloads written without the field.
        var reminder = JsonSerializer.Deserialize<ReminderPayload>("""{"UserId":5,"Text":"Without the field"}""")!;
        var promotion = JsonSerializer.Deserialize<WaitlistPromotionPayload>(
            $$"""{"EventId":"{{Guid.NewGuid()}}","UserId":5,"Title":"Without the field","StartsAtUnix":1800000000,"OptionEmote":"x","OptionLabel":"Going"}""")!;

        Assert.Null(reminder.GuildId);
        Assert.Equal("Without the field", reminder.Text);
        Assert.Null(promotion.GuildId);
        Assert.Equal("Going", promotion.OptionLabel);
    }

    private async Task<long> SeedGuildAsync(long guildId, long? defaultChannelId)
    {
        var response = await Bot.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", defaultChannelId));
        response.EnsureSuccessStatusCode();
        return guildId;
    }

    private async Task<List<ActionLogEntry>> SettingsChangesAsync(long guildId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.ActionLogEntries.AsNoTracking()
            .Where(a => a.GuildId == guildId && a.Action == ActionLogAction.SettingsChanged)
            .ToListAsync();
    }

    private async Task<(long ChannelId, ReminderPayload Payload)> ReminderAsync(Guid deliveryId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var row = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == deliveryId && d.Type == DeliveryType.Reminder);
        return (row.ChannelId, JsonSerializer.Deserialize<ReminderPayload>(row.PayloadJson)!);
    }
}
