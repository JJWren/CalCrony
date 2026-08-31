using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/livelist — a persistent upcoming-events embed the bot keeps current (managers only).
/// One per channel; the bot rewrites it (debounced through the outbox) whenever the guild's
/// events change, and a manually deleted message simply ends the list.</summary>
/// <param name="api">The CalCrony API client.</param>
[RequireContext(ContextType.Guild)]
[Group("livelist", "A persistent events list the bot keeps up to date")]
public class LiveListModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Posts the live-list embed and registers it with the API; compensates by deleting
    /// the post when registration fails (e.g. the channel already has one).</summary>
    /// <param name="channel">Target text channel (defaults to the current one).</param>
    /// <param name="limit">Maximum number of events the list shows.</param>
    [SlashCommand("create", "Post an auto-updating list of upcoming events (managers only)")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task CreateAsync(
        [Summary(description: "Channel to post the list in (defaults to here)")] ITextChannel? channel = null,
        [Summary(description: "Max number of events shown (1-25)"), MinValue(1), MaxValue(25)] int limit = 10)
    {
        await DeferAsync(ephemeral: true);

        var targetChannel = channel ?? Context.Channel as ITextChannel;
        if (targetChannel is null)
        {
            await FollowupAsync("Live lists can only be posted in text channels.", ephemeral: true);
            return;
        }

        var events = await api.ListEventsAsync((long)Context.Guild.Id, limit: limit);
        if (!events.Success || events.Value is null)
        {
            await FollowupAsync($"❌ {events.Error}", ephemeral: true);
            return;
        }

        var message = await targetChannel.SendMessageAsync(embed: LiveListEmbedBuilder.Build(events.Value));
        var recorded = await api.CreateLiveListAsync(
            (long)Context.Guild.Id,
            new CreateLiveListRequest(
                (long)Context.User.Id, (long)targetChannel.Id, (long)message.Id, limit, targetChannel.Name));
        if (!recorded.Success)
        {
            // Unregistered, the embed would never update — remove it and surface the refusal
            // (mirrors the PostEventMessage compensation).
            try
            {
                await message.DeleteAsync();
            }
            catch
            {
                // Best effort; a stray static embed beats a silent one that never syncs.
            }

            await FollowupAsync($"❌ {recorded.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"📌 Live list posted in {targetChannel.Mention} — I'll keep it updated as events change. " +
            "Deleting the message removes the list.",
            ephemeral: true);
    }

    /// <summary>Removes a channel's live list: clears the record first (so syncs stop), then
    /// deletes the message best-effort.</summary>
    /// <param name="channel">Target text channel (defaults to the current one).</param>
    [SlashCommand("remove", "Remove a channel's live list (managers only)")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task RemoveAsync(
        [Summary(description: "Channel whose live list to remove (defaults to here)")] ITextChannel? channel = null)
    {
        await DeferAsync(ephemeral: true);

        var targetChannel = channel ?? Context.Channel as ITextChannel;
        if (targetChannel is null)
        {
            await FollowupAsync("Live lists only exist in text channels.", ephemeral: true);
            return;
        }

        var lists = await api.ListGuildLiveListsAsync((long)Context.Guild.Id);
        if (!lists.Success || lists.Value is null)
        {
            await FollowupAsync($"❌ {lists.Error}", ephemeral: true);
            return;
        }

        var list = lists.Value.FirstOrDefault(l => l.ChannelId == (long)targetChannel.Id);
        if (list is null)
        {
            await FollowupAsync($"There's no live list in {targetChannel.Mention}.", ephemeral: true);
            return;
        }

        var deleted = await api.DeleteLiveListAsync(list.Id);
        if (!deleted.Success)
        {
            await FollowupAsync($"❌ {deleted.Error}", ephemeral: true);
            return;
        }

        try
        {
            if (await targetChannel.GetMessageAsync((ulong)list.MessageId) is { } message)
            {
                await message.DeleteAsync();
            }
        }
        catch
        {
            // Already gone (manually deleted); fine.
        }

        await FollowupAsync($"🗑️ Removed the live list from {targetChannel.Mention}.", ephemeral: true);
    }
}
