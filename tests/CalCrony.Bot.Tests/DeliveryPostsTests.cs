using CalCrony.Contracts;
using Discord;
using Microsoft.Extensions.Logging;
using static CalCrony.Bot.Tests.DeliveryStandIns;

namespace CalCrony.Bot.Tests;

/// <summary>What the bot posts for a delivery and whom the post may ping: the member a reminder
/// or a promotion is for, what a notification's mention field names, and nobody for a start
/// announcement — whatever is written in a title, a message or a reminder. And the posting
/// itself: in the delivery's channel when that is in the delivery's guild, with those mentions.
/// Where a channel may be handed out is <see cref="DeliveryChannelsTests"/>.</summary>
public class DeliveryPostsTests
{
    private const long GuildId = 700;
    private const string TextWithMentions = "@everyone @here <@&41> <@42> <@!43>";

    [Fact]
    public void A_reminder_pings_the_member_it_is_for_and_nobody_else()
    {
        var post = DeliveryPosts.Reminder(new ReminderPayload(55, TextWithMentions));

        Assert.Equal($"⏰ <@55> Reminder: {TextWithMentions}", post.Text);
        AssertAllows(post.Mentions, users: [55]);
    }

    [Fact]
    public void A_waitlist_promotion_pings_the_promoted_member_and_nobody_else()
    {
        var post = DeliveryPosts.WaitlistPromotion(
            new WaitlistPromotionPayload(Guid.NewGuid(), 55, TextWithMentions, 1800000000, "✅", "@everyone"));

        Assert.Equal(
            $"🎟️ <@55> A spot opened up — you're now ✅ **@everyone** for **{TextWithMentions}** (<t:1800000000:F>).",
            post.Text);
        AssertAllows(post.Mentions, users: [55]);
    }

    [Fact]
    public void A_start_announcement_pings_nobody()
    {
        var post = DeliveryPosts.EventStart(new EventStartPayload(Guid.NewGuid(), TextWithMentions, 1800000000, null));

        Assert.Equal($"🎉 **{TextWithMentions}** is starting now!", post.Text);
        AssertAllows(post.Mentions);
    }

    [Fact]
    public void A_notification_shows_its_mention_title_time_and_message()
    {
        var post = DeliveryPosts.Notification(Payload("<@&41>", message: "Bring snacks", title: "Game night"));

        Assert.Equal("🔔 <@&41> **Game night** starts <t:1800000000:R> (<t:1800000000:F>)\nBring snacks", post.Text);
    }

    [Fact]
    public void A_notification_without_mention_or_message_is_the_title_and_the_time()
    {
        var post = DeliveryPosts.Notification(Payload(null, message: " ", title: "Game night"));

        Assert.Equal("🔔 **Game night** starts <t:1800000000:R> (<t:1800000000:F>)", post.Text);
        AssertAllows(post.Mentions);
    }

    [Fact]
    public void A_notification_pings_the_role_in_its_mention_field_and_nothing_written_in_its_text()
    {
        // The field's role is not among the mentions written in the title and the message.
        var post = DeliveryPosts.Notification(Payload("<@&77>", message: TextWithMentions, title: TextWithMentions));

        Assert.Contains(TextWithMentions, post.Text);
        AssertAllows(post.Mentions, roles: [77]);
    }

    [Theory]
    [InlineData("<@42>")]
    [InlineData("<@!42>")]
    [InlineData("  <@42>  ")]
    public void A_mention_field_naming_a_member_allows_that_member(string field) =>
        AssertAllows(DeliveryPosts.MentionField(field), users: [42]);

    [Theory]
    [InlineData("<@&41>", 41UL)]
    [InlineData(" <@&41>\n", 41UL)]
    [InlineData("<@&18446744073709551615>", ulong.MaxValue)] // the largest id there is
    public void A_mention_field_naming_a_role_allows_that_role(string field, ulong roleId) =>
        AssertAllows(DeliveryPosts.MentionField(field), roles: [roleId]);

