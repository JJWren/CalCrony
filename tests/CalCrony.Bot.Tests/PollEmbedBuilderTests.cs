using CalCrony.Bot;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot.Tests;

public class PollEmbedBuilderTests
{
    [Fact]
    public void Five_options_render_buttons_six_render_select()
    {
        var five = SamplePoll(optionCount: 5);
        var buttons = Assert.Single(PollEmbedBuilder.BuildComponents(five).Components.OfType<ActionRowComponent>());
        Assert.All(buttons.Components, c => Assert.StartsWith("pollvote:", ((ButtonComponent)c).CustomId));

        var six = SamplePoll(optionCount: 6);
        var rows = PollEmbedBuilder.BuildComponents(six).Components.OfType<ActionRowComponent>().ToList();
        var menu = rows.SelectMany(r => r.Components).OfType<SelectMenuComponent>().Single();
        Assert.StartsWith("pollselect:", menu.CustomId);
        Assert.Equal(6, menu.Options.Count);
        Assert.Equal(6, menu.MaxValues);
    }

    [Fact]
    public void Single_vote_select_caps_max_values_at_one()
    {
        var poll = SamplePoll(optionCount: 7, singleVote: true);
        var menu = PollEmbedBuilder.BuildComponents(poll).Components
            .OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<SelectMenuComponent>().Single();
        Assert.Equal(1, menu.MaxValues);
        Assert.Equal(0, menu.MinValues); // empty submit clears votes
    }

    [Fact]
    public void Anonymous_polls_never_render_mentions()
    {
        var poll = SamplePoll(optionCount: 3, anonymous: true, withVotes: true);
        var embed = PollEmbedBuilder.Build(poll);
        Assert.DoesNotContain("<@", embed.Description);
        Assert.Contains("Anonymous", embed.Description);
    }

    [Fact]
    public void Named_polls_render_mentions_with_overflow_cap()
    {
        var options = new List<PollOptionDto> { new(Guid.NewGuid(), "popular", null, null, 0, 20) };
        var votes = Enumerable.Range(1, 20).Select(i => new PollVoteDto(i, options[0].Id)).ToList();
        var poll = new PollDto(Guid.NewGuid(), 1, 2, "Crowded?", false, false, false, false,
            3, 4, PollStatus.Open, null, null, "UTC", null, options, votes);

        var embed = PollEmbedBuilder.Build(poll);

        Assert.Contains("<@1>", embed.Description);
        Assert.Contains("+5 more", embed.Description);
    }

    [Fact]
    public void Closed_poll_shows_trophy_and_no_vote_components()
    {
        var poll = SamplePoll(optionCount: 3, withVotes: true, status: PollStatus.Closed);
        var embed = PollEmbedBuilder.Build(poll);
        Assert.Contains("🏆", embed.Description);
        Assert.Contains("🔒", embed.Description);
        Assert.Empty(PollEmbedBuilder.BuildComponents(poll).Components
            .OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>()
            .Where(b => b.CustomId.StartsWith("pollvote:")));
    }

    [Fact]
    public void Closed_time_poll_offers_convert_until_converted()
    {
        var unconverted = SampleTimePoll(status: PollStatus.Closed);
        var convertButtons = PollEmbedBuilder.BuildComponents(unconverted).Components
            .OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>()
            .Where(b => b.CustomId.StartsWith("pollconvert:"));
        Assert.Single(convertButtons);

        var converted = SampleTimePoll(status: PollStatus.Closed, convertedEventId: Guid.NewGuid());
        Assert.Empty(PollEmbedBuilder.BuildComponents(converted).Components.OfType<ActionRowComponent>());
        Assert.Contains("🎉", PollEmbedBuilder.Build(converted).Description);
    }

    [Fact]
    public void Time_polls_render_discord_timestamps_and_add_button_when_allowed()
    {
        var poll = SampleTimePoll(allowUserOptions: true);
        var embed = PollEmbedBuilder.Build(poll);
        Assert.Contains($"<t:{poll.Options[0].SlotAtUnix}:F>", embed.Description);

        var addButtons = PollEmbedBuilder.BuildComponents(poll).Components
            .OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>()
            .Where(b => b.CustomId.StartsWith("polladd:"));
        Assert.Single(addButtons);
    }

    private static PollDto SamplePoll(
        int optionCount, bool singleVote = false, bool anonymous = false, bool withVotes = false,
        PollStatus status = PollStatus.Open)
    {
        var options = Enumerable.Range(0, optionCount)
            .Select(i => new PollOptionDto(Guid.NewGuid(), $"option {i}", null, null, i, withVotes && i == 0 ? 2 : 0))
            .ToList();
        var votes = withVotes && !anonymous
            ? new List<PollVoteDto> { new(42, options[0].Id), new(43, options[0].Id) }
            : [];
        return new PollDto(Guid.NewGuid(), 1, 2, "Sample?", false, singleVote, anonymous, false,
            3, 4, status, null, status == PollStatus.Closed ? DateTimeOffset.UtcNow : null, "UTC", null, options, votes);
    }

    private static PollDto SampleTimePoll(
        PollStatus status = PollStatus.Open, Guid? convertedEventId = null, bool allowUserOptions = false)
    {
        var now = DateTimeOffset.UtcNow;
        var options = new List<PollOptionDto>
        {
            new(Guid.NewGuid(), "in 2 hours", now.AddHours(2), null, 0, 1),
            new(Guid.NewGuid(), "in 4 hours", now.AddHours(4), null, 1, 0),
        };
        return new PollDto(Guid.NewGuid(), 1, 2, "When?", true, false, false, allowUserOptions,
            3, 4, status, null, status == PollStatus.Closed ? now : null, "UTC", convertedEventId, options, []);
    }
}
