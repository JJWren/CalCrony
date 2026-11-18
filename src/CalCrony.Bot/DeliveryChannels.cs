using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

/// <summary>Finds the channel for a delivery. A delivery is for one guild, and it is posted —
/// and an embed of it re-rendered — only in a channel of that guild: a channel id that leads
/// anywhere else is not handed out, and the delivery is logged.</summary>
/// <param name="getChannel">Reads a channel by id, as the Discord client does; gives null when
/// there is none.</param>
/// <param name="logger">Where a delivery whose channel lies outside its guild is logged.</param>
public sealed class DeliveryChannels(Func<ulong, ValueTask<IChannel?>> getChannel, ILogger logger)
{
    /// <summary>What looking for a delivery's channel found.</summary>
    /// <param name="Channel">The channel; null when there is none to hand out.</param>
    /// <param name="OutsideGuild">True when a message channel is there and is not a channel of
    /// the delivery's guild.</param>
    public readonly record struct Found(IMessageChannel? Channel, bool OutsideGuild);

    /// <summary>The channel to post a delivery in.</summary>
    /// <param name="delivery">The outbox row; it is posted in the channel it names.</param>
    /// <param name="guildId">The guild the delivery is for; null when it names none, and then
    /// nothing is compared.</param>
    /// <returns>The channel; or null when the delivery is to be dropped, because its channel
    /// lies outside its guild and another try would find the same channel.</returns>
    /// <exception cref="InvalidOperationException">When there is no such message channel: the
    /// caller leaves the delivery pending, to be tried again.</exception>
    public async Task<IMessageChannel?> ForPostAsync(DeliveryDto delivery, long? guildId)
    {
        var found = await FindAsync(delivery.ChannelId, guildId);
        if (found.Channel is not null)
        {
            return found.Channel;
        }

        if (!found.OutsideGuild)
        {
            throw new InvalidOperationException($"Channel {delivery.ChannelId} not found or not a message channel.");
        }

        LogOutside(delivery, delivery.ChannelId, guildId);
        return null;
    }

    /// <summary>The channel in which a delivery re-renders an embed that is already posted.</summary>
    /// <param name="delivery">The outbox row.</param>
    /// <param name="channelId">The channel the embed was posted in.</param>
    /// <param name="guildId">The guild the event or the poll belongs to.</param>
    /// <returns>The channel; or null when there is nothing to re-render, because the channel is
    /// gone or lies outside the guild.</returns>
    public async Task<IMessageChannel?> ForRerenderAsync(DeliveryDto delivery, long channelId, long guildId)
    {
        var found = await FindAsync(channelId, guildId);
        if (found.OutsideGuild)
        {
            LogOutside(delivery, channelId, guildId);
        }

        return found.Channel;
    }

    /// <summary>The message channel with this id, when it belongs to the delivery's guild.</summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="guildId">The guild the delivery is for; null when it names none, and then
    /// nothing is compared.</param>
    /// <returns>The channel; or no channel, with <see cref="Found.OutsideGuild"/> saying whether
    /// that is because it lies outside the guild or because there is no such message channel.</returns>
    public async Task<Found> FindAsync(long channelId, long? guildId)
    {
        if (await getChannel((ulong)channelId) is not IMessageChannel channel)
        {
            return new Found(null, OutsideGuild: false);
        }

        return IsInGuild((channel as IGuildChannel)?.GuildId, guildId)
            ? new Found(channel, OutsideGuild: false)
            : new Found(null, OutsideGuild: true);
    }

    /// <summary>Whether a channel belongs to the guild a delivery is for.</summary>
    /// <param name="channelGuildId">The guild the channel belongs to; null when it belongs to none.</param>
    /// <param name="guildId">The guild the delivery is for, when it names one.</param>
    /// <returns>True when the delivery names no guild, or the channel is one of that guild's.</returns>
    public static bool IsInGuild(ulong? channelGuildId, long? guildId) =>
        guildId is not long id || (id >= 0 && channelGuildId == (ulong)id);

    private void LogOutside(DeliveryDto delivery, long channelId, long? guildId) =>
        logger.LogWarning(
            "Discarding delivery {DeliveryId} ({Type}): channel {ChannelId} is not in guild {GuildId}, which the delivery is for.",
            delivery.Id,
            delivery.Type,
            channelId,
            guildId);
}
