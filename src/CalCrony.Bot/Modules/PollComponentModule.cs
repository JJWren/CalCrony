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
/// <param name="api">The CalCrony API client.</param>
public class PollComponentModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Vote button: toggles the clicked option in the user's set (single-vote replaces/clears).</summary>
    /// <param name="pollIdRaw">The poll id from the component custom id.</param>
    /// <param name="optionIdRaw">The option id from the component custom id.</param>
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

        await SubmitVotesAsync(current.Value, userId, [.. next]);
    }

    /// <summary>Vote select: submits the selection verbatim as the user's full vote set.</summary>
    /// <param name="pollIdRaw">The poll id from the component custom id.</param>
    /// <param name="selections">The submitted select-menu values.</param>
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

        var current = await api.GetPollAsync(pollId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync("This poll no longer exists.", ephemeral: true);
            return;
        }

        await SubmitVotesAsync(current.Value, (long)Context.User.Id, optionIds);
    }

    // No DeferAsync here: a modal must be the interaction's INITIAL response.
    /// <summary>➕ button: opens the add-option modal — must be the initial response, so no DeferAsync
    /// first, and no API call before it either: an API round-trip in front of the initial response
    /// risks Discord's deadline, after which neither a modal nor a refusal can be delivered. The
    /// authoritative live role check runs on submit, where the interaction can be deferred.</summary>
    /// <param name="pollIdRaw">The poll id from the component custom id.</param>
    [ComponentInteraction("polladd:*")]
    public async Task AddOptionButtonAsync(string pollIdRaw)
    {
        await RespondWithModalAsync<AddPollOptionModal>($"polladdmodal:{pollIdRaw}");
    }

    /// <summary>Modal submit: adds the option via the API and re-renders the embed.</summary>
    /// <param name="pollIdRaw">The poll id from the component custom id.</param>
    /// <param name="modal">The submitted modal payload.</param>
    [ModalInteraction("polladdmodal:*")]
    public async Task AddOptionModalAsync(string pollIdRaw, AddPollOptionModal modal)
    {
        await DeferAsync(ephemeral: true);

        if (!Guid.TryParse(pollIdRaw, out var pollId))
        {
            await FollowupAsync("This poll no longer exists.", ephemeral: true);
            return;
        }

        // The live check (ADR 0004) runs here, after the defer: the API trusts bot calls, so a
        // failed lookup stops the submit rather than reaching the trusted mutation unchecked.
        var current = await api.GetPollAsync(pollId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync(current.NotFound ? "This poll no longer exists." : $"❌ {current.Error}", ephemeral: true);
            return;
        }

        if (RoleRestrictionCheck.Denied(Context.User, current.Value.CreatorId, current.Value.AllowedRoles, out var effective))
        {
            await FollowupAsync(RoleRestrictionCheck.Refusal("This poll", effective), ephemeral: true);
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
    /// <param name="pollIdRaw">The poll id from the component custom id.</param>
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

    /// <summary>Submits the vote set, re-renders the embed, and confirms ephemerally. A restricted
    /// poll is checked live first — adding a choice needs the role, removing choices never does,
    /// so a member who lost the role can still toggle their way down to nothing.</summary>
    /// <param name="current">The poll as it stands.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="optionIds">The full vote set to store.</param>
    private async Task SubmitVotesAsync(PollDto current, long userId, IReadOnlyList<Guid> optionIds)
    {
        var alreadyHeld = current.Votes.Where(v => v.UserId == userId).Select(v => v.OptionId).ToHashSet();
        if (optionIds.Any(id => !alreadyHeld.Contains(id))
            && RoleRestrictionCheck.Denied(Context.User, current.CreatorId, current.AllowedRoles, out var effective))
        {
            await FollowupAsync(RoleRestrictionCheck.Refusal("This poll", effective), ephemeral: true);
            return;
        }

        var result = await api.PutPollVotesAsync(current.Id, userId, new PutPollVotesRequest(optionIds));
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
    /// <param name="poll">The poll.</param>
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
