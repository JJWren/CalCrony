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
public sealed class DeliveryPollerService(
    DiscordSocketClient client,
    CalCronyApiClient api,
    IConfiguration configuration,
    ILogger<DeliveryPollerService> logger) : BackgroundService
{
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

    private async Task PostAsync(DeliveryDto delivery)
    {
        if (await client.GetChannelAsync((ulong)delivery.ChannelId) is not IMessageChannel channel)
        {
            throw new InvalidOperationException($"Channel {delivery.ChannelId} not found or not a message channel.");
        }

        var text = delivery.Type switch
        {
            DeliveryType.Reminder => FormatReminder(delivery.PayloadJson),
            DeliveryType.EventNotification => FormatNotification(delivery.PayloadJson),
            DeliveryType.EventStart => FormatEventStart(delivery.PayloadJson),
            _ => throw new InvalidOperationException($"Unknown delivery type {delivery.Type}."),
        };

        await channel.SendMessageAsync(text);
    }

    private static string FormatReminder(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<ReminderPayload>(payloadJson)!;
        return $"⏰ <@{payload.UserId}> Reminder: {payload.Text}";
    }

    private static string FormatNotification(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<EventNotificationPayload>(payloadJson)!;
        var mentions = string.IsNullOrWhiteSpace(payload.Mentions) ? "" : $"{payload.Mentions} ";
        var extra = string.IsNullOrWhiteSpace(payload.Message) ? "" : $"\n{payload.Message}";
        return $"🔔 {mentions}**{payload.Title}** starts <t:{payload.StartsAtUnix}:R> (<t:{payload.StartsAtUnix}:F>){extra}";
    }

    private static string FormatEventStart(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<EventStartPayload>(payloadJson)!;
        return $"🎉 **{payload.Title}** is starting now!";
    }
}
