using System.Text;
using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>Text of an opt-in DM reminder. Pure and static so the wording is directly testable.
/// Every DM ends with how to turn them off — the recipient asked for these, and must always be one
/// command away from stopping them.</summary>
public static class DmReminderText
{
    /// <summary>Renders the DM for a reminder or start announcement.</summary>
    /// <param name="payload">The delivery payload.</param>
    /// <returns>The message text.</returns>
    public static string Format(DmEventReminderPayload payload)
    {
        var where = string.IsNullOrWhiteSpace(payload.GuildName) ? "" : $" in **{payload.GuildName}**";
        var text = new StringBuilder(payload.IsStart
            ? $"🎉 **{payload.Title}**{where} is starting now!"
            : $"🔔 **{payload.Title}**{where} starts <t:{payload.StartsAtUnix}:R> (<t:{payload.StartsAtUnix}:F>).");

        if (!string.IsNullOrWhiteSpace(payload.Message))
        {
            text.Append('\n').Append(payload.Message);
        }

        if (payload.MessageId is { } messageId)
        {
            text.Append($"\n💬 https://discord.com/channels/{payload.GuildId}/{payload.ChannelId}/{messageId}");
        }

        text.Append("\n-# You get these because DM reminders are on for events you're attending. Turn them off with `/settings dm-reminders enabled:false`.");
        return text.ToString();
    }
}
