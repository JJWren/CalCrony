using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class SchedulingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 900;
    private const long ChannelId = 901;
    private const long CreatorId = 902;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Due_notification_flows_through_outbox_to_ack()
    {
        // Event starts in ~3 hours; a 200-minutes-before notification is already due.
        var ev = await CreateEventAsync("Notify Flow", "in 3 hours");
        var createNotification = await Client.PostAsJsonAsync(
            $"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(200, "Get ready!", "@here"));
        Assert.Equal(HttpStatusCode.Created, createNotification.StatusCode);

        await SweepAsync();

        var pending = await GetPendingAsync();
        var delivery = Assert.Single(pending, d => d.Type == DeliveryType.EventNotification && d.ChannelId == ChannelId);
        var payload = System.Text.Json.JsonSerializer.Deserialize<EventNotificationPayload>(delivery.PayloadJson)!;
        Assert.Equal("Notify Flow", payload.Title);
        Assert.Equal("Get ready!", payload.Message);

        // A second sweep must not duplicate the notification.
        await SweepAsync();
        Assert.Single(await GetPendingAsync(), d => d.Type == DeliveryType.EventNotification && d.ChannelId == ChannelId);

        var ack = await Client.PostAsync($"/deliveries/{delivery.Id}/ack", null);
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        Assert.DoesNotContain(await GetPendingAsync(), d => d.Id == delivery.Id);
    }

    [Fact]
    public async Task Started_event_gets_start_ping_and_later_ends()
    {
        var ev = await CreateEventAsync("Start Ping", "in 2 hours");

        // Push the start into the past directly — the API refuses to create past events.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var pastStart = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, pastStart));
        }

        await SweepAsync();

        var afterStart = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(EventStatus.Started, afterStart!.Status);
        Assert.Contains(await GetPendingAsync(), d => d.Type == DeliveryType.EventStart);

        // Sweep far in the future (beyond the default 60-minute length): event ends.
        await SweepAsync(SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(2)));
        var afterEnd = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(EventStatus.Ended, afterEnd!.Status);
    }

    [Fact]
    public async Task Reminder_is_created_future_dated_and_not_served_early()
    {
        var response = await Client.PostAsJsonAsync("/reminders", new CreateReminderRequest(
            GuildId, CreatorId, ChannelId, "in 2 hours", "stretch your legs"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var reminder = (await response.Content.ReadFromJsonAsync<ReminderDto>())!;
        Assert.True(reminder.FireAtUtc > DateTimeOffset.UtcNow.AddMinutes(110));
        Assert.DoesNotContain(await GetPendingAsync(), d => d.Id == reminder.Id);
    }

    private async Task SweepAsync(Instant? now = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(now ?? SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }

    private async Task<List<DeliveryDto>> GetPendingAsync() =>
        (await Client.GetFromJsonAsync<List<DeliveryDto>>("/deliveries/pending?limit=50"))!;

    private async Task<EventDto> CreateEventAsync(string title, string when)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, when, ChannelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }
}
