using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot.Modules;

/// <summary>Modal for voter-added poll options; the single input doubles as slot text on time polls.</summary>
public class AddPollOptionModal : IModal
{
    public string Title => "Add poll option";

    [InputLabel("Your option")]
    [ModalTextInput("option_text", maxLength: 100, placeholder: "e.g. Blue — or for time polls: saturday 7pm")]
    public string OptionText { get; set; } = "";
}

/// <summary>Handles poll message components: vote buttons, the vote select, the add-option modal, and convert.</summary>
public class PollComponentModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Vote button: toggles the clicked option in the user's set (single-vote replaces/clears).</summary>
    [ComponentInteraction("pollvote:*:*")]
    public async Task VoteButtonAsync(string pollIdRaw, string optionIdRaw)
    {
        await DeferAsync(ephemeral: true);

        if (!Guid.TryParse(pollIdRaw, out var pollId) || !Guid.TryParse(optionIdRaw, out var optionId))
        {
            await FollowupAsync("This poll button is broken — the poll may have been recreated.", ephemeral: true);
            return;
        }

        var current = await api.GetPollAsync(pollId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync("This poll no longer exists.", ephemeral: true);
            return;
        }

        var userId = (long)Context.User.Id;
        var mine = current.Value.Votes.Where(v => v.UserId == userId).Select(v => v.OptionId).ToHashSet();

        // Button semantics: single-vote = click to set, click your choice again to clear;
        // multi-vote = toggle the clicked option within your set.
        HashSet<Guid> next;
        if (current.Value.SingleVote)
        {
            next = mine.Contains(optionId) ? [] : [optionId];
        }
        else
        {
            next = [.. mine];
            if (!next.Remove(optionId))
            {
                next.Add(optionId);
            }
        }

        await SubmitVotesAsync(pollId, userId, [.. next]);
    }

    /// <summary>Vote select: submits the selection verbatim as the user's full vote set.</summary>
    [ComponentInteraction("pollselect:*")]
    public async Task VoteSelectAsync(string pollIdRaw, string[] selections)
    {
        await DeferAsync(ephemeral: true);

        if (!Guid.TryParse(pollIdRaw, out var pollId))
        {
            await FollowupAsync("This poll menu is broken — the poll may have been recreated.", ephemeral: true);
            return;
        }

        var optionIds = new List<Guid>();
        foreach (var selection in selections)
        {
            if (!Guid.TryParse(selection, out var optionId))
            {
                await FollowupAsync("This poll menu is out of date — try again after the message refreshes.", ephemeral: true);
                return;
            }

            optionIds.Add(optionId);
        }

        await SubmitVotesAsync(pollId, (long)Context.User.Id, optionIds);
    }

    // No DeferAsync here: a modal must be the interaction's INITIAL response.
    /// <summary>➕ button: opens the add-option modal — must be the initial response, so no DeferAsync first.</summary>
    [ComponentInteraction("polladd:*")]
    public async Task AddOptionButtonAsync(string pollIdRaw)
    {
        await RespondWithModalAsync<AddPollOptionModal>($"polladdmodal:{pollIdRaw}");
    }

    /// <summary>Modal submit: adds the option via the API and re-renders the embed.</summary>
    [ModalInteraction("polladdmodal:*")]
    public async Task AddOptionModalAsync(string pollIdRaw, AddPollOptionModal modal)
    {
        await DeferAsync(ephemeral: true);

        if (!Guid.TryParse(pollIdRaw, out var pollId))
        {
            await FollowupAsync("This poll no longer exists.", ephemeral: true);
            return;
        }

        var result = await api.AddPollOptionAsync(pollId, new AddPollOptionRequest((long)Context.User.Id, modal.OptionText));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await UpdatePollMessageAsync(result.Value);
        await FollowupAsync($"➕ Added **{modal.OptionText.Trim()}**.", ephemeral: true);
    }

    /// <summary>Convert button: turns the closed time poll's winner into an event (creator or manager).</summary>
    [ComponentInteraction("pollconvert:*")]
    public async Task ConvertButtonAsync(string pollIdRaw)
    {
        await DeferAsync(ephemeral: true);

        if (!Guid.TryParse(pollIdRaw, out var pollId))
        {
            await FollowupAsync("This poll no longer exists.", ephemeral: true);
            return;
        }

        var current = await api.GetPollAsync(pollId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync("This poll no longer exists.", ephemeral: true);
            return;
        }

        var canManage = (long)Context.User.Id == current.Value.CreatorId ||
                        (Context.User is IGuildUser guildUser && guildUser.GuildPermissions.ManageGuild);
        if (!canManage)
        {
            await FollowupAsync("Only the poll creator or a server manager can create the event.", ephemeral: true);
            return;
        }

        var result = await api.ConvertPollAsync(pollId, new ConvertPollRequest((long)Context.User.Id));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var refreshed = await api.GetPollAsync(pollId);
        if (refreshed.Success && refreshed.Value is not null)
        {
            await UpdatePollMessageAsync(refreshed.Value);
        }

        await FollowupAsync(
            $"✅ Event **{result.Value.Title}** created for <t:{result.Value.StartsAtUnix}:F> — its embed will appear shortly.",
            ephemeral: true);
    }

    /// <summary>Submits the vote set, re-renders the embed, and confirms ephemerally.</summary>
    private async Task SubmitVotesAsync(Guid pollId, long userId, IReadOnlyList<Guid> optionIds)
    {
        var result = await api.PutPollVotesAsync(pollId, userId, new PutPollVotesRequest(optionIds));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var poll = result.Value;
        await UpdatePollMessageAsync(poll);

        if (optionIds.Count == 0)
        {
            await FollowupAsync("Vote cleared.", ephemeral: true);
            return;
        }

        var picks = poll.Options
            .Where(o => optionIds.Contains(o.Id))
            .Select(o => poll.IsTimePoll && o.SlotAtUnix is { } unix ? $"<t:{unix}:f>" : $"**{o.Text}**");
        await FollowupAsync($"Your vote: {string.Join(", ", picks)}", ephemeral: true);
    }

    /// <summary>Re-renders the poll message from the interaction's message or the recorded channel/message ids.</summary>
    private async Task UpdatePollMessageAsync(PollDto poll)
    {
        // Component interactions carry their message; modal interactions may not — fall back
        // to fetching via the poll's recorded channel/message ids.
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.Message.ModifyAsync(m =>
            {
                m.Embed = PollEmbedBuilder.Build(poll);
                m.Components = PollEmbedBuilder.BuildComponents(poll);
            });
            return;
        }

        if (poll.MessageId is not long messageId)
        {
            return;
        }

        try
        {
            var channel = Context.Guild?.GetTextChannel((ulong)poll.ChannelId);
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
            // Message may be gone; the next sync delivery or interaction will repair it.
        }
    }
}
