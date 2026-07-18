using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Components;
using CalCrony.Web.Pages.App;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

public class PollComponentTests : TestContext
{
    private const long UserId = 42;

    [Fact]
    public void Vote_panel_highlights_only_the_users_own_votes()
    {
        UseApi(out _);
        var poll = SamplePoll(votes: [(UserId, 0), (77, 1)]);

        var cut = RenderComponent<PollVotePanel>(p => p
            .Add(x => x.Poll, poll)
            .Add(x => x.UserId, UserId));

        var buttons = cut.FindAll("button");
        Assert.Contains("selected", buttons[0].ClassName);
        Assert.DoesNotContain("selected", buttons[1].ClassName ?? "");
    }

    [Fact]
    public async Task Multi_vote_click_submits_the_xor_set()
    {
        UseApi(out var handler);
        var poll = SamplePoll(votes: [(UserId, 0)]);
        handler.NextPoll = poll;

        var cut = RenderComponent<PollVotePanel>(p => p
            .Add(x => x.Poll, poll)
            .Add(x => x.UserId, UserId));

        // Voting for option b while already on a must PUT the full {a, b} set.
        await cut.FindAll("button")[1].ClickAsync(new());

        Assert.Equal($"/polls/{poll.Id}/votes/{UserId}", handler.LastRequest!.RequestUri!.AbsolutePath);
        var body = JsonSerializer.Deserialize<PutPollVotesRequest>(handler.LastBody!, JsonWeb);
        Assert.Equal(2, body!.OptionIds.Count);
        Assert.Contains(poll.Options[0].Id, body.OptionIds);
        Assert.Contains(poll.Options[1].Id, body.OptionIds);
    }

    [Fact]
    public async Task Single_vote_clicking_your_own_option_clears_it()
    {
        UseApi(out var handler);
        var poll = SamplePoll(singleVote: true, votes: [(UserId, 0)]);
        handler.NextPoll = poll;

        var cut = RenderComponent<PollVotePanel>(p => p
            .Add(x => x.Poll, poll)
            .Add(x => x.UserId, UserId));

        await cut.FindAll("button")[0].ClickAsync(new());

        var body = JsonSerializer.Deserialize<PutPollVotesRequest>(handler.LastBody!, JsonWeb);
        Assert.Empty(body!.OptionIds);
    }

    [Fact]
    public void Anonymous_polls_render_counts_and_own_highlight_but_no_names()
    {
        UseApi(out _);
        // Anonymity contract: the DTO carries only the caller's own vote rows.
        var poll = SamplePoll(anonymous: true, votes: [(UserId, 0)], voteCounts: [3, 1]);

        var cut = RenderComponent<PollVotePanel>(p => p
            .Add(x => x.Poll, poll)
            .Add(x => x.UserId, UserId)
            .Add(x => x.NameResolver, (Func<long, string>?)null));

        Assert.Contains("selected", cut.FindAll("button")[0].ClassName);
        Assert.Contains(">3</span>", cut.Markup);
        Assert.DoesNotContain("user ", cut.Markup);
    }

    [Fact]
    public void Time_poll_options_render_local_times_and_closed_polls_lock_voting()
    {
        UseApi(out _);
        var slot = new DateTimeOffset(2030, 6, 1, 18, 0, 0, TimeSpan.Zero);
        var poll = SamplePoll(status: PollStatus.Closed, isTimePoll: true, slots: [slot, slot.AddHours(2)], voteCounts: [2, 0]);

        var cut = RenderComponent<PollVotePanel>(p => p
            .Add(x => x.Poll, poll)
            .Add(x => x.UserId, UserId));

        Assert.Contains("2030-06-01 18:00:00Z", cut.Markup); // LocalTime's stable UTC title attribute
        Assert.Contains("🏆", cut.Markup);
        Assert.All(cut.FindAll("button"), b => Assert.True(b.HasAttribute("disabled")));
    }

    [Fact]
    public void Poll_form_keeps_option_rows_between_two_and_ten()
    {
        UseApi(out _);

        var cut = RenderComponent<PollForm>(p => p.Add(x => x.GuildId, 1));

        Assert.Equal(2, cut.FindAll("[aria-label^='Remove option']").Count);
        Assert.All(cut.FindAll("[aria-label^='Remove option']"), b => Assert.True(b.HasAttribute("disabled")));

        var addButton = () => cut.FindAll("button").First(b => b.TextContent.Contains("Add option"));
        for (var i = 0; i < 8; i++)
        {
            addButton().Click();
        }

        Assert.Equal(10, cut.FindAll("[aria-label^='Remove option']").Count);
        Assert.True(addButton().HasAttribute("disabled"));
        Assert.All(cut.FindAll("[aria-label^='Remove option']"), b => Assert.False(b.HasAttribute("disabled")));
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private void UseApi(out CapturingHandler handler)
    {
        var captured = new CapturingHandler();
        handler = captured;
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(captured) { BaseAddress = new Uri("http://localhost") }));
    }

    private static PollDto SamplePoll(
        bool singleVote = false, bool anonymous = false, bool isTimePoll = false,
        PollStatus status = PollStatus.Open, IReadOnlyList<DateTimeOffset>? slots = null,
        IReadOnlyList<(long UserId, int OptionIndex)>? votes = null, IReadOnlyList<int>? voteCounts = null)
    {
        var options = Enumerable.Range(0, slots?.Count ?? 2)
            .Select(i => new PollOptionDto(
                Guid.NewGuid(), $"option {i}", slots?[i], null, i,
                voteCounts?[i] ?? (votes?.Count(v => v.OptionIndex == i) ?? 0)))
            .ToList();
        var voteDtos = votes?.Select(v => new PollVoteDto(v.UserId, options[v.OptionIndex].Id)).ToList() ?? [];
        return new PollDto(Guid.NewGuid(), 1, 3, "Sample?", isTimePoll, singleVote, anonymous, false,
            2, null, status, null, null, "UTC", null, options, voteDtos);
    }

    /// <summary>Records the last request and answers every call with NextPoll as JSON.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public PollDto? NextPoll { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(NextPoll, JsonWeb), Encoding.UTF8, "application/json"),
            };
        }
    }
}
