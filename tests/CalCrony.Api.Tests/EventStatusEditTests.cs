using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The answers of <c>PATCH /events/{id}</c> to a status it cannot store, to a second
/// upcoming occurrence of a series, and to a wait for rows it gives way in.</summary>
public class EventStatusEditTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 920;
    private const long ChannelId = 921;
    private const long CreatorId = 922;

    private HttpClient Client => fixture.Client;

    [Theory]
    [InlineData(4)]
    [InlineData(42)]
    [InlineData(-1)]
    public async Task A_status_outside_the_four_is_refused_and_nothing_is_stored(int status)
    {
        var ev = await CreateEventAsync("Keeps its status");

        // Sent as a number, as the API's JSON carries a status.
        var response = await Client.PatchAsync(
            $"/events/{ev.Id}", JsonContent.Create(new { editorId = CreatorId, title = "Not stored", status }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Choose a status of scheduled, started, ended or cancelled.",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        var stored = await GetEventAsync(ev.Id);
        Assert.Equal(EventStatus.Scheduled, stored.Status);
        Assert.Equal("Keeps its status", stored.Title);
    }

    [Theory]
    [InlineData(EventStatus.Scheduled)]
    [InlineData(EventStatus.Started)]
    [InlineData(EventStatus.Ended)]
    [InlineData(EventStatus.Cancelled)]
    public async Task Each_of_the_four_statuses_is_stored(EventStatus status)
    {
        var ev = await CreateEventAsync($"Set to {status}");

        var response = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Status: status));

        response.EnsureSuccessStatusCode();
        Assert.Equal(status, (await GetEventAsync(ev.Id)).Status);
    }

    [Fact]
    public async Task Setting_an_occurrence_back_to_scheduled_while_another_is_upcoming_answers_409()
    {
        // A weekly series whose first occurrence is cancelled, so a sweep makes its second.
        var first = await CreateSeriesEventAsync("Has a second");
        var seriesId = first.SeriesId!.Value;
        (await Client.PatchAsJsonAsync(
            $"/events/{first.Id}",
            new UpdateEventRequest(CreatorId, Status: EventStatus.Cancelled, Scope: EditScope.Occurrence))).EnsureSuccessStatusCode();
        await SweepAsync();
        var second = Assert.Single(await LiveSeriesEventsAsync(seriesId));

        var response = await Client.PatchAsJsonAsync(
            $"/events/{first.Id}", new UpdateEventRequest(CreatorId, Status: EventStatus.Scheduled));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "This series already has an upcoming occurrence.",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Equal(EventStatus.Cancelled, (await GetEventAsync(first.Id)).Status);
        Assert.Equal(second.Id, Assert.Single(await LiveSeriesEventsAsync(seriesId)).Id);
    }

    [Fact]
    public async Task An_edit_that_gives_way_in_a_wait_for_rows_answers_409()
    {
        // A weekly series and its upcoming occurrence.
        var ev = await CreateSeriesEventAsync("Waits for its rows");
        var seriesId = ev.SeriesId!.Value;

        // Another transaction holds the occurrence. A change to the whole series takes the series
        // row and then waits for the occurrence.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var other = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await using var transaction = await BeginWaitingLongerAsync(other);
        await other.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = {ev.Id} FOR UPDATE""");
        var edit = Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Title: "Not stored", Scope: EditScope.Series));
        await WaitForSessionsWaitingForALockAsync(1);

        // The other transaction now asks for the series row, so each waits for the other, and the
        // database stops the edit.
        var otherWaits = other.Database.ExecuteSqlAsync(
            $"""SELECT "Id" FROM "EventSeries" WHERE "Id" = {seriesId} FOR UPDATE""");

        var response = await edit;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "This event changed at the same time — try again.",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        await otherWaits;
        await transaction.RollbackAsync();

        Assert.Equal("Waits for its rows", (await GetEventAsync(ev.Id)).Title);
    }

    [Fact]
    public async Task An_edit_that_gives_way_as_it_saves_answers_409()
    {
        // A weekly series and its upcoming occurrence.
        var ev = await CreateSeriesEventAsync("Waits as it saves");
        var seriesId = ev.SeriesId!.Value;

        // Another transaction holds the occurrence's options. A change to the whole series that
        // sets an attendee limit takes the series row and the occurrence, and then waits for an
        // option as it saves.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var other = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await using var transaction = await BeginWaitingLongerAsync(other);
        await other.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "RsvpOptions" WHERE "EventId" = {ev.Id} FOR UPDATE""");
        var edit = Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AttendeeLimit: 5, Scope: EditScope.Series));
        await WaitForSessionsWaitingForALockAsync(1);

        // The other transaction now asks for the series row, so each waits for the other, and the
        // database stops the edit.
        var otherWaits = other.Database.ExecuteSqlAsync(
            $"""SELECT "Id" FROM "EventSeries" WHERE "Id" = {seriesId} FOR UPDATE""");

        var response = await edit;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "This event changed at the same time — try again.",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        await otherWaits;
        await transaction.RollbackAsync();

        // No option of the occurrence has a limit.
        Assert.All((await GetEventAsync(ev.Id)).Options, option => Assert.Null(option.Capacity));
    }

    /// <summary>Begins a transaction on <paramref name="other"/> that waits ten seconds, rather
    /// than the database's one, before the database looks for transactions waiting on each other,
    /// so that the request it waits with is the one the database stops.</summary>
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginWaitingLongerAsync(CalCronyDbContext other)
    {
        var transaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlRawAsync("SET LOCAL deadlock_timeout = '10s'");
        return transaction;
    }

    private async Task<EventDto> GetEventAsync(Guid id) =>
        (await Client.GetFromJsonAsync<EventDto>($"/events/{id}"))!;

    private async Task<EventDto> CreateEventAsync(string title)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events", new CreateEventRequest(CreatorId, title, "in 2 hours", ChannelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> CreateSeriesEventAsync(string title)
    {
        var response = await Client.PostAsJsonAsync(
            $"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, "in 2 hours", ChannelId, Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
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
}
