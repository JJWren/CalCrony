using CalCrony.Contracts;
using Discord;
using Microsoft.Extensions.Logging;
using static CalCrony.Bot.Tests.DeliveryStandIns;

namespace CalCrony.Bot.Tests;

/// <summary>Where a delivery may be posted, and an embed of it re-rendered: only in a message
/// channel of the guild it is for. The channels here are stand-ins that answer for their guild
/// and nothing else.</summary>
public class DeliveryChannelsTests
{
    private const long GuildId = 700;

    [Fact]
    public async Task A_channel_of_the_deliverys_guild_is_found()
    {
        var channel = Channel<ITextChannel>(guildId: 700);

        var found = await Channels(channel).FindAsync(ChannelId, GuildId);

        Assert.Same(channel, found.Channel);
        Assert.False(found.OutsideGuild);
    }

    [Fact]
    public async Task A_thread_of_the_deliverys_guild_is_found()
    {
        var thread = Channel<IThreadChannel>(guildId: 700);

        Assert.Same(thread, (await Channels(thread).FindAsync(ChannelId, GuildId)).Channel);
    }

    [Fact]
    public async Task A_channel_of_another_guild_is_not_handed_out()
    {
        var found = await Channels(Channel<ITextChannel>(guildId: 701)).FindAsync(ChannelId, GuildId);

        Assert.Null(found.Channel);
        Assert.True(found.OutsideGuild);
    }

    [Fact]
    public async Task A_channel_of_no_guild_is_not_handed_out_to_a_delivery_that_has_one()
    {
        var found = await Channels(Channel<IDMChannel>(guildId: null)).FindAsync(ChannelId, GuildId);

        Assert.Null(found.Channel);
        Assert.True(found.OutsideGuild);
    }

    [Fact]
    public async Task A_channel_that_is_not_there_is_missing_and_not_outside()
    {
        var found = await Channels(null).FindAsync(ChannelId, GuildId);

        Assert.Null(found.Channel);
        Assert.False(found.OutsideGuild);
    }

    [Fact]
    public async Task A_channel_that_takes_no_messages_is_missing_and_not_outside()
    {
        // A category belongs to a guild and cannot be posted in — whichever guild it is.
        foreach (var guildId in new ulong[] { 700, 701 })
        {
            var found = await Channels(Channel<ICategoryChannel>(guildId)).FindAsync(ChannelId, GuildId);

            Assert.Null(found.Channel);
            Assert.False(found.OutsideGuild);
        }
    }

    [Theory]
    [InlineData(700UL)]
    [InlineData(701UL)]
    public async Task A_delivery_that_names_no_guild_is_given_the_channel(ulong guildId)
    {
        var channel = Channel<ITextChannel>(guildId);

        var found = await Channels(channel).FindAsync(ChannelId, guildId: null);

        Assert.Same(channel, found.Channel);
        Assert.False(found.OutsideGuild);
    }

    [Fact]
    public async Task The_channel_is_read_by_the_id_that_is_asked_for()
    {
        ulong? asked = null;
        var channels = new DeliveryChannels(
            id =>
            {
                asked = id;
                return ValueTask.FromResult<IChannel?>(null);
            },
            new CollectingLogger());

        await channels.FindAsync(4242, GuildId);

        Assert.Equal(4242UL, asked);
    }

    [Theory]
    [InlineData(700UL, 700L, true)]
    [InlineData(701UL, 700L, false)]
    [InlineData(null, 700L, false)]
    [InlineData(700UL, null, true)]
    [InlineData(null, null, true)]
    [InlineData(700UL, -700L, false)]
    [InlineData(ulong.MaxValue, -1L, false)] // no guild has a negative id, and none matches one
    public void A_channel_is_in_a_deliverys_guild_when_the_two_are_the_same_guild(
        ulong? channelGuildId, long? guildId, bool expected) =>
        Assert.Equal(expected, DeliveryChannels.IsInGuild(channelGuildId, guildId));

    [Fact]
    public async Task A_delivery_is_posted_in_the_channel_it_names_when_that_is_in_its_guild()
    {
        var channel = Channel<ITextChannel>(guildId: 700);
        var log = new CollectingLogger();
        ulong? asked = null;
        var channels = new DeliveryChannels(
            id =>
            {
                asked = id;
                return ValueTask.FromResult<IChannel?>(channel);
            },
            log);

        var found = await channels.ForPostAsync(Reminder(), GuildId);

        Assert.Same(channel, found);
        Assert.Equal((ulong)ChannelId, asked);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_delivery_whose_channel_lies_outside_its_guild_is_dropped_with_a_warning()
    {
        var log = new CollectingLogger();
        var delivery = Reminder();

        var found = await Channels(Channel<ITextChannel>(guildId: 701), log).ForPostAsync(delivery, GuildId);

        Assert.Null(found);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            $"Discarding delivery {delivery.Id} (Reminder): channel {ChannelId} is not in guild {GuildId}, which the delivery is for.",
            entry.Message);
    }

    [Fact]
    public async Task A_delivery_whose_channel_is_not_there_is_left_to_be_tried_again()
    {
        var log = new CollectingLogger();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Channels(null, log).ForPostAsync(Reminder(), GuildId));

        Assert.Equal($"Channel {ChannelId} not found or not a message channel.", failure.Message);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_delivery_that_names_no_guild_is_posted_in_its_channel()
    {
        var channel = Channel<ITextChannel>(guildId: 701);
        var log = new CollectingLogger();

        Assert.Same(channel, await Channels(channel, log).ForPostAsync(Reminder(), guildId: null));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task An_embed_is_re_rendered_in_a_channel_of_its_guild()
    {
        var channel = Channel<ITextChannel>(guildId: 700);
        var log = new CollectingLogger();
        ulong? asked = null;
        var channels = new DeliveryChannels(
            id =>
            {
                asked = id;
                return ValueTask.FromResult<IChannel?>(channel);
            },
            log);

        // The channel is the one the embed was posted in, which the caller hands over.
        var found = await channels.ForRerenderAsync(Reminder(), channelId: 4242, GuildId);

        Assert.Same(channel, found);
        Assert.Equal(4242UL, asked);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task An_embed_in_a_channel_outside_its_guild_is_not_re_rendered_and_that_is_logged()
    {
        var log = new CollectingLogger();
        var delivery = Delivery(DeliveryType.SyncEventMessage, new SyncEventMessagePayload(Guid.NewGuid()));

        var found = await Channels(Channel<ITextChannel>(guildId: 701), log).ForRerenderAsync(delivery, 4242, GuildId);

        Assert.Null(found);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            $"Discarding delivery {delivery.Id} (SyncEventMessage): channel 4242 is not in guild {GuildId}, which the delivery is for.",
            entry.Message);
    }

    [Fact]
    public async Task An_embed_whose_channel_is_gone_is_not_re_rendered_and_nothing_is_said()
    {
        var log = new CollectingLogger();

        Assert.Null(await Channels(null, log).ForRerenderAsync(Reminder(), 4242, GuildId));
        Assert.Empty(log.Entries);
    }

    private static DeliveryDto Reminder() => Delivery(DeliveryType.Reminder, new ReminderPayload(55, "Bring the dice", GuildId));
}