    [Theory]
    [InlineData("@everyone")]
    [InlineData(" @everyone ")]
    public void A_mention_field_of_everyone_allows_everyone(string field) =>
        AssertAllows(DeliveryPosts.MentionField(field), everyone: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@here")] // not something /notify's mention option can store
    [InlineData("<@&41> <@&42>")] // two mentions
    [InlineData("<@&41> hello")] // a mention and more
    [InlineData("hello <@42>")]
    [InlineData("@everyone <@&41>")]
    [InlineData("@everyone @here")]
    [InlineData("@Everyone")]
    [InlineData("everyone")]
    [InlineData("<@&>")]
    [InlineData("<@&41")]
    [InlineData("<@&4 1>")]
    [InlineData("<@&-41>")]
    [InlineData("<@&٤١>")] // digits of another script
    [InlineData("<@&18446744073709551616>")] // one past the largest id
    [InlineData("<@&184467440737095516150>")] // too many digits
    [InlineData("<#41>")] // a channel
    [InlineData("<@&41>\n<@&42>")]
    public void A_mention_field_holding_anything_else_allows_nothing(string? field) =>
        AssertAllows(DeliveryPosts.MentionField(field));

    [Fact]
    public async Task A_post_is_sent_with_its_text_and_the_mentions_it_allows()
    {
        var channel = Channel<ITextChannel>(guildId: 700);
        var post = DeliveryPosts.Reminder(new ReminderPayload(55, TextWithMentions));

        await DeliveryPosts.SendAsync(channel, post);

        var sent = Assert.Single(Sent(channel));
        Assert.Equal(post.Text, sent.Text);
        Assert.Same(post.Mentions, sent.Mentions);
    }

    [Fact]
    public void Each_kind_of_text_delivery_gets_its_own_post_and_names_its_guild()
    {
        var eventId = Guid.NewGuid();
        var reminder = new ReminderPayload(55, "Bring the dice", GuildId);
        var notification = Payload("<@&41>", message: "Bring snacks", title: "Game night");
        var promotion = new WaitlistPromotionPayload(eventId, 55, "Game night", 1800000000, "✅", "Going", GuildId);
        var start = new EventStartPayload(eventId, "Game night", 1800000000, null, GuildId);

        AssertSame((DeliveryPosts.Reminder(reminder), GuildId), DeliveryPosts.For(Delivery(DeliveryType.Reminder, reminder)));
        AssertSame(
            (DeliveryPosts.Notification(notification), GuildId),
            DeliveryPosts.For(Delivery(DeliveryType.EventNotification, notification)));
        AssertSame(
            (DeliveryPosts.WaitlistPromotion(promotion), GuildId),
            DeliveryPosts.For(Delivery(DeliveryType.WaitlistPromotion, promotion)));
        AssertSame((DeliveryPosts.EventStart(start), GuildId), DeliveryPosts.For(Delivery(DeliveryType.EventStart, start)));

        static void AssertSame((DeliveryPosts.Post Post, long? GuildId) expected, (DeliveryPosts.Post Post, long? GuildId) actual)
        {
            Assert.Equal(expected.Post.Text, actual.Post.Text);
            AssertAllows(
                actual.Post.Mentions,
                users: [.. expected.Post.Mentions.UserIds],
                roles: [.. expected.Post.Mentions.RoleIds],
                everyone: expected.Post.Mentions.AllowedTypes == AllowedMentionTypes.Everyone);
            Assert.Equal(expected.GuildId, actual.GuildId);
        }
    }

    [Fact]
    public void A_text_delivery_whose_payload_names_no_guild_has_none()
    {
        var eventId = Guid.NewGuid();

        Assert.Null(DeliveryPosts.For(Delivery(DeliveryType.Reminder, new ReminderPayload(55, "x"))).GuildId);
        Assert.Null(DeliveryPosts.For(Delivery(
            DeliveryType.EventNotification, new EventNotificationPayload(eventId, "t", 1800000000, null, null))).GuildId);
        Assert.Null(DeliveryPosts.For(Delivery(
            DeliveryType.WaitlistPromotion, new WaitlistPromotionPayload(eventId, 55, "t", 1800000000, "✅", "Going"))).GuildId);
        Assert.Null(DeliveryPosts.For(Delivery(DeliveryType.EventStart, new EventStartPayload(eventId, "t", 1800000000, null))).GuildId);
    }

    [Fact]
    public void A_delivery_of_another_kind_is_no_text_post()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => DeliveryPosts.For(Delivery(DeliveryType.SyncEventMessage, new SyncEventMessagePayload(Guid.NewGuid()))));

