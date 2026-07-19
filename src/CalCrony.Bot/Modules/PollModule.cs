using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/poll — create standard and time polls, close them, and convert time-poll winners.</summary>
/// <param name="api">The CalCrony API client.</param>
[RequireContext(ContextType.Guild)]
[Group("poll", "Create and manage polls")]
public class PollModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Creates a standard poll from comma-separated options.</summary>
    /// <param name="question">The poll question.</param>
    /// <param name="options">Comma-separated option texts.</param>
    /// <param name="singleVote">When true, each voter gets exactly one choice.</param>
    /// <param name="anonymous">When true, embeds show counts without voter names.</param>
    /// <param name="allowOptions">When true, voters may add options.</param>
    /// <param name="closes">Optional natural-language close deadline.</param>
    [SlashCommand("create", "Create a poll")]
    public async Task CreateAsync(
        [Summary(description: "The question to ask")] string question,
        [Summary(description: "Comma-separated choices, 2-10, e.g. \"Pizza, Tacos, Sushi\"")] string options,
        [Summary("single-vote", "Each person may pick only one option")] bool singleVote = false,
        [Summary(description: "Hide who voted — show counts only")] bool anonymous = false,
        [Summary("allow-options", "Let voters add their own options")] bool allowOptions = false,
        [Summary(description: "When voting ends, e.g. \"friday 5pm\" — leave empty for manual close")] string? closes = null)
    {
        await CreateCoreAsync(question, options, isTimePoll: false, singleVote, anonymous, allowOptions, closes);
    }

    /// <summary>Creates a time poll whose options are natural-language slots (always multi-vote).</summary>
    /// <param name="question">The poll question.</param>
    /// <param name="slots">Comma-separated natural-language time slots.</param>
    /// <param name="anonymous">When true, embeds show counts without voter names.</param>
    /// <param name="allowOptions">When true, voters may add options.</param>
    /// <param name="closes">Optional natural-language close deadline.</param>
    [SlashCommand("time", "Create a time poll — vote for the best time")]
    public async Task TimeAsync(
        [Summary(description: "What are you scheduling?")] string question,
        [Summary(description: "Comma-separated times, e.g. \"friday 7pm, saturday 3pm, sunday noon\"")] string slots,
        [Summary(description: "Hide who voted — show counts only")] bool anonymous = false,
        [Summary("allow-options", "Let voters add their own times")] bool allowOptions = false,
        [Summary(description: "When voting ends, e.g. \"thursday noon\" — leave empty for manual close")] string? closes = null)
    {
        await CreateCoreAsync(question, slots, isTimePoll: true, singleVote: false, anonymous, allowOptions, closes);
    }

    /// <summary>Closes an open poll by name (creator or manager) and re-renders its embed.</summary>
    /// <param name="name">Event title (or fragment), or an autocomplete-picked event id.</param>
    [SlashCommand("close", "Close a poll and show the result")]
    public async Task CloseAsync([Summary("name", "Poll question (or part of it)")] string name)
    {
        await DeferAsync(ephemeral: true);

        var (poll, problem) = await PollFinder.FindSingleAsync(api, (long)Context.Guild.Id, name, PollStatus.Open);
        if (poll is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (!CanManage(poll))
        {
            await FollowupAsync("Only the poll creator or a server manager can close this poll.", ephemeral: true);
            return;
        }

        var result = await api.ClosePollAsync(poll.Id);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await TryUpdateMessageAsync(result.Value);
        var winner = result.Value.Options.OrderByDescending(o => o.VoteCount).FirstOrDefault();
        await FollowupAsync(
            $"🔒 Closed **{result.Value.Question}**" +
            (winner is null ? "." : $" — top answer: **{winner.Text}** with {winner.VoteCount} vote{(winner.VoteCount == 1 ? "" : "s")}."),
            ephemeral: true);
    }

    /// <summary>Converts a closed time poll's winner into an event (creator or manager).</summary>
    /// <param name="name">Event title (or fragment), or an autocomplete-picked event id.</param>
    /// <param name="title">The event title.</param>
    /// <param name="duration">Duration in minutes.</param>
    [SlashCommand("convert", "Turn a closed time poll's winning time into an event")]
    public async Task ConvertAsync(
        [Summary("name", "Poll question (or part of it)")] string name,
        [Summary(description: "Event title (defaults to the poll question)")] string? title = null,
        [Summary("duration", "Event duration in minutes")] int? duration = null)
    {
        await DeferAsync(ephemeral: true);

        var (poll, problem) = await PollFinder.FindSingleAsync(api, (long)Context.Guild.Id, name, PollStatus.Closed);
        if (poll is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (!poll.IsTimePoll)
        {
            await FollowupAsync($"**{poll.Question}** isn't a time poll — only time polls convert to events.", ephemeral: true);
            return;
        }

        if (!CanManage(poll))
        {
            await FollowupAsync("Only the poll creator or a server manager can convert this poll.", ephemeral: true);
            return;
        }

        var result = await api.ConvertPollAsync(poll.Id, new ConvertPollRequest((long)Context.User.Id, title, duration));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var refreshed = await api.GetPollAsync(poll.Id);
        if (refreshed.Success && refreshed.Value is not null)
        {
            await TryUpdateMessageAsync(refreshed.Value);
        }

        await FollowupAsync(
            $"✅ Event **{result.Value.Title}** created for <t:{result.Value.StartsAtUnix}:F> — " +
            $"its embed will appear in <#{result.Value.ChannelId}> shortly.",
            ephemeral: true);
    }

    /// <summary>Shared create flow: API call, embed post, message-id recording, ephemeral confirm.</summary>
    /// <param name="question">The poll question.</param>
    /// <param name="optionsCsv">Comma-separated option texts.</param>
    /// <param name="isTimePoll">True when options are candidate time slots.</param>
    /// <param name="singleVote">When true, each voter gets exactly one choice.</param>
    /// <param name="anonymous">When true, embeds show counts without voter names.</param>
    /// <param name="allowOptions">When true, voters may add options.</param>
    /// <param name="closes">Optional natural-language close deadline.</param>
    private async Task CreateCoreAsync(
        string question, string optionsCsv, bool isTimePoll, bool singleVote, bool anonymous, bool allowOptions, string? closes)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Channel is not ITextChannel channel)
        {
            await FollowupAsync("Polls can only be created in text channels.", ephemeral: true);
            return;
        }

        var options = optionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await api.CreatePollAsync((long)Context.Guild.Id, new CreatePollRequest(
            (long)Context.User.Id, question, (long)channel.Id, options,
            isTimePoll, singleVote, anonymous, allowOptions, closes));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var poll = result.Value;
        var message = await channel.SendMessageAsync(
            embed: PollEmbedBuilder.Build(poll),
            components: PollEmbedBuilder.BuildComponents(poll));
        await api.SetPollMessageAsync(poll.Id, new SetPollMessageRequest((long)channel.Id, (long)message.Id));

        await FollowupAsync(
            $"📊 **{poll.Question}** is live in {channel.Mention}" +
            (poll.ClosesAtUnix is { } unix ? $" — closes <t:{unix}:R>." : "."),
            ephemeral: true);
    }

    /// <summary>Creator-or-ManageGuild check mirroring the API guard.</summary>
    /// <param name="poll">The poll.</param>
    /// <returns>True for the creator or a ManageGuild holder.</returns>
    private bool CanManage(PollDto poll) =>
        (long)Context.User.Id == poll.CreatorId ||
        (Context.User is IGuildUser guildUser && guildUser.GuildPermissions.ManageGuild);

    /// <summary>Re-renders the posted poll embed in place; tolerates a manually deleted message.</summary>
    /// <param name="poll">The poll.</param>
    private async Task TryUpdateMessageAsync(PollDto poll)
    {
        if (poll.MessageId is not long messageId)
        {
            return;
        }

        try
        {
            var channel = Context.Guild.GetTextChannel((ulong)poll.ChannelId);
            if (channel is not null && await channel.GetMessageAsync((ulong)messageId) is IUserMessage message)
            {
                await message.ModifyAsync(m =>
                {
                    m.Embed = PollEmbedBuilder.Build(poll);
                    m.Components = PollEmbedBuilder.BuildComponents(poll);
                });
            }
        }
        catch
        {
            // The posted message may have been deleted manually; not fatal.
        }
    }
}
