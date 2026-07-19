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

    [Fact]
    public async Task Ended_series_occurrence_spawns_next_in_the_same_sweep()
    {
        var ev = await CreateSeriesEventAsync("Daily spawn", new RecurrenceRuleDto(RecurrenceUnit.Day));
        await PastDateAsync(ev.Id, minutesAgo: 120);

        // Sweep 1 flips Scheduled→Started (start ping); sweep 2 must end it AND spawn the next
        // occurrence in the same pass — the in-memory live re-filter, not the stale DB status.
        await SweepAsync();
        await SweepAsync();

        var live = await LiveSeriesEventsAsync(ev.SeriesId!.Value);
        var next = Assert.Single(live);
        Assert.NotEqual(ev.Id, next.Id);
        Assert.True(next.StartsAt > SystemClock.Instance.GetCurrentInstant());
        Assert.Contains(await GetPendingAsync(), d =>
            d.Type == DeliveryType.PostEventMessage && d.PayloadJson.Contains(next.Id.ToString()));

        // Idempotent: another sweep spawns nothing new.
        await SweepAsync();
        Assert.Single(await LiveSeriesEventsAsync(ev.SeriesId.Value));
    }

    [Fact]
    public async Task Count_exhaustion_ends_the_series()
    {
        var ev = await CreateSeriesEventAsync("Two rounds", new RecurrenceRuleDto(RecurrenceUnit.Day), repeatCount: 2);

        await PastDateAsync(ev.Id, minutesAgo: 120);
        await SweepAsync();
        await SweepAsync();
        var second = Assert.Single(await LiveSeriesEventsAsync(ev.SeriesId!.Value));

        await PastDateAsync(second.Id, minutesAgo: 120, alsoEnd: true);
        await SweepAsync();

        Assert.Empty(await LiveSeriesEventsAsync(ev.SeriesId.Value));
        var series = await Client.GetFromJsonAsync<SeriesDto>($"/series/{ev.SeriesId}");
        Assert.True(series!.Ended);
        Assert.Equal(2, series.OccurrenceCount);
    }

    [Fact]
    public async Task Until_date_reached_ends_the_series_cleanly()
    {
        // Weekly with an until-date tomorrow: the next slot (+7d) overshoots it.
        var ev = await CreateSeriesEventAsync(
            "Short lived", new RecurrenceRuleDto(RecurrenceUnit.Week), repeatUntil: "tomorrow");

        await PastDateAsync(ev.Id, minutesAgo: 120, alsoEnd: true);
        await SweepAsync();

        Assert.Empty(await LiveSeriesEventsAsync(ev.SeriesId!.Value));
        var series = await Client.GetFromJsonAsync<SeriesDto>($"/series/{ev.SeriesId}");
        Assert.True(series!.Ended);
    }

    [Fact]
    public async Task Downtime_catch_up_spawns_exactly_one_future_occurrence()
    {
        var ev = await CreateSeriesEventAsync("Stale weekly", new RecurrenceRuleDto(RecurrenceUnit.Week));

        // Simulate three weeks of downtime: the occurrence long over, the cursor three slots stale.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var staleStart = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(21));
            await db.Events.Where(e => e.Id == ev.Id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.StartsAt, staleStart)
                .SetProperty(e => e.Status, EventStatus.Ended));
            var staleDate = staleStart.InUtc().Date;
            await db.EventSeries.Where(s => s.Id == ev.SeriesId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AnchorDate, staleDate)
                .SetProperty(x => x.CurrentOccurrenceDate, staleDate));
        }

        await SweepAsync();

        var live = Assert.Single(await LiveSeriesEventsAsync(ev.SeriesId!.Value));
        Assert.True(live.StartsAt > SystemClock.Instance.GetCurrentInstant());

        // Missed slots were skipped, not materialized (or counted).
        var series = await Client.GetFromJsonAsync<SeriesDto>($"/series/{ev.SeriesId}");
        Assert.Equal(2, series!.OccurrenceCount);
    }

    [Fact]
    public async Task Partial_unique_index_rejects_a_second_live_occurrence()
    {
        var ev = await CreateSeriesEventAsync("Guarded slot", new RecurrenceRuleDto(RecurrenceUnit.Week));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        db.Events.Add(new Event
        {
            Id = Guid.NewGuid(),
            GuildId = GuildId,
            CreatorId = CreatorId,
            Title = "Impostor",
            StartsAt = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1)),
            ChannelId = ChannelId,
            Status = EventStatus.Scheduled,
            SeriesId = ev.SeriesId,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        });

        // Regression pin for IX_Events_SeriesId_Live — the enum literals in its filter included.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private async Task<EventDto> CreateSeriesEventAsync(
        string title, RecurrenceRuleDto rule, int? repeatCount = null, string? repeatUntil = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(
                CreatorId, title, "in 2 hours", ChannelId,
                Recurrence: rule, RepeatUntilText: repeatUntil, RepeatCount: repeatCount));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task PastDateAsync(Guid eventId, int minutesAgo, bool alsoEnd = false)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(minutesAgo));
        await db.Events.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.StartsAt, past)
            .SetProperty(e => e.Status, alsoEnd ? EventStatus.Ended : EventStatus.Scheduled));
    }

    private async Task<List<Event>> LiveSeriesEventsAsync(Guid seriesId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Events
            .Where(e => e.SeriesId == seriesId
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started))
            .ToListAsync();
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