        Assert.Equal("Unknown delivery type SyncEventMessage.", failure.Message);
    }

    [Fact]
    public async Task A_delivery_is_posted_in_its_channel_with_its_text_and_its_mentions()
    {
        var channel = Channel<ITextChannel>(guildId: 700);
        var log = new CollectingLogger();
        var payload = new ReminderPayload(55, TextWithMentions, GuildId);

        var posted = await DeliveryPosts.PostAsync(Delivery(DeliveryType.Reminder, payload), Channels(channel, log));

        Assert.True(posted);
        var sent = Assert.Single(Sent(channel));
        Assert.Equal($"⏰ <@55> Reminder: {TextWithMentions}", sent.Text);
        AssertAllows(sent.Mentions!, users: [55]);
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData(DeliveryType.Reminder)]
    [InlineData(DeliveryType.EventNotification)]
    [InlineData(DeliveryType.WaitlistPromotion)]
    [InlineData(DeliveryType.EventStart)]
    public async Task A_delivery_of_each_kind_is_posted_in_a_channel_of_its_guild_and_dropped_in_any_other(DeliveryType type)
    {
        var inside = Channel<ITextChannel>(guildId: 700);
        var outside = Channel<ITextChannel>(guildId: 701);
        var log = new CollectingLogger();
        var delivery = OfKind(type);

        Assert.True(await DeliveryPosts.PostAsync(delivery, Channels(inside, log)));
        Assert.Single(Sent(inside));
        Assert.Empty(log.Entries);

        Assert.False(await DeliveryPosts.PostAsync(delivery, Channels(outside, log)));
        Assert.Empty(Sent(outside));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains($"Discarding delivery {delivery.Id} ({type})", entry.Message);
    }

    [Fact]
    public async Task A_delivery_whose_channel_is_not_there_is_not_posted_and_is_left_to_be_tried_again()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DeliveryPosts.PostAsync(OfKind(DeliveryType.Reminder), Channels(null)));
    }

    private static DeliveryDto OfKind(DeliveryType type)
    {
        var eventId = Guid.NewGuid();
        return type switch
        {
            DeliveryType.Reminder => Delivery(type, new ReminderPayload(55, "Bring the dice", GuildId)),
            DeliveryType.EventNotification => Delivery(type, Payload("<@&41>")),
            DeliveryType.WaitlistPromotion => Delivery(
                type, new WaitlistPromotionPayload(eventId, 55, "Game night", 1800000000, "✅", "Going", GuildId)),
            DeliveryType.EventStart => Delivery(type, new EventStartPayload(eventId, "Game night", 1800000000, null, GuildId)),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private static EventNotificationPayload Payload(string? mentions, string? message = null, string title = "Title") =>
        new(Guid.NewGuid(), title, 1800000000, message, mentions, GuildId);

    /// <summary>Asserts exactly what a post lets Discord ping: these members, these roles, and
    /// everyone or not. The kinds of mention Discord may read from the text are said outright —
    /// none, or everyone — never left unsaid.</summary>
    private static void AssertAllows(
        AllowedMentions mentions, ulong[]? users = null, ulong[]? roles = null, bool everyone = false)
    {
        Assert.Equal(users ?? [], mentions.UserIds);
        Assert.Equal(roles ?? [], mentions.RoleIds);
        Assert.Equal(everyone ? AllowedMentionTypes.Everyone : AllowedMentionTypes.None, mentions.AllowedTypes);
    }
}
