using System.Text.Json;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.WebSocket;

namespace CalCrony.Bot;

/// <summary>
/// Polls the API outbox for due deliveries (reminders, event notifications, start pings),
/// posts them to Discord, and acks each row only after the post succeeds.
/// </summary>
/// <param name="client">The Discord socket client.</param>
/// <param name="api">The CalCrony API client.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
/// <param name="mirror">The native scheduled-event mirror.</param>
/// <param name="roles">The attendee-role manager.</param>
/// <param name="threads">The event-thread manager.</param>
public sealed class DeliveryPollerService(
    DiscordSocketClient client,
    CalCronyApiClient api,
    IConfiguration configuration,
    ILogger<DeliveryPollerService> logger,
    NativeEventMirror mirror,
    AttendeeRoleManager roles,
    EventThreadManager threads) : BackgroundService
{
    /// <summary>Polls the outbox (~15s), posts each due delivery to Discord, and acks only after success.</summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Api:PollSeconds", 15));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (client.ConnectionState != ConnectionState.Connected)
            {
                continue;
            }

            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delivery poll failed; will retry next tick.");
            }
        }
    }

    /// <summary>Drains the current pending batch, isolating per-delivery failures.</summary>
    /// <param name="ct">Cancels the request.</param>
    private async Task DrainAsync(CancellationToken ct)
    {
        var pending = await api.GetPendingDeliveriesAsync(ct: ct);
        if (!pending.Success || pending.Value is null || pending.Value.Count == 0)
        {
            return;
        }

        foreach (var delivery in pending.Value)
        {
            try
            {
                await PostAsync(delivery);
                await api.AckDeliveryAsync(delivery.Id, ct);
            }
            catch (Exception ex)
            {
                // Not acked — the API will re-serve it until MaxAttempts.
                logger.LogWarning(ex, "Failed to post delivery {DeliveryId} to channel {ChannelId}.", delivery.Id, delivery.ChannelId);
            }
        }
    }

    /// <summary>Dispatches one delivery to its per-type handler.</summary>
    /// <param name="delivery">The outbox row to post.</param>
    /// <exception cref="InvalidOperationException">When the channel is missing, the type is unknown, or a message id fails to record — caught by DrainAsync, which leaves the delivery pending for retry.</exception>
    private async Task PostAsync(DeliveryDto delivery)
    {
        if (delivery.Type == DeliveryType.SyncEventMessage)
        {
            await SyncEventMessageAsync(delivery);
            return;
        }

        if (delivery.Type == DeliveryType.PostEventMessage)
        {
            await PostEventMessageAsync(delivery);
            return;
        }

        if (delivery.Type == DeliveryType.DeleteEventMessage)
        {
            await DeleteEventMessageAsync(delivery);
            return;
        }

        if (delivery.Type == DeliveryType.SyncPollMessage)
        {
            await SyncPollMessageAsync(delivery);
            return;
        }

        if (delivery.Type == DeliveryType.PostPollMessage)
        {
            await PostPollMessageAsync(delivery);
            return;
        }

        if (delivery.Type == DeliveryType.DeletePollMessage)
        {
            await DeletePollMessageAsync(delivery);
            return;
        }

        if (delivery.Type == DeliveryType.EventStart)
        {
            await EventStartAsync(delivery);
            return;
        }

        if (delivery.Type == DeliveryType.CompleteNativeEvent)
        {
            var payload = JsonSerializer.Deserialize<CompleteNativeEventPayload>(delivery.PayloadJson)!;
            // Best-effort by design (the helper never throws), so this always acks.
            await mirror.TryCompleteAsync(payload.GuildId, payload.NativeEventId);
            return;
        }

        if (delivery.Type is DeliveryType.GrantAttendeeRole or DeliveryType.RevokeAttendeeRole)
        {
            // Best-effort by design (the manager never throws), so this always acks — a retry
            // could otherwise reorder a stale grant past a later revoke. That contract must
            // survive a malformed payload too, so the deserialize is guarded as well.
            AttendeeRolePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AttendeeRolePayload>(delivery.PayloadJson);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Discarding attendee-role delivery {DeliveryId} with a malformed payload.", delivery.Id);
                return;
            }

            if (payload is null)
            {
                return;
            }

            await (delivery.Type == DeliveryType.GrantAttendeeRole
                ? roles.TryGrantAsync(payload.GuildId, payload.RoleId, payload.UserId)
                : roles.TryRevokeAsync(payload.GuildId, payload.RoleId, payload.UserId));
            return;
        }

        if (delivery.Type == DeliveryType.AddThreadMember)
        {
            // Best-effort (the manager never throws), so this always acks; the deserialize is
            // guarded so a malformed payload can't break that contract.
            ThreadMemberPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ThreadMemberPayload>(delivery.PayloadJson);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Discarding thread-member delivery {DeliveryId} with a malformed payload.", delivery.Id);
                return;
            }

            if (payload is not null)
            {
                await threads.TryAddMemberAsync(payload.GuildId, payload.ThreadId, payload.UserId);
            }

            return;
        }

        if (delivery.Type == DeliveryType.ArchiveThread)
        {
            ArchiveThreadPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ArchiveThreadPayload>(delivery.PayloadJson);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Discarding thread-archive delivery {DeliveryId} with a malformed payload.", delivery.Id);
                return;
            }

            if (payload is not null)
            {
                await threads.TryArchiveAsync(payload.GuildId, payload.ThreadId);
            }

            return;
        }

        if (await client.GetChannelAsync((ulong)delivery.ChannelId) is not IMessageChannel channel)
        {
            throw new InvalidOperationException($"Channel {delivery.ChannelId} not found or not a message channel.");
        }

        var text = delivery.Type switch
        {
            DeliveryType.Reminder => FormatReminder(delivery.PayloadJson),
            DeliveryType.EventNotification => FormatNotification(delivery.PayloadJson),
            _ => throw new InvalidOperationException($"Unknown delivery type {delivery.Type}."),
        };

        await channel.SendMessageAsync(text);
    }

    /// <summary>A web-created event needs its Discord embed posted (mirrors what /create does):
    /// post to the delivery's channel, then record the message id with the API. Event already
    /// gone or already posted ⇒ done.</summary>
    /// <param name="delivery">The outbox row to post.</param>
    private async Task PostEventMessageAsync(DeliveryDto delivery)
    {
        var payload = JsonSerializer.Deserialize<PostEventMessagePayload>(delivery.PayloadJson)!;
        var result = await api.GetEventAsync(payload.EventId);
        if (!result.Success || result.Value is null || result.Value.MessageId is not null)
        {
            return;
        }

        if (await client.GetChannelAsync((ulong)delivery.ChannelId) is not IMessageChannel channel)
        {
            throw new InvalidOperationException($"Channel {delivery.ChannelId} not found or not a message channel.");
        }

        var ev = result.Value;
        var message = await channel.SendMessageAsync(
            embed: EventEmbedBuilder.Build(ev),
            components: EventEmbedBuilder.BuildComponents(ev));

        var recorded = await api.SetMessageAsync(ev.Id, new SetEventMessageRequest(delivery.ChannelId, (long)message.Id));
        if (!recorded.Success)
        {
            // Without a recorded MessageId, future syncs/deletes can't find the embed — and a
            // bare retry would re-post it. Compensate by removing this post, then let the
            // delivery retry cleanly.
            try
            {
                await message.DeleteAsync();
            }
            catch
            {
                // Best effort; the retry-time MessageId-not-null check has no help here, but a
                // stray embed beats a silent one that never syncs.
            }

            throw new InvalidOperationException($"Failed to record posted message id: {recorded.Error}");
        }

        // After the embed bookkeeping is safe: mirror to a native scheduled event and open the
        // discussion thread (both best-effort — neither failure blocks the delivery).
        await mirror.TryUpsertAsync(recorded.Value!);
        await threads.TryCreateAsync(recorded.Value!, message);
    }

    /// <summary>A web-deleted event's embed and native twin should disappear; the ids were
    /// captured before the row died. Best-effort — anything already gone counts as done.</summary>
    /// <param name="delivery">The outbox row to post.</param>
    private async Task DeleteEventMessageAsync(DeliveryDto delivery)
    {
        var payload = JsonSerializer.Deserialize<DeleteEventMessagePayload>(delivery.PayloadJson)!;
        if (payload.GuildId is long guildId)
        {
            await mirror.TryDeleteAsync(guildId, payload.NativeEventId);
        }

        if (payload.MessageId is not long messageId ||
            await client.GetChannelAsync((ulong)payload.ChannelId) is not IMessageChannel channel ||
            await channel.GetMessageAsync((ulong)messageId) is not IMessage message)
        {
            return;
        }

        await message.DeleteAsync();
    }

    /// <summary>A web action changed event data shown on the posted Discord embed — re-render it.
    /// Anything already gone (event deleted, message removed, channel missing) counts as done.</summary>
    /// <param name="delivery">The outbox row to post.</param>
    private async Task SyncEventMessageAsync(DeliveryDto delivery)
    {
        var payload = JsonSerializer.Deserialize<SyncEventMessagePayload>(delivery.PayloadJson)!;
        var result = await api.GetEventAsync(payload.EventId);
        if (!result.Success || result.Value is null)
        {
            return;
        }

        // Keep the native twin accurate even when the embed was hand-deleted.
        await mirror.TryUpsertAsync(result.Value);
        if (result.Value.MessageId is not long messageId)
        {
            return;
        }

        var ev = result.Value;
        if (await client.GetChannelAsync((ulong)ev.ChannelId) is not IMessageChannel channel ||
            await channel.GetMessageAsync((ulong)messageId) is not IUserMessage message)
        {
            return;
        }

        await message.ModifyAsync(m =>
        {
            m.Embed = EventEmbedBuilder.Build(ev);
            m.Components = EventEmbedBuilder.BuildComponents(ev);
        });
    }

    /// <summary>A web/scheduler action changed poll data shown on the posted embed — re-render.
    /// Anything already gone counts as done.</summary>
    /// <param name="delivery">The outbox row to post.</param>
    private async Task SyncPollMessageAsync(DeliveryDto delivery)
    {
        var payload = JsonSerializer.Deserialize<SyncPollMessagePayload>(delivery.PayloadJson)!;
        var result = await api.GetPollAsync(payload.PollId);
        if (!result.Success || result.Value is null || result.Value.MessageId is not long messageId)
        {
            return;
        }

        var poll = result.Value;
        if (await client.GetChannelAsync((ulong)poll.ChannelId) is not IMessageChannel channel ||
            await channel.GetMessageAsync((ulong)messageId) is not IUserMessage message)
        {
            return;
        }

        await message.ModifyAsync(m =>
        {
            m.Embed = PollEmbedBuilder.Build(poll);
            m.Components = PollEmbedBuilder.BuildComponents(poll);
        });
    }

    /// <summary>A web-created poll needs its embed posted; mirrors PostEventMessageAsync,
    /// including the compensating delete when recording the message id fails.</summary>
    /// <param name="delivery">The outbox row to post.</param>
    private async Task PostPollMessageAsync(DeliveryDto delivery)
    {
        var payload = JsonSerializer.Deserialize<PostPollMessagePayload>(delivery.PayloadJson)!;
        var result = await api.GetPollAsync(payload.PollId);
        if (!result.Success || result.Value is null || result.Value.MessageId is not null)
        {
            return;
        }

        if (await client.GetChannelAsync((ulong)delivery.ChannelId) is not IMessageChannel channel)
        {
            throw new InvalidOperationException($"Channel {delivery.ChannelId} not found or not a message channel.");
        }

        var poll = result.Value;
        var message = await channel.SendMessageAsync(
            embed: PollEmbedBuilder.Build(poll),
            components: PollEmbedBuilder.BuildComponents(poll));

        var recorded = await api.SetPollMessageAsync(poll.Id, new SetPollMessageRequest(delivery.ChannelId, (long)message.Id));
        if (!recorded.Success)
        {
            try
            {
                await message.DeleteAsync();
            }
            catch
            {
                // Best effort; a stray embed beats one that never syncs.
            }

            throw new InvalidOperationException($"Failed to record posted poll message id: {recorded.Error}");
        }
    }

    /// <summary>A web-deleted poll's embed should disappear; ids were captured pre-delete.</summary>
    /// <param name="delivery">The outbox row to post.</param>
    private async Task DeletePollMessageAsync(DeliveryDto delivery)
    {
        var payload = JsonSerializer.Deserialize<DeletePollMessagePayload>(delivery.PayloadJson)!;
        if (await client.GetChannelAsync((ulong)payload.ChannelId) is not IMessageChannel channel ||
            await channel.GetMessageAsync((ulong)payload.MessageId) is not IMessage message)
        {
            return;
        }

        await message.DeleteAsync();
    }

    /// <summary>Message text for a reminder delivery.</summary>
    /// <param name="payloadJson">The serialized delivery payload.</param>
    /// <returns>The message text.</returns>
    private static string FormatReminder(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<ReminderPayload>(payloadJson)!;
        return $"⏰ <@{payload.UserId}> Reminder: {payload.Text}";
    }

    /// <summary>Message text for a pre-event notification delivery.</summary>
    /// <param name="payloadJson">The serialized delivery payload.</param>
    /// <returns>The message text.</returns>
    private static string FormatNotification(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<EventNotificationPayload>(payloadJson)!;
        var mentions = string.IsNullOrWhiteSpace(payload.Mentions) ? "" : $"{payload.Mentions} ";
        var extra = string.IsNullOrWhiteSpace(payload.Message) ? "" : $"\n{payload.Message}";
        return $"🔔 {mentions}**{payload.Title}** starts <t:{payload.StartsAtUnix}:R> (<t:{payload.StartsAtUnix}:F>){extra}";
    }


    /// <summary>Posts the event-start announcement, then flips the mirrored native event to
    /// active (best-effort; the ping is the ack-gated action).</summary>
    /// <param name="delivery">The outbox row to post.</param>
    /// <exception cref="InvalidOperationException">When the channel is missing — caught by
    /// DrainAsync, which leaves the delivery pending for retry.</exception>
    private async Task EventStartAsync(DeliveryDto delivery)
    {
        if (await client.GetChannelAsync((ulong)delivery.ChannelId) is not IMessageChannel channel)
        {
            throw new InvalidOperationException($"Channel {delivery.ChannelId} not found or not a message channel.");
        }

        await channel.SendMessageAsync(FormatEventStart(delivery.PayloadJson));

        var payload = JsonSerializer.Deserialize<EventStartPayload>(delivery.PayloadJson)!;
        if (payload is { GuildId: long guildId, NativeEventId: long nativeId })
        {
            await mirror.TryStartAsync(guildId, nativeId);
        }
    }

    /// <summary>Message text for an event-start announcement.</summary>
    /// <param name="payloadJson">The serialized delivery payload.</param>
    /// <returns>The message text.</returns>
    private static string FormatEventStart(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<EventStartPayload>(payloadJson)!;
        return $"🎉 **{payload.Title}** is starting now!";
    }
}
