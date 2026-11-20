using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The sweep of <see cref="DeliveryScheduler"/> takes the rows it may change before it
/// changes any of them, and works from them as they stand once it holds them. Most tests here
/// change rows between the sweep's first step, which finds what it may change, and its second,
/// which takes those rows.</summary>
public class SweepRowLockTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 910;
    private const long ChannelId = 911;
    private const long CreatorId = 912;

    private HttpClient Client => fixture.Client;

    // Of the last sweep run by SweepCountingSavesAsync: its time, and the number it returned.
    private Instant sweptAt;
    private int sweptCount;

    [Fact]
    public async Task A_reminder_deleted_before_the_sweep_takes_it_is_not_sent_and_the_rest_is_saved()
    {
        // Two things are due in one sweep: a reminder, and another event's start.
        var notified = await CreateEventAsync("Loses its reminder", "in 3 hours");
        var notification = await AddDueNotificationAsync(notified.Id);
        var starting = await CreateEventAsync("Starts in that sweep", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);

        // The reminder is deleted after the sweep has found it and before it takes it.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.DeleteAsync($"/events/{notified.Id}/notifications/{notification.Id}")).EnsureSuccessStatusCode());

        // The sweep is saved at its first try, the other event has started, nothing is sent for
        // the reminder that is gone, and nothing is said of it.
        Assert.Equal(1, saves);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(starting.Id)).Status);
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventNotification, notified.Id));
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task An_event_deleted_before_the_sweep_takes_it_is_not_started_and_the_rest_is_saved()
    {
        // Two events are due to start in one sweep. One of them has a reminder that is due as well.
        var deleted = await CreateEventAsync("Deleted as it is due", "in 2 hours");
        await AddDueNotificationAsync(deleted.Id);
        var starting = await CreateEventAsync("Starts in that sweep too", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);
        await PastDateAsync(deleted.Id, minutesAgo: 5);

        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.DeleteAsync($"/events/{deleted.Id}")).EnsureSuccessStatusCode());

        Assert.Equal(1, saves);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(starting.Id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/events/{deleted.Id}")).StatusCode);
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventStart, deleted.Id));
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventNotification, deleted.Id));
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task Many_reminders_deleted_before_the_sweep_takes_them_are_not_sent()
    {
        // Seven events, each with a reminder that is due, and an eighth due to start.
        var notifications = new List<(Guid EventId, Guid Id)>();
        for (var i = 0; i < 7; i++)
        {
            var ev = await CreateEventAsync($"One of seven, {i}", "in 3 hours");
            notifications.Add((ev.Id, (await AddDueNotificationAsync(ev.Id)).Id));
        }

        var starting = await CreateEventAsync("Starts beside the seven", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);

        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
        {
            foreach (var (eventId, id) in notifications)
            {
                (await Client.DeleteAsync($"/events/{eventId}/notifications/{id}")).EnsureSuccessStatusCode();
            }
        });

        Assert.Equal(1, saves);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(starting.Id)).Status);
        foreach (var (eventId, _) in notifications)
        {
            Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventNotification, eventId));
        }

        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task Polls_deleted_before_the_sweep_takes_them_are_not_closed_by_it()
    {
        // Two polls whose closing time has passed, each with a posted message, so a sweep that
        // closes one queues the re-render of its message; and an event due to start.
        var polls = new List<PollDto> { await CreateDuePollAsync("Deleted first?"), await CreateDuePollAsync("Deleted second?") };
        foreach (var poll in polls)
        {
            await GivePollAPostedMessageAsync(poll.Id);
        }

        var starting = await CreateEventAsync("Starts beside the polls", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);

        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
        {
            foreach (var poll in polls)
            {
                (await Client.DeleteAsync($"/polls/{poll.Id}")).EnsureSuccessStatusCode();
            }
        });

        Assert.Equal(1, saves);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(starting.Id)).Status);
        Assert.All(polls, poll => Assert.Equal(0, CountRows($"""SELECT count(*)::int AS "Value" FROM "Polls" WHERE "Id" = '{poll.Id}'""")));
        foreach (var poll in polls)
        {
            Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.SyncPollMessage, poll.Id));
        }

        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task A_series_whose_occurrence_is_deleted_before_the_sweep_takes_it_is_as_the_delete_left_it()
    {
        // A daily series with a reminder of its own. Its occurrence has started and its length
        // has passed, so this sweep is to end it and make the next one.
        var ev = await CreateSeriesEventAsync("Deleted as it ends", new RecurrenceRuleDto(RecurrenceUnit.Day));
        (await Client.PostAsJsonAsync(
            $"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, Scope: EditScope.Series))).EnsureSuccessStatusCode();
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;
        var postsBefore = await PostDeliveryIdsAsync();

        // Deleting the occurrence that is still live stops its series.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode());

        Assert.Equal(1, saves);
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);

        // The series is as the delete left it: stopped, with the one occurrence it had counted,
        // and no occurrence of it is stored.
        var series = await SeriesAsync(seriesId);
        Assert.True(series.Ended);
        Assert.Equal(1, series.OccurrenceCount);
        Assert.Equal(0, await CountOccurrencesAsync(seriesId));

        // Nothing was queued to post an occurrence that is not there.
        await AssertEveryPostQueuedSinceIsForAStoredEventAsync(postsBefore);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);

        // The next sweep takes the series as it stands: stopped.
        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
        Assert.Empty(await LiveSeriesEventsAsync(seriesId));
    }

    [Fact]
    public async Task A_series_the_sweep_would_have_ended_is_not_said_to_have_ended_when_its_occurrence_was_deleted()
    {
        // Weekly with an until-date tomorrow: once its occurrence has ended the sweep finds no
        // next one, ends the series, and says so in the log.
        var ev = await CreateSeriesEventAsync(
            "The only occurrence", new RecurrenceRuleDto(RecurrenceUnit.Week), repeatUntil: "tomorrow");
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;

        // Here the occurrence is deleted before the sweep takes it, which stops the series itself.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode());

        // The sweep ended nothing, so it says nothing of the series.
        Assert.Equal(1, saves);
        Assert.DoesNotContain(log, e => e.Message.Contains(seriesId.ToString()));
    }

    [Fact]
    public async Task A_series_whose_occurrence_is_set_back_to_scheduled_before_the_sweep_takes_it_gets_no_next_occurrence()
    {
        // Two weekly series, each with its occurrence cancelled, and a daily one whose occurrence
        // this sweep ends: the sweep is to make the next occurrence of each of the three. And an
        // event of its own that is due to start.
        var taken = await CreateSeriesEventAsync("Live again", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var free = await CreateSeriesEventAsync("Still free", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var ending = await CreateSeriesEventAsync("Ends beside them", new RecurrenceRuleDto(RecurrenceUnit.Day));
        await SetStatusAsync(taken.Id, EventStatus.Cancelled);
        await SetStatusAsync(free.Id, EventStatus.Cancelled);
        await StartedAsync(ending.Id, minutesAgo: 120);
        var due = await CreateEventAsync("Starts beside them", "in 2 hours");
        await PastDateAsync(due.Id, minutesAgo: 5);
        var takenSeries = taken.SeriesId!.Value;
        var freeSeries = free.SeriesId!.Value;
        var endingSeries = ending.SeriesId!.Value;
        var before = await SeriesAsync(takenSeries);
        var postsBefore = await PostDeliveryIdsAsync();

        // The first series' occurrence is set back to scheduled before the sweep takes it.
        var (log, saves) = await SweepCountingSavesAsync(
            beforeTheRowsAreTaken: () => SetStatusAsync(taken.Id, EventStatus.Scheduled));

        Assert.Equal(1, saves);
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);

        // That series has the one occurrence it had, live, and is as the sweep found it.
        Assert.Equal(taken.Id, Assert.Single(await LiveSeriesEventsAsync(takenSeries)).Id);
        Assert.Equal(1, await CountOccurrencesAsync(takenSeries));
        var after = await SeriesAsync(takenSeries);
        Assert.False(after.Ended);
        Assert.Equal(before.OccurrenceCount, after.OccurrenceCount);
        Assert.Equal(before.CurrentOccurrenceDate, after.CurrentOccurrenceDate);

        // The other two series each have their next occurrence, and the event that was due has
        // started.
        Assert.NotEqual(free.Id, Assert.Single(await LiveSeriesEventsAsync(freeSeries)).Id);
        Assert.NotEqual(ending.Id, Assert.Single(await LiveSeriesEventsAsync(endingSeries)).Id);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(due.Id)).Status);

        await AssertEveryPostQueuedSinceIsForAStoredEventAsync(postsBefore);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);

        // The next sweep finds that series with its live occurrence and has nothing to say.
        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
        Assert.Equal(1, await CountOccurrencesAsync(takenSeries));
    }

    [Fact]
    public async Task An_occurrence_no_longer_scheduled_when_the_sweep_takes_it_is_not_started()
    {
        // A weekly series with two occurrences: the first cancelled, and the second, which a
        // sweep then made, live.
        var first = await CreateSeriesEventAsync("Two occurrences", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var seriesId = first.SeriesId!.Value;
        await SetStatusAsync(first.Id, EventStatus.Cancelled);
        await SweepCountingSavesAsync(null);
        var second = Assert.Single(await LiveSeriesEventsAsync(seriesId));

        // Now the other way round, with the first one due: this sweep finds it scheduled and due.
        await SetStatusAsync(second.Id, EventStatus.Cancelled);
        await SetStatusAsync(first.Id, EventStatus.Scheduled);
        await PastDateAsync(first.Id, minutesAgo: 5);

        // Before the sweep takes it, the first is cancelled again and the second set back to
        // scheduled.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
        {
            await SetStatusAsync(first.Id, EventStatus.Cancelled);
            await SetStatusAsync(second.Id, EventStatus.Scheduled);
        });

        Assert.Equal(1, saves);
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);

        // What is stored stands: the first is cancelled, and the second is the live one.
        Assert.Equal(EventStatus.Cancelled, (await GetEventAsync(first.Id)).Status);
        Assert.Equal(second.Id, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);

        // Nothing was queued for the start of the first, and the sweep's count is the number of
        // deliveries it stored.
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventStart, first.Id));
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task An_occurrence_skipped_before_the_sweep_takes_it_stays_as_the_skip_left_it()
    {
        // A daily series whose occurrence has started and whose length has passed: this sweep is
        // to end it and make the next one.
        var ev = await CreateSeriesEventAsync("Skipped as it ends", new RecurrenceRuleDto(RecurrenceUnit.Day));
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;
        var postsBefore = await PostDeliveryIdsAsync();

        // The occurrence is skipped before the sweep takes it: the skip cancels it, makes the
        // next occurrence and moves the series on.
        EventSeries? asTheSkipLeftIt = null;
        Guid? skipsNext = null;
        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
        {
            var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
            skip.EnsureSuccessStatusCode();
            skipsNext = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent?.Id;
            asTheSkipLeftIt = await SeriesAsync(seriesId);
        });

        Assert.Equal(1, saves);
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);

        // The series is as the skip left it, with the skip's next occurrence as its live one, and
        // the occurrence itself is stored as the skip left it, cancelled.
        var after = await SeriesAsync(seriesId);
        Assert.Equal(asTheSkipLeftIt!.CurrentOccurrenceDate, after.CurrentOccurrenceDate);
        Assert.Equal(asTheSkipLeftIt.OccurrenceCount, after.OccurrenceCount);
        Assert.Equal(skipsNext, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);
        Assert.Equal(2, await CountOccurrencesAsync(seriesId));
        Assert.Equal(EventStatus.Cancelled, (await GetEventAsync(ev.Id)).Status);
        await AssertEveryPostQueuedSinceIsForAStoredEventAsync(postsBefore);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task The_occurrences_of_a_series_the_sweep_takes_are_held_until_it_is_saved()
    {
        // A weekly series with its occurrence cancelled, so this sweep makes its next one.
        var ev = await CreateSeriesEventAsync("Looked at, held", new RecurrenceRuleDto(RecurrenceUnit.Week));
        await SetStatusAsync(ev.Id, EventStatus.Cancelled);
        var seriesId = ev.SeriesId!.Value;

        // An edit of the cancelled occurrence, made once the sweep holds its rows, waits for it.
        Task<HttpResponseMessage>? edit = null;
        var (_, saves) = await SweepCountingSavesAsync(
            beforeTheRowsAreTaken: null,
            beforeTheSave: async () =>
            {
                edit = Client.PatchAsJsonAsync(
                    $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Title: "Edited meanwhile"));
                await WaitForSessionsWaitingForALockAsync(1);
            });

        // The sweep is saved, and the edit goes through after it.
        Assert.Equal(1, saves);
        (await edit!).EnsureSuccessStatusCode();
        Assert.Equal("Edited meanwhile", (await GetEventAsync(ev.Id)).Title);
        Assert.NotEqual(ev.Id, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);
    }

    [Fact]
    public async Task Every_kind_of_row_the_sweep_takes_is_held_until_it_is_saved()
    {
        // Due in one sweep: a reminder, the start of an event, the close of a poll, and the next
        // occurrence of a weekly series whose occurrence is cancelled.
        var keeps = await CreateEventAsync("Keeps its reminder, held", "in 3 hours");
        var kept = await AddDueNotificationAsync(keeps.Id);
        var starting = await CreateEventAsync("Starts, held", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);
        var poll = await CreateDuePollAsync("Closes, held?");
        var advanced = await CreateSeriesEventAsync("Advanced, held", new RecurrenceRuleDto(RecurrenceUnit.Week));
        await SetStatusAsync(advanced.Id, EventStatus.Cancelled);
        var seriesId = advanced.SeriesId!.Value;

        // Requests made once the sweep holds its rows wait for it: one for the reminder, one for
        // its event, one for the event that starts, one for the poll, and one for the series.
        var waiting = new List<Task<HttpResponseMessage>>();
        var (_, saves) = await SweepCountingSavesAsync(
            beforeTheRowsAreTaken: null,
            beforeTheSave: async () =>
            {
                waiting.Add(Client.DeleteAsync($"/events/{keeps.Id}/notifications/{kept.Id}"));
                waiting.Add(Client.PatchAsJsonAsync(
                    $"/events/{keeps.Id}", new UpdateEventRequest(CreatorId, Title: "Its event, edited meanwhile")));
                waiting.Add(Client.PatchAsJsonAsync(
                    $"/events/{starting.Id}", new UpdateEventRequest(CreatorId, Title: "Edited meanwhile")));
                waiting.Add(Client.DeleteAsync($"/polls/{poll.Id}"));
                waiting.Add(Client.PostAsync($"/series/{seriesId}/stop", null));
                await WaitForSessionsWaitingForALockAsync(5);
            });

        // The sweep is saved, and each request goes through after it, on what the sweep stored:
        // the ping of the reminder that was then removed is queued, the event that was then edited
        // has started, and the series that was then stopped has the next occurrence the sweep made,
        // as its last.
        Assert.Equal(1, saves);
        foreach (var request in waiting)
        {
            (await request).EnsureSuccessStatusCode();
        }

        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventNotification, keeps.Id));
        Assert.Equal("Its event, edited meanwhile", (await GetEventAsync(keeps.Id)).Title);
        var edited = await GetEventAsync(starting.Id);
        Assert.Equal(EventStatus.Started, edited.Status);
        Assert.Equal("Edited meanwhile", edited.Title);
        Assert.True((await SeriesAsync(seriesId)).Ended);
        Assert.NotEqual(advanced.Id, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);
    }

    [Fact]
    public async Task Rows_made_due_after_the_sweep_has_found_its_rows_are_left_to_the_next_sweep()
    {
        // An event due when the sweep looks, so the sweep has rows to take; and four rows that are
        // not due yet then.
        var due = await CreateEventAsync("Due when the sweep looks", "in 2 hours");
        await PastDateAsync(due.Id, minutesAgo: 5);
        var starts = await CreateEventAsync("Due to start a moment later", "in 2 hours");
        var ends = await CreateEventAsync("Due to end a moment later", "in 2 hours");
        var notified = await CreateEventAsync("Its reminder due a moment later", "in 3 hours");
        var poll = await CreatePollAsync("Due to close a moment later?");

        // Each becomes due after the sweep has found what it may change, and before it takes it.
        await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
        {
            await PastDateAsync(starts.Id, minutesAgo: 5);
            await StartedAsync(ends.Id, minutesAgo: 120);
            await AddDueNotificationAsync(notified.Id);
            await PastClosingTimeAsync(poll.Id);
        });

        // The sweep does what it found, and leaves the rest as it is.
        Assert.Equal(EventStatus.Started, (await GetEventAsync(due.Id)).Status);
        Assert.Equal(EventStatus.Scheduled, (await GetEventAsync(starts.Id)).Status);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(ends.Id)).Status);
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventNotification, notified.Id));
        Assert.Null(await ClosedAtAsync(poll.Id));

        // The next sweep takes them.
        await SweepCountingSavesAsync(null);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(starts.Id)).Status);
        Assert.Equal(EventStatus.Ended, (await GetEventAsync(ends.Id)).Status);
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventNotification, notified.Id));
        Assert.NotNull(await ClosedAtAsync(poll.Id));
    }

    [Fact]
    public async Task The_sweep_takes_a_series_before_its_occurrences()
    {
        // A daily series whose occurrence has started and whose length has passed: this sweep is
        // to end it and make the next one.
        var ev = await CreateSeriesEventAsync("Series first", new RecurrenceRuleDto(RecurrenceUnit.Day));
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;

        // Another transaction holds the series row; the sweep waits for it.
        await using var holder = await HoldRowAsync($"""SELECT "Id" FROM "EventSeries" WHERE "Id" = '{seriesId}' FOR UPDATE""");
        var sweep = Task.Run(() => SweepCountingSavesAsync(null));
        await WaitForSessionsWaitingForALockAsync(1);

        // While it waits, it holds no event of that series: a third transaction takes the
        // occurrence at once.
        Assert.True(await TryTakeAtOnceAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = '{ev.Id}' FOR UPDATE NOWAIT"""));

        // Once the series row is let go of, the sweep is saved: the occurrence has ended and the
        // series has its next one.
        await holder.DisposeAsync();
        var (_, saves) = await sweep;
        Assert.Equal(1, saves);
        Assert.Equal(EventStatus.Ended, (await GetEventAsync(ev.Id)).Status);
        Assert.NotEqual(ev.Id, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);
    }

    [Fact]
    public async Task The_sweep_takes_an_event_before_its_reminders()
    {
        // An event due to start, with a reminder that is due as well.
        var ev = await CreateEventAsync("Event first", "in 3 hours");
        var notification = await AddDueNotificationAsync(ev.Id);
        await PastDateAsync(ev.Id, minutesAgo: 5);

        // Another transaction holds the event row; the sweep waits for it.
        await using var holder = await HoldRowAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = '{ev.Id}' FOR UPDATE""");
        var sweep = Task.Run(() => SweepCountingSavesAsync(null));
        await WaitForSessionsWaitingForALockAsync(1);

        // While it waits, it holds none of that event's reminders.
        Assert.True(await TryTakeAtOnceAsync(
            $"""SELECT "Id" FROM "EventNotifications" WHERE "Id" = '{notification.Id}' FOR UPDATE NOWAIT"""));

        // Once the event row is let go of, the sweep is saved: the event has started and its
        // reminder is queued.
        await holder.DisposeAsync();
        var (_, saves) = await sweep;
        Assert.Equal(1, saves);
        Assert.Equal(EventStatus.Started, (await GetEventAsync(ev.Id)).Status);
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventNotification, ev.Id));
    }

    [Fact]
    public async Task An_event_cancelled_before_the_sweep_takes_it_is_not_started()
    {
        var ev = await CreateEventAsync("Cancelled before it is taken", "in 2 hours");
        await PastDateAsync(ev.Id, minutesAgo: 5);

        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Status: EventStatus.Cancelled)))
            .EnsureSuccessStatusCode());

        Assert.Equal(1, saves);
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(EventStatus.Cancelled, (await GetEventAsync(ev.Id)).Status);
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventStart, ev.Id));
    }

    [Fact]
    public async Task An_event_moved_later_before_the_sweep_takes_it_is_not_started()
    {
        var ev = await CreateEventAsync("Moved later before it is taken", "in 2 hours");
        await PastDateAsync(ev.Id, minutesAgo: 5);

        await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, WhenText: "in 3 days")))
            .EnsureSuccessStatusCode());

        Assert.Equal(EventStatus.Scheduled, (await GetEventAsync(ev.Id)).Status);
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventStart, ev.Id));
    }

    [Fact]
    public async Task A_started_event_moved_later_before_the_sweep_takes_it_is_not_ended()
    {
        // A started event whose length has passed. It has a posted message and an RSVP cutoff that
        // has passed as well, so the sweep also finds it among the cutoffs it re-renders.
        var ev = await CreateEventAsync("Started, moved later before it is taken", "in 2 hours");
        await StartedAsync(ev.Id, minutesAgo: 120);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var cutoff = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(150));
            await db.Events.Where(e => e.Id == ev.Id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.MessageId, 1L)
                .SetProperty(e => e.RsvpClosesAt, cutoff));
        }

        await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, WhenText: "in 3 days")))
            .EnsureSuccessStatusCode());

        Assert.Equal(EventStatus.Started, (await GetEventAsync(ev.Id)).Status);
    }

    [Fact]
    public async Task A_reminder_of_an_event_cancelled_before_the_sweep_takes_it_is_not_sent()
    {
        var ev = await CreateEventAsync("Its reminder, cancelled before it is taken", "in 3 hours");
        await AddDueNotificationAsync(ev.Id);

        await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Status: EventStatus.Cancelled)))
            .EnsureSuccessStatusCode());

        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventNotification, ev.Id));
    }

    [Fact]
    public async Task A_reminder_of_an_event_moved_later_before_the_sweep_takes_it_is_not_sent()
    {
        var ev = await CreateEventAsync("Its reminder, moved later before it is taken", "in 3 hours");
        await AddDueNotificationAsync(ev.Id);

        await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, WhenText: "in 3 days")))
            .EnsureSuccessStatusCode());

        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventNotification, ev.Id));
    }

    [Fact]
    public async Task A_series_stopped_before_the_sweep_takes_it_gets_no_next_occurrence()
    {
        // A daily series whose occurrence has started and whose length has passed.
        var ev = await CreateSeriesEventAsync("Stopped before it is taken", new RecurrenceRuleDto(RecurrenceUnit.Day));
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;

        await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.PostAsync($"/series/{seriesId}/stop", null)).EnsureSuccessStatusCode());

        // The occurrence has ended, and the series, stopped, has no next one.
        Assert.Equal(EventStatus.Ended, (await GetEventAsync(ev.Id)).Status);
        Assert.Empty(await LiveSeriesEventsAsync(seriesId));
        Assert.Equal(1, await CountOccurrencesAsync(seriesId));
    }

    [Fact]
    public async Task A_change_to_a_whole_series_before_the_sweep_takes_it_reaches_the_next_occurrence()
    {
        // A daily series whose occurrence has started and whose length has passed.
        var ev = await CreateSeriesEventAsync("Before the change", new RecurrenceRuleDto(RecurrenceUnit.Day));
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;

        await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
            (await Client.PatchAsJsonAsync(
                $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Title: "After the change", Scope: EditScope.Series)))
            .EnsureSuccessStatusCode());

        Assert.Equal("After the change", Assert.Single(await LiveSeriesEventsAsync(seriesId)).Title);
    }

    [Fact]
    public async Task A_poll_closed_before_the_sweep_takes_it_keeps_its_closing_time()
    {
        // A poll whose closing time has passed is closed by hand before the sweep takes it.
        var poll = await CreateDuePollAsync("Closed by hand?");
        Instant? closedByHand = null;
        var (log, saves) = await SweepCountingSavesAsync(beforeTheRowsAreTaken: async () =>
        {
            (await Client.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();
            closedByHand = await ClosedAtAsync(poll.Id);
        });

        // The sweep leaves it as the close left it.
        Assert.Equal(1, saves);
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);
        Assert.NotNull(closedByHand);
        Assert.Equal(closedByHand, await ClosedAtAsync(poll.Id));
    }

    [Fact]
    public async Task A_save_that_fails_inside_an_exception_of_the_providers_names_the_rows_in_the_log()
    {
        var ev = await CreateEventAsync("Held start", "in 2 hours");
        await PastDateAsync(ev.Id, minutesAgo: 5);

        // The save is refused with an error the database provider takes to be passing, which it
        // hands on inside an exception of its own.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var options = new DbContextOptionsBuilder<CalCronyDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<CalCronyDbContext>>())
            .AddInterceptors(new RefusesTheSave())
            .Options;
        await using var db = new CalCronyDbContext(options);
        var log = new CollectingLogger();
        var scheduler = new DeliveryScheduler(db, new SeriesMaterializer(db), log);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None));
        Assert.IsType<DbUpdateException>(failure.InnerException);

        // One error line, naming the rows by entity, key and state, with the failure not attached.
        var entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.StartsWith("The sweep could not be saved.", entry.Message);
        Assert.Matches(@"Delivery [0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12} \(Added\)", entry.Message);
        Assert.Null(entry.Exception);

        // Nothing of that sweep was saved.
        Assert.Equal(EventStatus.Scheduled, (await GetEventAsync(ev.Id)).Status);
    }

    /// <summary>Refuses a save of the sweep's: the first command that writes deliveries fails
    /// with an error the database provider takes to be passing.</summary>
    private sealed class RefusesTheSave : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("INSERT INTO \"Deliveries\"", StringComparison.Ordinal)
                ? throw new Npgsql.PostgresException("refused for the test", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.SerializationFailure)
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task<Instant?> ClosedAtAsync(Guid pollId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Polls.Where(p => p.Id == pollId).Select(p => p.ClosedAt).SingleAsync();
    }

    private async Task<EventDto> GetEventAsync(Guid id) =>
        (await Client.GetFromJsonAsync<EventDto>($"/events/{id}"))!;

    private async Task<EventDto> CreateSeriesEventAsync(string title, RecurrenceRuleDto rule, string? repeatUntil = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, "in 2 hours", ChannelId, Recurrence: rule, RepeatUntilText: repeatUntil));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> CreateEventAsync(string title, string when)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events", new CreateEventRequest(CreatorId, title, when, ChannelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    /// <summary>Adds a reminder that is due at once: 200 minutes before an event that starts in
    /// three hours.</summary>
    private async Task<EventNotificationDto> AddDueNotificationAsync(Guid eventId)
    {
        var added = await Client.PostAsJsonAsync($"/events/{eventId}/notifications", new CreateEventNotificationRequest(200));
        added.EnsureSuccessStatusCode();
        return (await added.Content.ReadFromJsonAsync<EventNotificationDto>())!;
    }

    /// <summary>Makes a poll whose closing time has passed, so the next sweep closes it.</summary>
    private async Task<PollDto> CreateDuePollAsync(string question)
    {
        var poll = await CreatePollAsync(question);
        await PastClosingTimeAsync(poll.Id);
        return poll;
    }

    /// <summary>Makes a poll that closes in two hours.</summary>
    private async Task<PollDto> CreatePollAsync(string question)
    {
        var created = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/polls",
            new CreatePollRequest(CreatorId, question, ChannelId, ["a", "b"], ClosesText: "in 2 hours"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<PollDto>())!;
    }

    /// <summary>Puts a poll's closing time five minutes in the past.</summary>
    private async Task PastClosingTimeAsync(Guid pollId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
        await db.Polls.Where(p => p.Id == pollId).ExecuteUpdateAsync(s => s.SetProperty(p => p.ClosesAt, past));
    }

    /// <summary>Records a message for a poll, as the bot does once it has posted the poll.</summary>
    private async Task GivePollAPostedMessageAsync(Guid pollId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.Polls.Where(p => p.Id == pollId).ExecuteUpdateAsync(s => s.SetProperty(p => p.MessageId, 1L));
    }

    private async Task PastDateAsync(Guid eventId, int minutesAgo)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(minutesAgo));
        await db.Events.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.StartsAt, past)
            .SetProperty(e => e.Status, EventStatus.Scheduled));
    }

    /// <summary>Puts an event's start in the past and marks it started, as a sweep would have.</summary>
    private async Task StartedAsync(Guid eventId, int minutesAgo)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(minutesAgo));
        await db.Events.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.StartsAt, past)
            .SetProperty(e => e.Status, EventStatus.Started));
    }

    /// <summary>Sets an event's status through the API, as an edit of that one occurrence.</summary>
    private async Task SetStatusAsync(Guid eventId, EventStatus status) =>
        (await Client.PatchAsJsonAsync(
            $"/events/{eventId}",
            new UpdateEventRequest(CreatorId, Status: status, Scope: EditScope.Occurrence))).EnsureSuccessStatusCode();

    private async Task<List<Event>> LiveSeriesEventsAsync(Guid seriesId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Events
            .Where(e => e.SeriesId == seriesId
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started))
            .ToListAsync();
    }

    private async Task<EventSeries> SeriesAsync(Guid seriesId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.EventSeries.AsNoTracking().SingleAsync(s => s.Id == seriesId);
    }

    /// <summary>How many events are stored for a series, whatever their status.</summary>
    private async Task<int> CountOccurrencesAsync(Guid seriesId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Events.CountAsync(e => e.SeriesId == seriesId);
    }

    private async Task<List<Guid>> PostDeliveryIdsAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Deliveries.Where(d => d.Type == DeliveryType.PostEventMessage).Select(d => d.Id).ToListAsync();
    }

    /// <summary>Every delivery that posts an event's embed, queued since
    /// <paramref name="postsBefore"/> was read, is for an event that is stored.</summary>
    private async Task AssertEveryPostQueuedSinceIsForAStoredEventAsync(List<Guid> postsBefore)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var queued = await db.Deliveries.AsNoTracking()
            .Where(d => d.Type == DeliveryType.PostEventMessage && !postsBefore.Contains(d.Id))
            .ToListAsync();
        var stored = await db.Events.Select(e => e.Id).ToListAsync();
        Assert.All(queued, d => Assert.Contains(
            System.Text.Json.JsonSerializer.Deserialize<PostEventMessagePayload>(d.PayloadJson)!.EventId, stored));
    }

    /// <summary>How many deliveries carry this time as the time they were made: those of the
    /// sweep that ran at it.</summary>
    private async Task<int> CountDeliveriesCreatedAtAsync(Instant at)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Deliveries.CountAsync(d => d.CreatedAt == at);
    }

    private async Task<int> CountDeliveriesAsync(DeliveryType type, Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Deliveries.CountAsync(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString()));
    }

    private int CountRows(string sql)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return db.Database.SqlQueryRaw<int>(sql).Single();
    }

    /// <summary>Waits until that many sessions of this database wait for a lock, and fails
    /// when they do not come to.</summary>
    private async Task WaitForSessionsWaitingForALockAsync(int sessions)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var waiting = 0;
        for (var tries = 0; tries < 200; tries++)
        {
            waiting = await db.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""")
                .SingleAsync();
            if (waiting >= sessions)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Sessions waiting for a lock: {waiting}, where {sessions} were to come.");
    }

    /// <summary>Runs the statement in a transaction of its own and holds what it took until the
    /// returned holder is disposed, which ends the transaction.</summary>
    private async Task<RowHolder> HoldRowAsync(string sql)
    {
        var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
        return new RowHolder(scope, transaction);
    }

    /// <summary>Whether a statement that takes rows without waiting for them succeeds, in a
    /// transaction of its own that it then ends.</summary>
    private async Task<bool> TryTakeAtOnceAsync(string sql)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            return true;
        }
        catch (Exception ex) when (ex.GetBaseException() is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.LockNotAvailable })
        {
            return false;
        }
    }

    /// <summary>A transaction kept open, with the scope of its context.</summary>
    private sealed class RowHolder(AsyncServiceScope scope, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        : IAsyncDisposable
    {
        private bool disposed;

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await transaction.DisposeAsync();
            await scope.DisposeAsync();
        }
    }

    /// <summary>Runs one sweep with a scheduler built by hand around a logger that keeps what it
    /// is given, and returns those entries.</summary>
    private async Task<List<LogEntry>> SweepWithLogAsync() => (await SweepCountingSavesAsync(null)).Log;

    /// <summary>Runs one sweep as <see cref="SweepWithLogAsync"/> does, and counts how often its
    /// context saves. <paramref name="beforeTheRowsAreTaken"/> is run once, after the sweep has
    /// found what it may change and before it takes those rows; it must be run, so the sweep must
    /// have something to take. <paramref name="beforeTheSave"/> is run once, as the sweep is
    /// about to save. The sweep's time, to the millisecond so that it is stored as it is, and the
    /// number the sweep returns are kept in <see cref="sweptAt"/> and
    /// <see cref="sweptCount"/>.</summary>
    private async Task<(List<LogEntry> Log, int Saves)> SweepCountingSavesAsync(
        Func<Task>? beforeTheRowsAreTaken, Func<Task>? beforeTheSave = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var taking = new BeforeTheRowsAreTaken(beforeTheRowsAreTaken);
        var saves = new CountingSaves(beforeTheSave);
        var options = new DbContextOptionsBuilder<CalCronyDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<CalCronyDbContext>>())
            .AddInterceptors(taking, saves)
            .Options;

        await using var db = new CalCronyDbContext(options);
        var log = new CollectingLogger();
        var scheduler = new DeliveryScheduler(db, new SeriesMaterializer(db), log);
        sweptAt = Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
        sweptCount = await scheduler.SweepAsync(sweptAt, CancellationToken.None);
        Assert.True(beforeTheRowsAreTaken is null || taking.Ran, "The sweep took no rows, so the step before it never ran.");
        return (log.Entries, saves.Count);
    }

    /// <summary>Runs what it is given once, before the first command that takes rows.</summary>
    private sealed class BeforeTheRowsAreTaken(Func<Task>? run) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Ran && run is not null && command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal))
            {
                Ran = true;
                await run();
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Counts the context's saves, and runs what it is given once, as the context is
    /// about to save for the first time.</summary>
    private sealed class CountingSaves(Func<Task>? beforeTheFirst) : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Count++ == 0 && beforeTheFirst is not null)
            {
                await beforeTheFirst();
            }

            return result;
        }
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
}
