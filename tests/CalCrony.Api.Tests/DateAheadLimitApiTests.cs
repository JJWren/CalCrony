using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The limit on how far ahead a date typed in words may lie, at every route that reads
/// such text. <see cref="DateAheadLimitTests"/> pins the limit itself, to the day; here each route
/// reads text a year or two inside the limit and turns the parser's refusal of text a year past
/// it into a 400 that carries its reason. A year or more either side keeps the results the same
/// whenever the suite runs. The last test is the conversion of a time poll, which reads no text:
/// there a winning time stored past the limit is refused with a 409.</summary>
public class DateAheadLimitApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 9950;
    private const long ChannelId = 9951;
    private const long CreatorId = 9952;

    private const string InsideLimit = "in 9 years";
    private const string PastLimit = "in 11 years";

    private static readonly string TooFarAhead =
        $"is more than {DateTextLimits.MaxYearsAhead} years away; dates can be set up to {DateTextLimits.MaxYearsAhead} years ahead.";

    private static readonly string Refusal = $"\"{PastLimit}\" {TooFarAhead}";

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Parse_datetime_reads_a_date_inside_the_limit_and_refuses_one_past_it()
    {
        var inside = await Client.PostAsJsonAsync(
            "/tools/parse-datetime", new ParseDateTimeRequest(InsideLimit, TimeZone: "UTC"));
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        var past = await Client.PostAsJsonAsync(
            "/tools/parse-datetime", new ParseDateTimeRequest(PastLimit, TimeZone: "UTC"));
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Equal(Refusal, (await past.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Create_event_reads_a_start_inside_the_limit_and_refuses_one_past_it()
    {
        var inside = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Start inside the limit", InsideLimit, ChannelId));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);

        var past = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, "Start past the limit", PastLimit, ChannelId));
        await AssertRefusedAsync(past, Refusal);
    }

    [Fact]
    public async Task Create_event_reads_a_repeat_end_inside_the_limit_and_refuses_one_past_it()
    {
        var inside = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Repeat end inside the limit", "in 2 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatUntilText: InsideLimit));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);

        var past = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Repeat end past the limit", "in 2 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatUntilText: PastLimit));
        await AssertRefusedAsync(past, Refusal);
    }

    [Fact]
    public async Task Edit_event_refuses_a_new_start_past_the_limit_and_takes_one_inside_it()
    {
        var ev = await CreateEventAsync("Moved", "in 2 hours");

        // Refused first, and the event is left as it was.
        var past = await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, WhenText: PastLimit));
        await AssertRefusedAsync(past, Refusal);
        var unmoved = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(ev.StartsAtUtc, unmoved!.StartsAtUtc);

        var inside = await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, WhenText: InsideLimit));
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        var moved = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.True(moved!.StartsAtUtc > ev.StartsAtUtc.AddYears(8));
    }

    [Fact]
    public async Task Edit_series_reads_a_repeat_end_inside_the_limit_and_refuses_one_past_it()
    {
        var created = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Series end moved", "in 2 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ev = (await created.Content.ReadFromJsonAsync<EventDto>())!;

        var inside = await Client.PatchAsJsonAsync($"/series/{ev.SeriesId}",
            new UpdateSeriesRequest(End: SeriesEndChoice.Until, RepeatUntilText: InsideLimit));
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        var past = await Client.PatchAsJsonAsync($"/series/{ev.SeriesId}",
            new UpdateSeriesRequest(End: SeriesEndChoice.Until, RepeatUntilText: PastLimit));
        await AssertRefusedAsync(past, Refusal);
    }

    // The RSVP cutoff has a reader of its own in front of the parser, which puts its own opening
    // words before the parser's reason. A cutoff has to come before the start, so the events here
    // start inside the limit themselves.
    [Fact]
    public async Task Create_event_reads_an_rsvp_cutoff_inside_the_limit_and_refuses_one_past_it()
    {
        var inside = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Cutoff inside the limit", InsideLimit, ChannelId, RsvpCloseText: "in 8 years"));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);

        var past = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Cutoff past the limit", InsideLimit, ChannelId, RsvpCloseText: PastLimit));
        await AssertRsvpCutoffRefusedAsync(past);
    }

    [Fact]
    public async Task Edit_event_reads_an_rsvp_cutoff_inside_the_limit_and_refuses_one_past_it()
    {
        var ev = await CreateEventAsync("Cutoff edited", InsideLimit);

        var inside = await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, RsvpCloseText: "in 8 years"));
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        var past = await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, RsvpCloseText: PastLimit));
        await AssertRsvpCutoffRefusedAsync(past);
    }

    [Fact]
    public async Task Reminder_reads_a_time_inside_the_limit_and_refuses_one_past_it()
    {
        var inside = await Client.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(GuildId, CreatorId, ChannelId, InsideLimit, "inside the limit"));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);

        var past = await Client.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(GuildId, CreatorId, ChannelId, PastLimit, "past the limit"));
        await AssertRefusedAsync(past, Refusal);
    }

    [Fact]
    public async Task Poll_reads_a_closing_time_inside_the_limit_and_refuses_one_past_it()
    {
        var inside = await Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            CreatorId, "Closing time inside the limit", ChannelId, ["one", "two"], ClosesText: InsideLimit));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);

        var past = await Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            CreatorId, "Closing time past the limit", ChannelId, ["one", "two"], ClosesText: PastLimit));
        await AssertRefusedAsync(past, Refusal);
    }

    [Fact]
    public async Task Time_poll_reads_slots_inside_the_limit_and_refuses_one_past_it_when_made_and_when_added()
    {
        var past = await Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            CreatorId, "Time slot past the limit", ChannelId, ["tomorrow 6pm", PastLimit], IsTimePoll: true));
        await AssertRefusedAsync(past, Refusal);

        var inside = await Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            CreatorId, "Time slots inside the limit", ChannelId, ["tomorrow 6pm", InsideLimit], IsTimePoll: true));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);
        var poll = (await inside.Content.ReadFromJsonAsync<PollDto>())!;

        // A slot added to the poll afterwards is read by the same rule.
        var addedPast = await Client.PostAsJsonAsync(
            $"/polls/{poll.Id}/options", new AddPollOptionRequest(CreatorId, PastLimit));
        await AssertRefusedAsync(addedPast, Refusal);

        var addedInside = await Client.PostAsJsonAsync(
            $"/polls/{poll.Id}/options", new AddPollOptionRequest(CreatorId, "in 8 years"));
        Assert.True(addedInside.IsSuccessStatusCode, $"adding a slot inside the limit: {(int)addedInside.StatusCode}");
    }

    // A time poll's slots are read when the poll is made; the winning one becomes an event's start
    // later, without being read again. The slot is written here as a poll made before the limit
    // could hold it, a year either side of the limit.
    [Fact]
    public async Task Convert_poll_refuses_a_winning_time_past_the_limit_and_takes_one_inside_it()
    {
        var created = await Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            CreatorId, "Winning time and the limit", ChannelId, ["in 5 hours", "in 3 hours"], IsTimePoll: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var poll = (await created.Content.ReadFromJsonAsync<PollDto>())!;
        var winner = poll.Options[1];
        (await Client.PutAsJsonAsync($"/polls/{poll.Id}/votes/42", new PutPollVotesRequest([winner.Id]))).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();

        var now = SystemClock.Instance.GetCurrentInstant();
        await SetSlotAsync(winner.Id, now.Plus(Duration.FromDays(11 * 366)));
        var past = await Client.PostAsJsonAsync($"/polls/{poll.Id}/convert", new ConvertPollRequest(CreatorId));
        Assert.Equal(HttpStatusCode.Conflict, past.StatusCode);
        Assert.Equal(
            $"The winning time {TooFarAhead}",
            (await past.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var inside = now.Plus(Duration.FromDays(9 * 365));
        await SetSlotAsync(winner.Id, inside);
        var taken = await Client.PostAsJsonAsync($"/polls/{poll.Id}/convert", new ConvertPollRequest(CreatorId));
        Assert.Equal(HttpStatusCode.Created, taken.StatusCode);
        var ev = (await taken.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(inside.ToUnixTimeSeconds(), ev.StartsAtUtc.ToUnixTimeSeconds());
    }

    private async Task SetSlotAsync(Guid optionId, Instant slot)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.PollOptions.Where(o => o.Id == optionId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.SlotAt, (Instant?)slot));
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string expected)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains(expected, error!.Error);
    }

    private static async Task AssertRsvpCutoffRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
        Assert.Contains("RSVP cutoff", error);
        Assert.Contains(Refusal, error);
    }

    private async Task<EventDto> CreateEventAsync(string title, string when)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, when, ChannelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }
}
