using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var delivery = Assert.Single(pending, d => IsPingFor(d, ev));
        var payload = System.Text.Json.JsonSerializer.Deserialize<EventNotificationPayload>(delivery.PayloadJson)!;
        Assert.Equal("Notify Flow", payload.Title);
        Assert.Equal("Get ready!", payload.Message);

        // A second sweep must not duplicate the notification.
        await SweepAsync();
        Assert.Single(await GetPendingAsync(), d => IsPingFor(d, ev));

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
    public async Task A_series_that_cannot_be_advanced_is_logged_and_passed_over()
    {
        // Two series whose occurrences have ended, so each is due its next one, and an ordinary
        // event due to start in the same sweep. A sweep takes the series in id order, and the one
        // that comes first is the one made to fail, so the other is reached after it.
        var one = await CreateSeriesEventAsync("Series one", new RecurrenceRuleDto(RecurrenceUnit.Day));
        var two = await CreateSeriesEventAsync("Series two", new RecurrenceRuleDto(RecurrenceUnit.Day));
        var (stuck, fine) =
            string.CompareOrdinal(one.SeriesId!.Value.ToString(), two.SeriesId!.Value.ToString()) < 0 ? (one, two) : (two, one);
        var due = await CreateEventAsync("Due in the same sweep", "in 2 hours");
        await PastDateAsync(stuck.Id, minutesAgo: 120, alsoEnd: true);
        await PastDateAsync(fine.Id, minutesAgo: 120, alsoEnd: true);
        await PastDateAsync(due.Id, minutesAgo: 5);

        // The first series gets a rule that nothing can step through: an interval of zero, which
        // the API never stores. Working out its next occurrence then fails.
        await SetSeriesIntervalAsync(stuck.SeriesId!.Value, 0);
        try
        {
            var log = await SweepWithLogAsync();

            // The other series has its next occurrence, and the event that was due has started.
            var next = Assert.Single(await LiveSeriesEventsAsync(fine.SeriesId!.Value));
            Assert.NotEqual(fine.Id, next.Id);
            var started = await Client.GetFromJsonAsync<EventDto>($"/events/{due.Id}");
            Assert.Equal(EventStatus.Started, started!.Status);

            // The series that failed is left exactly as it was — still running, nothing spawned,
            // nothing counted — to be tried again on the next sweep.
            Assert.Empty(await LiveSeriesEventsAsync(stuck.SeriesId.Value));
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var row = await db.EventSeries.SingleAsync(s => s.Id == stuck.SeriesId);
            Assert.False(row.Ended);
            Assert.Equal(1, row.OccurrenceCount);

            // And it is in the log once, as an error that names the series and its server and
            // carries what went wrong.
            var entry = Assert.Single(log, e => e.Level == LogLevel.Error);
            Assert.Contains(stuck.SeriesId.Value.ToString(), entry.Message);
            Assert.Contains($"guild {GuildId}", entry.Message);
            Assert.IsType<DivideByZeroException>(entry.Exception);
        }
        finally
        {
            // Back to a rule that can be stepped, whatever the assertions above made of it.
            await SetSeriesIntervalAsync(stuck.SeriesId.Value, 1);
        }

        // With its rule readable again, the next sweep advances it like any other.
        await SweepAsync();
        Assert.Single(await LiveSeriesEventsAsync(stuck.SeriesId.Value));
    }

    [Fact]
    public async Task A_series_that_ends_in_a_sweep_is_named_in_the_log()
    {
        // Weekly with an until-date tomorrow: once its occurrence has ended there is no next one.
        var ev = await CreateSeriesEventAsync(
            "Ends in the log", new RecurrenceRuleDto(RecurrenceUnit.Week), repeatUntil: "tomorrow");
        await PastDateAsync(ev.Id, minutesAgo: 120, alsoEnd: true);

        var log = await SweepWithLogAsync();

        var entry = Assert.Single(log, e => e.Message.Contains(ev.SeriesId!.Value.ToString()));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains($"guild {GuildId}", entry.Message);
        Assert.Contains("has ended", entry.Message);

        // Said once: the series is no longer among those the next sweep looks at.
        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Message.Contains(ev.SeriesId!.Value.ToString()));
    }

    [Fact]
    public async Task A_sweep_that_cannot_be_saved_names_the_rows_in_the_log()
    {
        var ev = await CreateEventAsync("Held title", "in 2 hours");
        await PastDateAsync(ev.Id, minutesAgo: 5);

        // For this test the database refuses a start announcement, so the sweep that starts the
        // event cannot be saved.
        const string Refusal = "refuses_start_announcements";
        await ExecuteSqlAsync($"""ALTER TABLE "Deliveries" ADD CONSTRAINT "{Refusal}" CHECK ("Type" <> 2) NOT VALID""");
        var log = new CollectingLogger();
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var scheduler = new DeliveryScheduler(db, new SeriesMaterializer(db), log);
            await Assert.ThrowsAsync<DbUpdateException>(
                () => scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None));
        }
        finally
        {
            await ExecuteSqlAsync($"""ALTER TABLE "Deliveries" DROP CONSTRAINT "{Refusal}" """);
        }

        // One error line: the row by entity, key and state, and nothing of what the row holds.
        var entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.StartsWith("The sweep could not be saved.", entry.Message);
        Assert.Matches(@"Delivery [0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12} \(Added\)", entry.Message);
        Assert.DoesNotContain("Held title", entry.Message);

        // The failure itself, which quotes what the database refused, is not attached to the
        // line: it goes on to the caller.
        Assert.Null(entry.Exception);

        // Nothing of that sweep was saved, and the next one starts the event.
        Assert.Equal(EventStatus.Scheduled, (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.Status);
        await SweepAsync();
        Assert.Equal(EventStatus.Started, (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.Status);
    }

    [Fact]
    public async Task A_started_event_ends_once_its_own_length_has_passed()
    {
        var created = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Ninety minutes", "in 2 hours", ChannelId, DurationMinutes: 90));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ev = (await created.Content.ReadFromJsonAsync<EventDto>())!;
        await PastDateAsync(ev.Id, minutesAgo: 5);
        await SweepAsync();
        var started = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(EventStatus.Started, started!.Status);
        var start = Instant.FromDateTimeOffset(started.StartsAtUtc);

        // An hour after its start, the length of an event that has none set, it is still running.
        await SweepAsync(start.Plus(Duration.FromMinutes(60)));
        Assert.Equal(EventStatus.Started, (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.Status);

        // Ninety minutes after its start it has ended.
        await SweepAsync(start.Plus(Duration.FromMinutes(90)));
        Assert.Equal(EventStatus.Ended, (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.Status);
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

    private async Task SetSeriesIntervalAsync(Guid seriesId, int interval)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.EventSeries.Where(s => s.Id == seriesId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Interval, interval));
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

    /// <summary>Runs one sweep with a scheduler built by hand around a logger that keeps what it
    /// is given, and returns those entries.</summary>
    private async Task<List<LogEntry>> SweepWithLogAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var log = new CollectingLogger();
        var scheduler = new DeliveryScheduler(db, new SeriesMaterializer(db), log);
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
        return log.Entries;
    }

    /// <summary>Whether a delivery is the notification ping of this event, in the class's channel.</summary>
    private static bool IsPingFor(DeliveryDto delivery, EventDto ev) =>
        delivery.Type == DeliveryType.EventNotification
        && delivery.ChannelId == ChannelId
        && delivery.PayloadJson.Contains(ev.Id.ToString());

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CollectingLogger : ILogger<DeliveryScheduler>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
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
