using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot;

/// <summary>Creates, populates, and archives event discussion threads. Strictly best-effort:
/// every operation catches and logs instead of throwing (a thread problem must never fail a
/// delivery or a slash reply), and every "already gone / already archived / user left" state
/// counts as done.</summary>
public class EventThreadManager(DiscordSocketClient client, CalCronyApiClient api, ILogger<EventThreadManager> logger)
{
    private const int NameLimit = 100;

    /// <summary>Pure thread-name mapping (Discord caps thread names at 100), public for direct testing.</summary>
    /// <param name="title">The event title.</param>
    /// <returns>The thread name.</returns>
    public static string BuildName(string title) =>
        title.Length <= NameLimit ? title : title[..NameLimit];

    /// <summary>Opens the event's discussion thread on its just-posted embed message and records
    /// the thread id with the API. No-op unless the event wants a thread and has none yet.</summary>
    /// <param name="ev">The event (post-SetMessage DTO).</param>
    /// <param name="message">The posted embed message.</param>
    public async Task TryCreateAsync(EventDto ev, IUserMessage message)
    {
        if (!ev.WantsThread || ev.ThreadId is not null)
        {
            return;
        }

        try
        {
            if (message.Channel is not ITextChannel channel)
            {
                return;
            }

            var thread = await channel.CreateThreadAsync(
                BuildName(ev.Title), ThreadType.PublicThread, ThreadArchiveDuration.OneWeek, message);
            var recorded = await api.SetThreadAsync(ev.Id, new SetThreadRequest((long)thread.Id));
            if (!recorded.Success)
            {
                logger.LogWarning(
                    "Created thread {ThreadId} for event {EventId} but failed to record it: {Error}",
                    thread.Id, ev.Id, recorded.Error);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort thread creation failed for event {EventId}.", ev.Id);
        }
    }

    /// <summary>Adds a user to the thread (Discord treats re-adding an existing member as a no-op).
    /// A thread that was deleted Discord-side clears the event's stale id so the API stops
    /// enqueueing deliveries for it.</summary>
    /// <param name="eventId">The event id (for stale-id clearing).</param>
    /// <param name="guildId">The Discord guild id.</param>
    /// <param name="threadId">The Discord thread-channel id.</param>
    /// <param name="userId">The Discord user id.</param>
    public async Task TryAddMemberAsync(Guid eventId, long guildId, long threadId, long userId)
    {
        try
        {
            if (await ResolveThreadAsync(guildId, threadId) is not { } thread)
            {
                await ClearStaleThreadAsync(eventId, threadId);
                return;
            }

            var user = await ResolveGuildUserAsync(guildId, userId);
            if (user is null)
            {
                return; // User left the guild.
            }

            await thread.AddUserAsync(user);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Best-effort thread member-add failed for user {UserId} / thread {ThreadId} in guild {GuildId}.",
                userId, threadId, guildId);
        }
    }

    /// <summary>Archives the thread; already archived counts as done, and a thread deleted
    /// Discord-side clears the event's stale id (best-effort — the event row may itself be gone).</summary>
    /// <param name="eventId">The event id (for stale-id clearing).</param>
    /// <param name="guildId">The Discord guild id.</param>
    /// <param name="threadId">The Discord thread-channel id.</param>
    public async Task TryArchiveAsync(Guid eventId, long guildId, long threadId)
    {
        try
        {
            if (await ResolveThreadAsync(guildId, threadId) is not { } thread)
            {
                await ClearStaleThreadAsync(eventId, threadId);
                return;
            }

            if (!thread.IsArchived)
            {
                await thread.ModifyAsync(t => t.Archived = true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Best-effort thread archive failed for thread {ThreadId} in guild {GuildId}.", threadId, guildId);
        }
    }

    /// <summary>Clears a deleted thread's id off the event so the API stops enqueueing thread
    /// deliveries for it. Best-effort — a 404 (event already deleted) is fine.</summary>
    private async Task ClearStaleThreadAsync(Guid eventId, long threadId)
    {
        var cleared = await api.SetThreadAsync(eventId, new SetThreadRequest(null));
        if (!cleared.Success)
        {
            logger.LogDebug(
                "Could not clear stale thread {ThreadId} off event {EventId}: {Error}",
                threadId, eventId, cleared.Error);
        }
    }

    /// <summary>Socket cache first; REST fallback covers auto-archived threads the gateway dropped.</summary>
    private async Task<IThreadChannel?> ResolveThreadAsync(long guildId, long threadId)
    {
        if (client.GetGuild((ulong)guildId) is { } guild
            && guild.GetThreadChannel((ulong)threadId) is { } cached)
        {
            return cached;
        }

        return await client.Rest.GetChannelAsync((ulong)threadId) as IThreadChannel;
    }

    private async Task<IGuildUser?> ResolveGuildUserAsync(long guildId, long userId)
    {
        if (client.GetGuild((ulong)guildId) is { } guild && guild.GetUser((ulong)userId) is { } cached)
        {
            return cached;
        }

        return await client.Rest.GetGuildUserAsync((ulong)guildId, (ulong)userId);
    }
}
