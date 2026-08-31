using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot;

/// <summary>Keeps live-list messages current: re-renders the embed from the guild's upcoming
/// events, and clears the record when the message (or its channel) turns out to be gone — a
/// manually deleted list message means the list is over, never reposted (no resurrect loop).
/// Shared by the SyncLiveList delivery handler and the Ready-time reconcile.</summary>
/// <param name="client">The Discord socket client.</param>
/// <param name="api">The CalCrony API client.</param>
/// <param name="logger">The host logger.</param>
public class LiveListManager(DiscordSocketClient client, CalCronyApiClient api, ILogger<LiveListManager> logger)
{
    /// <summary>Re-renders one live list's message in place.</summary>
    /// <param name="list">The live list to sync.</param>
    /// <exception cref="InvalidOperationException">When the events fetch fails — the delivery
    /// handler leaves the row pending for retry (the Ready reconcile catches per list).</exception>
    public async Task SyncAsync(LiveListDto list)
    {
        var events = await api.ListEventsAsync(list.GuildId, limit: list.Limit);
        if (!events.Success || events.Value is null)
        {
            throw new InvalidOperationException($"Failed to list events for live list {list.Id}: {events.Error}");
        }

        if (client.GetGuild((ulong)list.GuildId) is null)
        {
            // The bot left the guild: nothing resolves from here, but the record must survive
            // for a re-invite (Ready's presence sync marks the guild absent so ListAll skips it).
            return;
        }

        if (await client.GetChannelAsync((ulong)list.ChannelId) is not IMessageChannel channel)
        {
            // The channel is gone (deleted, or access revoked) — the message went with it.
            await ClearRecordAsync(list, "its channel no longer resolves");
            return;
        }

        if (await channel.GetMessageAsync((ulong)list.MessageId) is not IUserMessage message)
        {
            // Someone deleted the list message by hand: that's the remove gesture.
            await ClearRecordAsync(list, "its message was deleted");
            return;
        }

        await message.ModifyAsync(m => m.Embed = LiveListEmbedBuilder.Build(events.Value));
    }

    /// <summary>Clears a dead list's record so the API stops enqueueing syncs for it. Best-effort —
    /// a failed delete self-heals on the next sync or Ready reconcile.</summary>
    private async Task ClearRecordAsync(LiveListDto list, string reason)
    {
        logger.LogInformation(
            "Clearing live list {LiveListId} in channel {ChannelId}: {Reason}.", list.Id, list.ChannelId, reason);
        var deleted = await api.DeleteLiveListAsync(list.Id);
        if (!deleted.Success)
        {
            logger.LogWarning(
                "Could not clear live list {LiveListId}: {Error}", list.Id, deleted.Error);
        }
    }
}
