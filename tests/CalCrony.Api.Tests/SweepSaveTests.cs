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

/// <summary>The sweep's save (<see cref="SweepSave"/>) with rows changed under it: each test has
/// something done between the sweep's reads and its save.</summary>
public class SweepSaveTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 900;
    private const long ChannelId = 901;
    private const long CreatorId = 902;

    private HttpClient Client => fixture.Client;

    // Of the last sweep run by SweepCountingSavesAsync: its time, and the number it returned.
    private Instant sweptAt;
    private int sweptCount;

    [Fact]
    public async Task A_notification_deleted_while_the_sweep_runs_is_let_go_of_and_the_rest_is_saved()
    {
        // Two things are due in one sweep: a notification, and another event's start.
        var notified = await CreateEventAsync("Loses its notification", "in 3 hours");
        var added = await Client.PostAsJsonAsync(
            $"/events/{notified.Id}/notifications", new CreateEventNotificationRequest(200));
        var notification = (await added.Content.ReadFromJsonAsync<EventNotificationDto>())!;
        var starting = await CreateEventAsync("Starts in that sweep", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);

        // The notification is deleted after the sweep has read it and before the sweep saves.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
            (await Client.DeleteAsync($"/events/{notified.Id}/notifications/{notification.Id}")).EnsureSuccessStatusCode());

        // The sweep is saved, at its second try: the other event has started…
        Assert.Equal(2, saves);
        Assert.Equal(EventStatus.Started, (await Client.GetFromJsonAsync<EventDto>($"/events/{starting.Id}"))!.Status);

        // …and things stand as they would had the sweep run just before the notification was
        // deleted: its ping is queued, once.
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventNotification, notified.Id));

        // Said once, as a warning that names the row, and not as a failure.
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            "Rows the sweep changed were deleted while it ran; they are let go of and the save is tried again without them: "
            + $"EventNotification {notification.Id} (Modified).",
            entry.Message);

        // Nothing of it is left for the next sweep.
        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventNotification, notified.Id));
    }

    [Fact]
    public async Task An_event_deleted_while_the_sweep_runs_is_let_go_of_and_the_rest_is_saved()
    {
        // Two events are due to start in one sweep. One of them has a notification that is due
        // as well, so the sweep changes two of that event's rows.
        var deleted = await CreateEventAsync("Deleted as it starts", "in 2 hours");
        var added = await Client.PostAsJsonAsync(
            $"/events/{deleted.Id}/notifications", new CreateEventNotificationRequest(200));
        var notification = (await added.Content.ReadFromJsonAsync<EventNotificationDto>())!;
        var starting = await CreateEventAsync("Starts in that sweep too", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);
        await PastDateAsync(deleted.Id, minutesAgo: 5);

        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
            (await Client.DeleteAsync($"/events/{deleted.Id}")).EnsureSuccessStatusCode());

        // The sweep is saved at its second try: the other event has started, and the deleted one
        // is gone.
        Assert.Equal(2, saves);
        Assert.Equal(EventStatus.Started, (await Client.GetFromJsonAsync<EventDto>($"/events/{starting.Id}"))!.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/events/{deleted.Id}")).StatusCode);

        // Both rows are let go of together, in one warning that names each of them once, and
        // none of it is a failure.
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1, Occurrences(entry.Message, $"Event {deleted.Id} (Modified)"));
        Assert.Equal(1, Occurrences(entry.Message, $"EventNotification {notification.Id} (Modified)"));

        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Many_rows_deleted_while_the_sweep_runs_are_let_go_of_together()
    {
        // Seven events, each with a notification that is due, and an eighth due to start.
        var notifications = new List<(Guid EventId, Guid Id)>();
        for (var i = 0; i < 7; i++)
        {
            var ev = await CreateEventAsync($"One of seven, {i}", "in 3 hours");
            var added = await Client.PostAsJsonAsync(
                $"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200));
            notifications.Add((ev.Id, (await added.Content.ReadFromJsonAsync<EventNotificationDto>())!.Id));
        }

        var starting = await CreateEventAsync("Starts beside the seven", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);

        // All seven notifications go between the sweep's reads and its save.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
        {
            foreach (var (eventId, id) in notifications)
            {
                (await Client.DeleteAsync($"/events/{eventId}/notifications/{id}")).EnsureSuccessStatusCode();
            }
        });

        // One more save is enough for all of them, and the rest of the sweep is in it.
        Assert.Equal(2, saves);
        Assert.Equal(EventStatus.Started, (await Client.GetFromJsonAsync<EventDto>($"/events/{starting.Id}"))!.Status);

        // One warning: five of the rows named, the other two counted.
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(5, notifications.Count(n => entry.Message.Contains($"EventNotification {n.Id} (Modified)")));
        Assert.EndsWith(" and 2 more.", entry.Message);

        // Their pings were queued before they went, one each.
        foreach (var (eventId, _) in notifications)
        {
            Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventNotification, eventId));
        }
    }

    [Fact]
    public async Task Polls_deleted_while_the_sweep_closes_them_are_let_go_of_together()
    {
        // Two polls whose closing time has passed, so this sweep closes both.
        var polls = new List<PollDto>();
        for (var i = 0; i < 2; i++)
        {
            var created = await Client.PostAsJsonAsync(
                $"/guilds/{GuildId}/polls",
                new CreatePollRequest(CreatorId, $"Closes in the sweep, {i}?", ChannelId, ["a", "b"], ClosesText: "in 2 hours"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            polls.Add((await created.Content.ReadFromJsonAsync<PollDto>())!);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var ids = polls.Select(p => p.Id).ToList();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
            await db.Polls.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.ClosesAt, past));
        }

        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
        {
            foreach (var poll in polls)
            {
                (await Client.DeleteAsync($"/polls/{poll.Id}")).EnsureSuccessStatusCode();
            }
        });

        // One more save, and one warning that names each of the two once.
        Assert.Equal(2, saves);
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.All(polls, poll => Assert.Equal(1, Occurrences(entry.Message, $"Poll {poll.Id} (Modified)")));
    }

    [Fact]
    public async Task A_series_whose_occurrence_is_deleted_as_the_sweep_ends_it_gets_no_next_occurrence_from_that_sweep()
    {
        // A daily series with a notification of its own. Its occurrence has started and its
        // length has passed, so this sweep ends it and makes the next one.
        var ev = await CreateSeriesEventAsync("Deleted as it ends", new RecurrenceRuleDto(RecurrenceUnit.Day));
        (await Client.PostAsJsonAsync(
            $"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, Scope: EditScope.Series))).EnsureSuccessStatusCode();
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;
        var postsBefore = await PostDeliveryIdsAsync();

        // Deleting the occurrence that is still live stops its series, and that is done while
        // the sweep runs.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
            (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode());

        Assert.Equal(2, saves);
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains($"Event {ev.Id} (Modified)", entry.Message);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();

            // The series is as the delete left it: stopped, with the one occurrence it had
            // counted, and no occurrence of it is stored.
            var series = await db.EventSeries.AsNoTracking().SingleAsync(s => s.Id == seriesId);
            Assert.True(series.Ended);
            Assert.Equal(1, series.OccurrenceCount);
            Assert.False(await db.Events.AnyAsync(e => e.SeriesId == seriesId));
        }

        // And nothing was queued to post an occurrence that is not there, and the sweep's count
        // of what it queued is the number of deliveries it stored.
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

        // Here the occurrence is deleted while the sweep runs, which stops the series itself.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
            (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode());

        Assert.Equal(2, saves);
        Assert.Contains(log, e => e.Level == LogLevel.Warning && e.Message.Contains($"Event {ev.Id} (Modified)"));

        // The sweep ended nothing, so it says nothing of the series.
        Assert.DoesNotContain(log, e => e.Message.Contains(seriesId.ToString()));
    }

    [Fact]
    public async Task A_next_occurrence_is_let_go_of_when_its_series_has_a_live_occurrence_again()
    {
        // Two weekly series, each with its occurrence cancelled, and a daily one whose occurrence
        // this sweep ends: the sweep makes the next occurrence of each of the three. And an event
        // of its own that is due to start.
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

        // The first series' occurrence is set back to scheduled while the sweep runs.
        var (log, saves) = await SweepCountingSavesAsync(
            beforeTheSave: () => SetStatusAsync(taken.Id, EventStatus.Scheduled));

        Assert.Equal(2, saves);
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(takenSeries.ToString(), entry.Message);
        Assert.DoesNotContain(freeSeries.ToString(), entry.Message);
        Assert.DoesNotContain(endingSeries.ToString(), entry.Message);

        // That series has the one occurrence it had, live, and is as the sweep found it.
        Assert.Equal(taken.Id, Assert.Single(await LiveSeriesEventsAsync(takenSeries)).Id);
        Assert.Equal(1, await CountOccurrencesAsync(takenSeries));
        var after = await SeriesAsync(takenSeries);
        Assert.False(after.Ended);
        Assert.Equal(before.OccurrenceCount, after.OccurrenceCount);
        Assert.Equal(before.CurrentOccurrenceDate, after.CurrentOccurrenceDate);

        // The other two series each have their next occurrence — the one whose occurrence this
        // sweep ended as well, though that occurrence was still stored as live when the sweep
        // looked — and the event that was due has started.
        Assert.NotEqual(free.Id, Assert.Single(await LiveSeriesEventsAsync(freeSeries)).Id);
        Assert.NotEqual(ending.Id, Assert.Single(await LiveSeriesEventsAsync(endingSeries)).Id);
        Assert.Equal(EventStatus.Started, (await Client.GetFromJsonAsync<EventDto>($"/events/{due.Id}"))!.Status);

        // Nothing was queued to post an occurrence that is not there, and the sweep's count of
        // what it queued is the number of deliveries it stored.
        await AssertEveryPostQueuedSinceIsForAStoredEventAsync(postsBefore);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);

        // The next sweep finds that series with its live occurrence and has nothing to say.
        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
        Assert.Equal(1, await CountOccurrencesAsync(takenSeries));
    }

    [Fact]
    public async Task An_occurrence_the_sweep_starts_counts_as_live_for_its_series()
    {
        // A weekly series with its occurrence cancelled, so this sweep makes its next one, and an
        // event of its own that this sweep starts.
        var ev = await CreateSeriesEventAsync("Joined by a start", new RecurrenceRuleDto(RecurrenceUnit.Week));
        await SetStatusAsync(ev.Id, EventStatus.Cancelled);
        var starts = await CreateEventAsync("Starts into the series", "in 2 hours");
        await PastDateAsync(starts.Id, minutesAgo: 5);
        var seriesId = ev.SeriesId!.Value;
        var postsBefore = await PostDeliveryIdsAsync();

        // While the sweep runs, the event becomes an occurrence of that series, stored as
        // cancelled. The sweep's own save sets it to started, so it is live once that is written.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: () => ExecuteSqlAsync(
            $"""UPDATE "Events" SET "SeriesId" = '{seriesId}', "Status" = {(int)EventStatus.Cancelled} WHERE "Id" = '{starts.Id}'"""));

        Assert.Equal(2, saves);
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            "The save met a second live occurrence of a series. Let go of, and the save is tried again without them: "
            + $"the next occurrence made for series {seriesId}.",
            entry.Message);

        // The started event is the series' one live occurrence, and no next one was stored.
        var live = Assert.Single(await LiveSeriesEventsAsync(seriesId));
        Assert.Equal(starts.Id, live.Id);
        Assert.Equal(EventStatus.Started, live.Status);
        Assert.Equal(2, await CountOccurrencesAsync(seriesId));
        await AssertEveryPostQueuedSinceIsForAStoredEventAsync(postsBefore);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task A_save_is_tried_again_as_it_is_when_no_series_has_a_live_occurrence_after_all()
    {
        // A weekly series with its occurrence cancelled, so this sweep makes its next one.
        var ev = await CreateSeriesEventAsync("Live for a moment", new RecurrenceRuleDto(RecurrenceUnit.Week));
        await SetStatusAsync(ev.Id, EventStatus.Cancelled);
        var seriesId = ev.SeriesId!.Value;

        // The occurrence is set back to scheduled before the sweep's first save, and cancelled
        // again once that save has failed, before the sweep looks.
        var (log, saves) = await SweepCountingSavesAsync(
            beforeTheSave: () => SetStatusAsync(ev.Id, EventStatus.Scheduled),
            afterTheFirstFailure: () => SetStatusAsync(ev.Id, EventStatus.Cancelled));

        // Nothing is let go of: the second save stores the next occurrence.
        Assert.Equal(2, saves);
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            "The save met a second live occurrence of a series, and nothing the sweep writes stands against one now. "
            + "The save is tried again as it is.",
            entry.Message);
        Assert.NotEqual(ev.Id, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);
        Assert.Equal(2, await CountOccurrencesAsync(seriesId));
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);
    }

    [Fact]
    public async Task Every_next_occurrence_is_let_go_of_when_one_still_cannot_be_stored()
    {
        // Three weekly series with their occurrences cancelled, so this sweep makes the next
        // occurrence of each, and an event of its own.
        var first = await CreateSeriesEventAsync("Live again first", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var second = await CreateSeriesEventAsync("Live again later", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var third = await CreateSeriesEventAsync("Free throughout", new RecurrenceRuleDto(RecurrenceUnit.Week));
        await SetStatusAsync(first.Id, EventStatus.Cancelled);
        await SetStatusAsync(second.Id, EventStatus.Cancelled);
        await SetStatusAsync(third.Id, EventStatus.Cancelled);
        var joins = await CreateEventAsync("Joins the second series", "in 2 hours");
        var firstSeries = first.SeriesId!.Value;
        var secondSeries = second.SeriesId!.Value;
        var thirdSeries = third.SeriesId!.Value;
        var postsBefore = await PostDeliveryIdsAsync();

        // The first series has a live occurrence again before the sweep's first save. The second
        // has none when the sweep then looks, and gets one before the save is tried again: an
        // event that belonged to no series, and so was not among the occurrences the sweep held.
        var (log, saves) = await SweepCountingSavesAsync(
            beforeTheSave: () => SetStatusAsync(first.Id, EventStatus.Scheduled),
            beforeTheSecondSave: () => ExecuteSqlAsync(
                $"""UPDATE "Events" SET "SeriesId" = '{secondSeries}' WHERE "Id" = '{joins.Id}'"""));

        // Three saves. The first failure lets go of the first series' next occurrence alone; the
        // second lets go of every next occurrence that was left, the third series' among them.
        Assert.Equal(3, saves);
        var warnings = log.Where(e => e.Level >= LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.StartsWith("The save met a second live occurrence of a series. Let go of", warnings[0].Message);
        Assert.Contains(firstSeries.ToString(), warnings[0].Message);
        Assert.DoesNotContain(secondSeries.ToString(), warnings[0].Message);
        Assert.DoesNotContain(thirdSeries.ToString(), warnings[0].Message);
        Assert.StartsWith("The save met a second live occurrence of a series once more.", warnings[1].Message);
        Assert.DoesNotContain(firstSeries.ToString(), warnings[1].Message);
        Assert.Contains(secondSeries.ToString(), warnings[1].Message);
        Assert.Contains(thirdSeries.ToString(), warnings[1].Message);

        // The first two series each have the one live occurrence they were given, the third has
        // none, and no next occurrence was stored for any of them.
        Assert.Equal(first.Id, Assert.Single(await LiveSeriesEventsAsync(firstSeries)).Id);
        Assert.Equal(joins.Id, Assert.Single(await LiveSeriesEventsAsync(secondSeries)).Id);
        Assert.Empty(await LiveSeriesEventsAsync(thirdSeries));
        Assert.Equal(1, await CountOccurrencesAsync(firstSeries));
        Assert.Equal(2, await CountOccurrencesAsync(secondSeries));
        Assert.Equal(1, await CountOccurrencesAsync(thirdSeries));
        await AssertEveryPostQueuedSinceIsForAStoredEventAsync(postsBefore);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);

        // The next sweep makes the third series' next occurrence, and has nothing to say.
        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
        Assert.NotEqual(third.Id, Assert.Single(await LiveSeriesEventsAsync(thirdSeries)).Id);
    }

    [Fact]
    public async Task The_sweeps_start_of_an_occurrence_is_let_go_of_when_another_occurrence_of_its_series_is_live()
    {
        // A weekly series with two occurrences: the first cancelled, and the second, which a
        // sweep then made, live.
        var first = await CreateSeriesEventAsync("Two occurrences", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var seriesId = first.SeriesId!.Value;
        await SetStatusAsync(first.Id, EventStatus.Cancelled);
        await SweepAsync();
        var second = Assert.Single(await LiveSeriesEventsAsync(seriesId));

        // Now the other way round, with the first one due: this sweep reads it as scheduled and
        // sets out to start it.
        await SetStatusAsync(second.Id, EventStatus.Cancelled);
        await SetStatusAsync(first.Id, EventStatus.Scheduled);
        await PastDateAsync(first.Id, minutesAgo: 5);

        // While the sweep runs, the first is cancelled again and the second set back to scheduled.
        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
        {
            await SetStatusAsync(first.Id, EventStatus.Cancelled);
            await SetStatusAsync(second.Id, EventStatus.Scheduled);
        });

        // One more save, and one warning that names the occurrence whose start is let go of.
        Assert.Equal(2, saves);
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            "The save met a second live occurrence of a series. Let go of, and the save is tried again without them: "
            + $"the sweep's change to Event {first.Id} (Modified).",
            entry.Message);

        // What is stored stands: the first is cancelled, and the second is the live one.
        Assert.Equal(EventStatus.Cancelled, (await Client.GetFromJsonAsync<EventDto>($"/events/{first.Id}"))!.Status);
        Assert.Equal(second.Id, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);

        // What the sweep queued for the start stays queued, once, and the sweep's count is the
        // number of deliveries it stored.
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventStart, first.Id));
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);

        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventStart, first.Id));
    }

    [Fact]
    public async Task An_occurrence_skipped_as_the_sweep_ends_it_leaves_the_series_as_the_skip_left_it()
    {
        // A daily series whose occurrence has started and whose length has passed: this sweep
        // ends it and makes the next one.
        var ev = await CreateSeriesEventAsync("Skipped as it ends", new RecurrenceRuleDto(RecurrenceUnit.Day));
        await StartedAsync(ev.Id, minutesAgo: 120);
        var seriesId = ev.SeriesId!.Value;
        var postsBefore = await PostDeliveryIdsAsync();

        // The occurrence is skipped while the sweep runs: the skip cancels it, makes the next
        // occurrence and moves the series on.
        EventSeries? asTheSkipLeftIt = null;
        Guid? skipsNext = null;
        var (log, saves) = await SweepCountingSavesAsync(beforeTheSave: async () =>
        {
            var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
            skip.EnsureSuccessStatusCode();
            skipsNext = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent?.Id;
            asTheSkipLeftIt = await SeriesAsync(seriesId);
        });

        Assert.Equal(2, saves);
        var entry = Assert.Single(log, e => e.Level >= LogLevel.Warning);
        Assert.EndsWith($": the next occurrence made for series {seriesId}.", entry.Message);

        // The series is as the skip left it, with the skip's next occurrence as its live one.
        var after = await SeriesAsync(seriesId);
        Assert.Equal(asTheSkipLeftIt!.CurrentOccurrenceDate, after.CurrentOccurrenceDate);
        Assert.Equal(asTheSkipLeftIt.OccurrenceCount, after.OccurrenceCount);
        Assert.Equal(skipsNext, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);
        Assert.Equal(2, await CountOccurrencesAsync(seriesId));
        await AssertEveryPostQueuedSinceIsForAStoredEventAsync(postsBefore);
        Assert.Equal(await CountDeliveriesCreatedAtAsync(sweptAt), sweptCount);

        // The occurrence itself is stored as the sweep wrote it, ended: the sweep's own change
        // of it is saved with the rest, over the skip's.
        Assert.Equal(EventStatus.Ended, (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.Status);
    }

    [Fact]
    public async Task A_second_failure_with_no_next_occurrence_left_goes_on_to_the_caller()
    {
        // A weekly series whose occurrence is live, and an event of its own that is due to start.
        // No series is without a live occurrence, so this sweep makes no next occurrence.
        var series = await CreateSeriesEventAsync("Has its live occurrence", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var starts = await CreateEventAsync("Starts into that series", "in 2 hours");
        await PastDateAsync(starts.Id, minutesAgo: 5);

        // While the sweep runs, the event becomes an occurrence of that series, stored as
        // cancelled, so that the sweep's own start of it makes a second live one. The sweep does
        // not know the event as an occurrence of a series, so it finds nothing to let go of,
        // tries the save again as it is, and has no next occurrence to let go of after that.
        var (log, saves) = await SweepCountingSavesAsync(
            beforeTheSave: () => ExecuteSqlAsync(
                $"""UPDATE "Events" SET "SeriesId" = '{series.SeriesId!.Value}', "Status" = {(int)EventStatus.Cancelled} WHERE "Id" = '{starts.Id}'"""),
            failing: true);

        // Two saves: one warning, then the line of a sweep that could not be saved.
        Assert.Equal(2, saves);
        var said = log.Where(e => e.Level >= LogLevel.Warning).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Error], said.Select(e => e.Level));
        Assert.EndsWith("The save is tried again as it is.", said[0].Message);
        Assert.StartsWith("The sweep could not be saved.", said[1].Message);

        // Nothing of that sweep is saved.
        Assert.Equal(EventStatus.Cancelled, (await Client.GetFromJsonAsync<EventDto>($"/events/{starts.Id}"))!.Status);
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventStart, starts.Id));
    }

    [Fact]
    public async Task A_third_failure_on_a_live_occurrence_goes_on_to_the_caller()
    {
        // Two weekly series with their occurrences cancelled, so this sweep makes the next
        // occurrence of each; a third whose occurrence is live; and two events of their own, one
        // of them due to start.
        var first = await CreateSeriesEventAsync("Meets the first save", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var second = await CreateSeriesEventAsync("Meets the second save", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var third = await CreateSeriesEventAsync("Meets the third save", new RecurrenceRuleDto(RecurrenceUnit.Week));
        await SetStatusAsync(first.Id, EventStatus.Cancelled);
        await SetStatusAsync(second.Id, EventStatus.Cancelled);
        var joins = await CreateEventAsync("Joins the second series, live", "in 2 hours");
        var starts = await CreateEventAsync("Starts into the third series", "in 2 hours");
        await PastDateAsync(starts.Id, minutesAgo: 5);

        // Each save meets a second live occurrence that was not there when the sweep last looked.
        // Before the first, the first series' occurrence is set back to scheduled. Before the
        // second, an event that belonged to no series becomes the second series' live occurrence.
        // Before the third, the event the sweep is starting becomes an occurrence of the third
        // series, stored as cancelled, so that the sweep's own start of it makes a second live one.
        var (log, saves) = await SweepCountingSavesAsync(
            beforeTheSave: () => SetStatusAsync(first.Id, EventStatus.Scheduled),
            beforeTheSecondSave: () => ExecuteSqlAsync(
                $"""UPDATE "Events" SET "SeriesId" = '{second.SeriesId!.Value}' WHERE "Id" = '{joins.Id}'"""),
            beforeTheThirdSave: () => ExecuteSqlAsync(
                $"""UPDATE "Events" SET "SeriesId" = '{third.SeriesId!.Value}', "Status" = {(int)EventStatus.Cancelled} WHERE "Id" = '{starts.Id}'"""),
            failing: true);

        // Three saves and no more: two warnings, then the line of a sweep that could not be
        // saved, without the failure attached.
        Assert.Equal(3, saves);
        var said = log.Where(e => e.Level >= LogLevel.Warning).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Warning, LogLevel.Error], said.Select(e => e.Level));
        Assert.StartsWith("The sweep could not be saved.", said[2].Message);
        Assert.Null(said[2].Exception);

        // Nothing of that sweep is saved: the event is not started, and nothing is queued for it.
        Assert.Equal(EventStatus.Cancelled, (await Client.GetFromJsonAsync<EventDto>($"/events/{starts.Id}"))!.Status);
        Assert.Equal(0, await CountDeliveriesAsync(DeliveryType.EventStart, starts.Id));
        Assert.Equal(1, await CountOccurrencesAsync(first.SeriesId!.Value));

        Assert.DoesNotContain(await SweepWithLogAsync(), e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task The_occurrences_the_sweep_looked_at_are_held_until_it_is_saved()
    {
        // Two weekly series with their occurrences cancelled, so this sweep makes the next
        // occurrence of each.
        var taken = await CreateSeriesEventAsync("Live again, held", new RecurrenceRuleDto(RecurrenceUnit.Week));
        var other = await CreateSeriesEventAsync("Looked at, held", new RecurrenceRuleDto(RecurrenceUnit.Week));
        await SetStatusAsync(taken.Id, EventStatus.Cancelled);
        await SetStatusAsync(other.Id, EventStatus.Cancelled);

        // The first series' occurrence is set back to scheduled while the sweep runs, so the
        // sweep looks at both series. An edit of the other series' occurrence, made as the sweep
        // saves again, waits for the sweep.
        Task<HttpResponseMessage>? edit = null;
        var (_, saves) = await SweepCountingSavesAsync(
            beforeTheSave: () => SetStatusAsync(taken.Id, EventStatus.Scheduled),
            beforeTheSecondSave: async () =>
            {
                edit = Client.PatchAsJsonAsync(
                    $"/events/{other.Id}",
                    new UpdateEventRequest(CreatorId, Title: "Edited meanwhile", Scope: EditScope.Occurrence));
                await WaitForSessionsWaitingForALockAsync(1);
            });

        // The sweep is saved at its second try, and the edit goes through after it.
        Assert.Equal(2, saves);
        (await edit!).EnsureSuccessStatusCode();
        Assert.Equal("Edited meanwhile", (await Client.GetFromJsonAsync<EventDto>($"/events/{other.Id}"))!.Title);
        Assert.NotEqual(other.Id, Assert.Single(await LiveSeriesEventsAsync(other.SeriesId!.Value)).Id);
    }

    [Fact]
    public async Task The_rows_that_are_still_there_are_held_until_the_sweep_is_saved()
    {
        // Due in one sweep: two notifications, the start of an event, and the close of a poll.
        var loses = await CreateEventAsync("Loses its notification, others held", "in 3 hours");
        var gone = await AddDueNotificationAsync(loses.Id);
        var keeps = await CreateEventAsync("Keeps its notification, held", "in 3 hours");
        var kept = await AddDueNotificationAsync(keeps.Id);
        var starting = await CreateEventAsync("Starts, held", "in 2 hours");
        await PastDateAsync(starting.Id, minutesAgo: 5);
        var poll = await CreateDuePollAsync("Closes, held?");

        // One notification is deleted while the sweep runs, so the sweep looks for every
        // notification, event and poll it changed. Three requests made as the sweep saves again,
        // one for each kind of row that is still there, wait for the sweep.
        var waiting = new List<Task<HttpResponseMessage>>();
        var (_, saves) = await SweepCountingSavesAsync(
            beforeTheSave: async () =>
                (await Client.DeleteAsync($"/events/{loses.Id}/notifications/{gone.Id}")).EnsureSuccessStatusCode(),
            beforeTheSecondSave: async () =>
            {
                waiting.Add(Client.DeleteAsync($"/events/{keeps.Id}/notifications/{kept.Id}"));
                waiting.Add(Client.PatchAsJsonAsync(
                    $"/events/{starting.Id}", new UpdateEventRequest(CreatorId, Title: "Edited meanwhile")));
                waiting.Add(Client.DeleteAsync($"/polls/{poll.Id}"));
                await WaitForSessionsWaitingForALockAsync(3);
            });

        // The sweep is saved at its second try, and each request goes through after it, on what
        // the sweep stored: the ping of the notification that was then removed is queued, and the
        // event that was then edited has started.
        Assert.Equal(2, saves);
        foreach (var request in waiting)
        {
            (await request).EnsureSuccessStatusCode();
        }

        Assert.Equal(1, await CountDeliveriesAsync(DeliveryType.EventNotification, keeps.Id));
        var edited = (await Client.GetFromJsonAsync<EventDto>($"/events/{starting.Id}"))!;
        Assert.Equal(EventStatus.Started, edited.Status);
        Assert.Equal("Edited meanwhile", edited.Title);
    }

    private async Task<EventDto> CreateSeriesEventAsync(
        string title, RecurrenceRuleDto rule, string? repeatUntil = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(
                CreatorId, title, "in 2 hours", ChannelId,
                Recurrence: rule, RepeatUntilText: repeatUntil));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
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

    private async Task<List<Event>> LiveSeriesEventsAsync(Guid seriesId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Events
            .Where(e => e.SeriesId == seriesId
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Started))
            .ToListAsync();
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }

    /// <summary>Runs one sweep with a scheduler built by hand around a logger that keeps what it
    /// is given, and returns those entries.</summary>
    private async Task<List<LogEntry>> SweepWithLogAsync() => (await SweepCountingSavesAsync(null)).Log;

    /// <summary>Runs one sweep as <see cref="SweepWithLogAsync"/> does, and counts how often its
    /// context saves. <paramref name="beforeTheSave"/> is run once, between the sweep's reads
    /// and its first save; <paramref name="afterTheFirstFailure"/> once, when a save has failed
    /// and before the sweep does anything about it; and <paramref name="beforeTheSecondSave"/>
    /// and <paramref name="beforeTheThirdSave"/> once each, as the save is tried again. With
    /// <paramref name="failing"/> the sweep is to end in a failed save. The sweep's time, to the
    /// millisecond so that it is stored as it is, and the number the sweep returns are kept in
    /// <see cref="sweptAt"/> and <see cref="sweptCount"/>.</summary>
    private async Task<(List<LogEntry> Log, int Saves)> SweepCountingSavesAsync(
        Func<Task>? beforeTheSave,
        Func<Task>? beforeTheSecondSave = null,
        Func<Task>? afterTheFirstFailure = null,
        Func<Task>? beforeTheThirdSave = null,
        bool failing = false)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var saves = new CountingSaves(afterTheFirstFailure, beforeTheSave, beforeTheSecondSave, beforeTheThirdSave);
        var options = new DbContextOptionsBuilder<CalCronyDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<CalCronyDbContext>>())
            .AddInterceptors(saves)
            .Options;

        await using var db = new CalCronyDbContext(options);
        var log = new CollectingLogger();
        var scheduler = new DeliveryScheduler(db, new SeriesMaterializer(db), log);
        sweptAt = Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
        if (failing)
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => scheduler.SweepAsync(sweptAt, CancellationToken.None));
        }
        else
        {
            sweptCount = await scheduler.SweepAsync(sweptAt, CancellationToken.None);
        }

        return (log.Entries, saves.Count);
    }

    /// <summary>Counts the context's saves, and runs what it is given once each: one thing when
    /// a save has failed for the first time, and of the others the first as the context is about
    /// to save for the first time, the second as it is about to save for the second time, and so
    /// on. A sweep that keeps on saving is stopped.</summary>
    private sealed class CountingSaves(Func<Task>? afterTheFirstFailure, params Func<Task>?[] before)
        : SaveChangesInterceptor
    {
        private int failures;

        public int Count { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var save = Count++;
            Assert.True(save < 20, "The sweep keeps on saving.");
            if (save < before.Length && before[save] is { } run)
            {
                await run();
            }

            return result;
        }

        public override async Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (failures++ == 0 && afterTheFirstFailure is not null)
            {
                await afterTheFirstFailure();
            }
        }
    }

    private static int Occurrences(string text, string part) => text.Split(part).Length - 1;

    /// <summary>Adds a notification that is due at once: 200 minutes before an event that
    /// starts in three hours.</summary>
    private async Task<EventNotificationDto> AddDueNotificationAsync(Guid eventId)
    {
        var added = await Client.PostAsJsonAsync($"/events/{eventId}/notifications", new CreateEventNotificationRequest(200));
        added.EnsureSuccessStatusCode();
        return (await added.Content.ReadFromJsonAsync<EventNotificationDto>())!;
    }

    /// <summary>Makes a poll whose closing time has passed, so the next sweep closes it.</summary>
    private async Task<PollDto> CreateDuePollAsync(string question)
    {
        var created = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/polls",
            new CreatePollRequest(CreatorId, question, ChannelId, ["a", "b"], ClosesText: "in 2 hours"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var poll = (await created.Content.ReadFromJsonAsync<PollDto>())!;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
        await db.Polls.Where(p => p.Id == poll.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ClosesAt, past));
        return poll;
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

    /// <summary>Sets an event's status through the API, as an edit of that one occurrence.</summary>
    private async Task SetStatusAsync(Guid eventId, EventStatus status) =>
        (await Client.PatchAsJsonAsync(
            $"/events/{eventId}",
            new UpdateEventRequest(CreatorId, Status: status, Scope: EditScope.Occurrence))).EnsureSuccessStatusCode();

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

    private async Task<List<Guid>> PostDeliveryIdsAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Deliveries.Where(d => d.Type == DeliveryType.PostEventMessage).Select(d => d.Id).ToListAsync();
    }

    private async Task<int> CountDeliveriesAsync(DeliveryType type, Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.Deliveries.CountAsync(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString()));
    }

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

    private async Task<EventDto> CreateEventAsync(string title, string when)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, when, ChannelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }
}
