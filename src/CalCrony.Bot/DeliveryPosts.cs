using System.Text.Json;
using System.Text.RegularExpressions;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

/// <summary>The text posts the bot makes in a server's channel for a delivery, and whom each may
/// ping. The text holds what members wrote — a title, a message, a reminder — so every post
/// names the mentions it allows, and a mention written into such text is shown and pings nobody.</summary>
public static partial class DeliveryPosts
{
    /// <summary>A post: its text, and the mentions in it that may ping.</summary>
    /// <param name="Text">The message text.</param>
    /// <param name="Mentions">The mentions Discord is told to honour.</param>
    public sealed record Post(string Text, AllowedMentions Mentions);

    /// <summary>Posts a delivery that is a text post: in the channel it names, when that is a
    /// channel of the guild the delivery is for, and with the mentions the post allows.</summary>
    /// <param name="delivery">The outbox row.</param>
    /// <param name="channels">Finds the channel a delivery may be posted in.</param>
    /// <returns>True when it is posted; false when the delivery is dropped, its channel lying
    /// outside its guild.</returns>
    /// <exception cref="InvalidOperationException">When the delivery is of no kind that is a text
    /// post, or its channel is not there.</exception>
    public static async Task<bool> PostAsync(DeliveryDto delivery, DeliveryChannels channels)
    {
        var (post, guildId) = For(delivery);
        if (await channels.ForPostAsync(delivery, guildId) is not { } channel)
        {
            return false;
        }

        await SendAsync(channel, post);
        return true;
    }

    /// <summary>The post for a delivery, and the guild the delivery is for: a reminder, a
    /// pre-event notification, a waitlist promotion or a start announcement.</summary>
    /// <param name="delivery">The outbox row.</param>
    /// <returns>The post, and the guild its payload names, if it names one.</returns>
    /// <exception cref="InvalidOperationException">When the delivery is of another kind.</exception>
    public static (Post Post, long? GuildId) For(DeliveryDto delivery)
    {
        switch (delivery.Type)
        {
            case DeliveryType.Reminder:
                var reminder = JsonSerializer.Deserialize<ReminderPayload>(delivery.PayloadJson)!;
                return (Reminder(reminder), reminder.GuildId);
            case DeliveryType.EventNotification:
                var notification = JsonSerializer.Deserialize<EventNotificationPayload>(delivery.PayloadJson)!;
                return (Notification(notification), notification.GuildId);
            case DeliveryType.WaitlistPromotion:
                var promotion = JsonSerializer.Deserialize<WaitlistPromotionPayload>(delivery.PayloadJson)!;
                return (WaitlistPromotion(promotion), promotion.GuildId);
            case DeliveryType.EventStart:
                var start = JsonSerializer.Deserialize<EventStartPayload>(delivery.PayloadJson)!;
                return (EventStart(start), start.GuildId);
            default:
                throw new InvalidOperationException($"Unknown delivery type {delivery.Type}.");
        }
    }

    /// <summary>A one-off reminder: pings the member it is for.</summary>
    /// <param name="payload">The reminder payload.</param>
    /// <returns>The post.</returns>
    public static Post Reminder(ReminderPayload payload) => new(
        $"⏰ <@{payload.UserId}> Reminder: {payload.Text}",
        OnlyMember((ulong)payload.UserId));

    /// <summary>A waitlist promotion, posted in the event's channel (a DM could be blocked; the
    /// channel ping is reliable and visible to organizers too): pings the promoted member.</summary>
    /// <param name="payload">The promotion payload.</param>
    /// <returns>The post.</returns>
    public static Post WaitlistPromotion(WaitlistPromotionPayload payload) => new(
        $"🎟️ <@{payload.UserId}> A spot opened up — you're now {payload.OptionEmote} " +
        $"**{payload.OptionLabel}** for **{payload.Title}** (<t:{payload.StartsAtUnix}:F>).",
        OnlyMember((ulong)payload.UserId));

    /// <summary>The event-start announcement: pings nobody.</summary>
    /// <param name="payload">The start payload.</param>
    /// <returns>The post.</returns>
    public static Post EventStart(EventStartPayload payload) => new(
        $"🎉 **{payload.Title}** is starting now!",
        Nobody());

    /// <summary>A pre-event notification: pings what its mention field names (see
    /// <see cref="MentionField"/>).</summary>
    /// <param name="payload">The notification payload.</param>
    /// <returns>The post.</returns>
    public static Post Notification(EventNotificationPayload payload)
    {
        var mentions = string.IsNullOrWhiteSpace(payload.Mentions) ? "" : $"{payload.Mentions} ";
        var extra = string.IsNullOrWhiteSpace(payload.Message) ? "" : $"\n{payload.Message}";
        return new Post(
            $"🔔 {mentions}**{payload.Title}** starts <t:{payload.StartsAtUnix}:R> (<t:{payload.StartsAtUnix}:F>){extra}",
            MentionField(payload.Mentions));
    }

    /// <summary>The mentions a notification's mention field allows. The field is what /notify's
    /// mention option stores: one role, one member, or <c>@everyone</c>. It allows that one
    /// mention, and a field holding anything else allows none. Each answer says outright which
    /// kinds of mention Discord may read from the text: none, or for <c>@everyone</c> that one
    /// kind, which also covers an <c>@everyone</c> or <c>@here</c> written elsewhere in the post
    /// and so reaches nobody the field does not already name.</summary>
    /// <param name="mentionField">The stored mention text, if any.</param>
    /// <returns>The mentions to allow.</returns>
    public static AllowedMentions MentionField(string? mentionField)
    {
        var text = mentionField?.Trim() ?? "";
        if (text is "@everyone")
        {
            return new AllowedMentions(AllowedMentionTypes.Everyone);
        }

        if (RoleMention().Match(text) is { Success: true } role && ulong.TryParse(role.Groups[1].ValueSpan, out var roleId))
        {
            return new AllowedMentions(AllowedMentionTypes.None) { RoleIds = [roleId] };
        }

        if (MemberMention().Match(text) is { Success: true } member && ulong.TryParse(member.Groups[1].ValueSpan, out var userId))
        {
            return OnlyMember(userId);
        }

        return Nobody();
    }

    /// <summary>Sends a post with the mentions it allows.</summary>
    /// <param name="channel">The channel to post in.</param>
    /// <param name="post">The post.</param>
    /// <returns>The sent message.</returns>
    public static Task<IUserMessage> SendAsync(IMessageChannel channel, Post post) =>
        channel.SendMessageAsync(post.Text, allowedMentions: post.Mentions);

    /// <summary>No mention may ping: an empty list of kinds, which is the form Discord documents
    /// for it.</summary>
    private static AllowedMentions Nobody() => new(AllowedMentionTypes.None);

    /// <summary>One member's mention may ping, and no kind of mention is read from the text.</summary>
    private static AllowedMentions OnlyMember(ulong userId) => new(AllowedMentionTypes.None) { UserIds = [userId] };

    /// <summary>A field that is one role mention and nothing else, as Discord writes a role.</summary>
    [GeneratedRegex(@"\A<@&([0-9]{1,20})>\z")]
    private static partial Regex RoleMention();

    /// <summary>A field that is one member mention and nothing else, as Discord writes a member,
    /// with or without the older <c>!</c>.</summary>
    [GeneratedRegex(@"\A<@!?([0-9]{1,20})>\z")]
    private static partial Regex MemberMention();
}
