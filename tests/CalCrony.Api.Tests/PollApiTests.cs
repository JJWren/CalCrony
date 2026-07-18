using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

public class PollApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 9000;
    private const long ChannelId = 9001;
    private const long CreatorId = 9002;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Create_validates_option_counts_and_close_text()
    {
        var one = await PostPollAsync(new CreatePollRequest(CreatorId, "One?", ChannelId, ["only"]));
        Assert.Equal(HttpStatusCode.BadRequest, one.StatusCode);

        var eleven = await PostPollAsync(new CreatePollRequest(
            CreatorId, "Eleven?", ChannelId, [.. Enumerable.Range(1, 11).Select(i => $"o{i}")]));
        Assert.Equal(HttpStatusCode.BadRequest, eleven.StatusCode);

        var badCloses = await PostPollAsync(new CreatePollRequest(
            CreatorId, "Bad closes?", ChannelId, ["a", "b"], ClosesText: "flurble"));
        Assert.Equal(HttpStatusCode.BadRequest, badCloses.StatusCode);
        Assert.Contains("Close time", (await badCloses.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Time_poll_parses_slots_names_bad_options_and_orders_chronologically()
    {
        var bad = await PostPollAsync(new CreatePollRequest(
            CreatorId, "When?", ChannelId, ["in 3 hours", "banana"], IsTimePoll: true));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("banana", (await bad.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        // Entered out of order — must come back chronological.
        var created = await CreatePollAsync(new CreatePollRequest(
            CreatorId, "When raid?", ChannelId, ["in 6 hours", "in 2 hours", "in 4 hours"], IsTimePoll: true));
        var slots = created.Options.Select(o => o.SlotAtUtc!.Value).ToList();
        Assert.True(slots.SequenceEqual(slots.OrderBy(s => s)));
        Assert.False(created.SingleVote); // time polls are always multi-vote
    }

    [Fact]
    public async Task Duplicate_time_slots_are_rejected()
    {
        var response = await PostPollAsync(new CreatePollRequest(
            CreatorId, "Dupes?", ChannelId, ["in 2 hours", "in 120 minutes"], IsTimePoll: true));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Vote_set_replacement_supports_multi_single_and_clear()
    {
        var poll = await CreatePollAsync(new CreatePollRequest(CreatorId, "Multi?", ChannelId, ["a", "b", "c"]));
        var (a, b) = (poll.Options[0].Id, poll.Options[1].Id);

        var multi = await PutVotesAsync(poll.Id, 42, [a, b]);
        Assert.Equal(2, multi.Options.Where(o => o.Id == a || o.Id == b).Sum(o => o.VoteCount));

        var reduced = await PutVotesAsync(poll.Id, 42, [b]);
        Assert.Equal(0, reduced.Options.Single(o => o.Id == a).VoteCount);
        Assert.Equal(1, reduced.Options.Single(o => o.Id == b).VoteCount);

        var cleared = await PutVotesAsync(poll.Id, 42, []);
        Assert.Equal(0, cleared.Options.Sum(o => o.VoteCount));

        var single = await CreatePollAsync(new CreatePollRequest(CreatorId, "Single?", ChannelId, ["x", "y"], SingleVote: true));
        var two = await Client.PutAsJsonAsync($"/polls/{single.Id}/votes/42",
            new PutPollVotesRequest([single.Options[0].Id, single.Options[1].Id]));
        Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);
    }

    [Fact]
    public async Task Votes_on_closed_polls_and_foreign_options_are_rejected()
    {
        var poll = await CreatePollAsync(new CreatePollRequest(CreatorId, "Closing?", ChannelId, ["a", "b"]));
        var foreign = await Client.PutAsJsonAsync($"/polls/{poll.Id}/votes/42", new PutPollVotesRequest([Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        (await Client.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();
        var late = await Client.PutAsJsonAsync($"/polls/{poll.Id}/votes/42", new PutPollVotesRequest([poll.Options[0].Id]));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Add_option_respects_cap_and_time_parsing()
    {
        var poll = await CreatePollAsync(new CreatePollRequest(
            CreatorId, "Add more?", ChannelId, [.. Enumerable.Range(1, 9).Select(i => $"o{i}")], AllowUserOptions: true));

        var tenth = await Client.PostAsJsonAsync($"/polls/{poll.Id}/options", new AddPollOptionRequest(43, "o10"));
        Assert.Equal(HttpStatusCode.Created, tenth.StatusCode);

        var eleventh = await Client.PostAsJsonAsync($"/polls/{poll.Id}/options", new AddPollOptionRequest(43, "o11"));
        Assert.Equal(HttpStatusCode.Conflict, eleventh.StatusCode);

        var timePoll = await CreatePollAsync(new CreatePollRequest(
            CreatorId, "Add time?", ChannelId, ["in 2 hours", "in 4 hours"], IsTimePoll: true, AllowUserOptions: true));
        var badSlot = await Client.PostAsJsonAsync($"/polls/{timePoll.Id}/options", new AddPollOptionRequest(43, "gibberish"));
        Assert.Equal(HttpStatusCode.BadRequest, badSlot.StatusCode);
    }

    [Fact]
    public async Task Close_is_idempotent()
    {
        var poll = await CreatePollAsync(new CreatePollRequest(CreatorId, "Twice?", ChannelId, ["a", "b"]));
        (await Client.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();
        var second = await Client.PostAsync($"/polls/{poll.Id}/close", null);
        second.EnsureSuccessStatusCode();
        Assert.Equal(PollStatus.Closed, (await second.Content.ReadFromJsonAsync<PollDto>())!.Status);
    }

    [Fact]
    public async Task Convert_creates_event_from_winner_with_inherited_channel_and_enqueues_post()
    {
        // 200 chars: within the 252-char question cap, beyond the 128-char event-title cap.
        var poll = await CreatePollAsync(new CreatePollRequest(
            CreatorId, new string('q', 200),
            ChannelId, ["in 5 hours", "in 3 hours"], IsTimePoll: true));
        var earliest = poll.Options[0]; // chronological ordering => index 0 is "in 3 hours"
        var latest = poll.Options[1];

        // Latest slot wins on votes.
        await PutVotesAsync(poll.Id, 42, [latest.Id]);
        await PutVotesAsync(poll.Id, 43, [latest.Id]);
        await PutVotesAsync(poll.Id, 44, [earliest.Id]);
        (await Client.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();

        var convert = await Client.PostAsJsonAsync($"/polls/{poll.Id}/convert", new ConvertPollRequest(CreatorId, DurationMinutes: 90));
        Assert.Equal(HttpStatusCode.Created, convert.StatusCode);
        var ev = (await convert.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(latest.SlotAtUtc, ev.StartsAtUtc);
        Assert.Equal(ChannelId, ev.ChannelId);           // poll's channel, not any default
        Assert.Equal(128, ev.Title.Length);              // 252-char question truncated
        Assert.Equal(90, ev.DurationMinutes);
        Assert.Equal(3, ev.Options.Count);

        // Bot caller too: the event posts via the outbox.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.True(await db.Deliveries.AnyAsync(d =>
            d.Type == DeliveryType.PostEventMessage && d.PayloadJson.Contains(ev.Id.ToString())));

        var again = await Client.PostAsJsonAsync($"/polls/{poll.Id}/convert", new ConvertPollRequest(CreatorId));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Convert_ties_break_to_earliest_slot_even_with_zero_votes()
    {
        var poll = await CreatePollAsync(new CreatePollRequest(
            CreatorId, "Tie?", ChannelId, ["in 8 hours", "in 6 hours"], IsTimePoll: true));
        (await Client.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();

        var convert = await Client.PostAsJsonAsync($"/polls/{poll.Id}/convert", new ConvertPollRequest(CreatorId));
        Assert.Equal(HttpStatusCode.Created, convert.StatusCode);
        var ev = (await convert.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(poll.Options[0].SlotAtUtc, ev.StartsAtUtc); // chronological index 0 = earliest
    }

    [Fact]
    public async Task Convert_rejects_non_time_and_open_polls()
    {
        var standard = await CreatePollAsync(new CreatePollRequest(CreatorId, "Standard?", ChannelId, ["a", "b"]));
        (await Client.PostAsync($"/polls/{standard.Id}/close", null)).EnsureSuccessStatusCode();
        var nonTime = await Client.PostAsJsonAsync($"/polls/{standard.Id}/convert", new ConvertPollRequest(CreatorId));
        Assert.Equal(HttpStatusCode.BadRequest, nonTime.StatusCode);

        var open = await CreatePollAsync(new CreatePollRequest(
            CreatorId, "Open time?", ChannelId, ["in 2 hours", "in 4 hours"], IsTimePoll: true));
        var stillOpen = await Client.PostAsJsonAsync($"/polls/{open.Id}/convert", new ConvertPollRequest(CreatorId));
        Assert.Equal(HttpStatusCode.Conflict, stillOpen.StatusCode);
    }

    private Task<HttpResponseMessage> PostPollAsync(CreatePollRequest request) =>
        Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", request);

    private async Task<PollDto> CreatePollAsync(CreatePollRequest request)
    {
        var response = await PostPollAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PollDto>())!;
    }

    private async Task<PollDto> PutVotesAsync(Guid pollId, long userId, IReadOnlyList<Guid> optionIds)
    {
        var response = await Client.PutAsJsonAsync($"/polls/{pollId}/votes/{userId}", new PutPollVotesRequest(optionIds));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PollDto>())!;
    }
}
