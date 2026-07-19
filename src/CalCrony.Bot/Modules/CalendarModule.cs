using CalCrony.Bot.Api;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

// Deliberately no [RequireContext(ContextType.Guild)] — connecting a personal calendar is
// per-user and guild-independent, so this should work from a DM with the bot too.
/// <summary>/calendar — link, inspect, or unlink an external calendar (DM-capable).</summary>
/// <param name="api">The CalCrony API client.</param>
[Group("calendar", "Connect and manage your external calendar")]
public class CalendarModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Starts the OAuth link and DMs/replies with the consent URL.</summary>
    [SlashCommand("connect", "Connect your Google Calendar so others can check your availability")]
    public async Task ConnectAsync()
    {
        await DeferAsync(ephemeral: true);

        var result = await api.CreateCalendarLinkTokenAsync((long)Context.User.Id);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            "📅 Connect your Google Calendar:\n" +
            $"```{result.Value.StartUrl}```\n" +
            "This link expires in 10 minutes and works once. Only free/busy blocks are ever read — never event titles or details.",
            ephemeral: true);
    }

    /// <summary>Shows whether a calendar is linked and when it last refreshed.</summary>
    [SlashCommand("status", "Show whether your Google Calendar is connected")]
    public async Task StatusAsync()
    {
        await DeferAsync(ephemeral: true);

        var result = await api.GetCalendarStatusAsync((long)Context.User.Id);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            result.Value.Connected
                ? $"🟢 Connected to Google Calendar since <t:{result.Value.ConnectedAtUtc!.Value.ToUnixTimeSeconds()}:D>."
                : "⚪ Not connected. Run `/calendar connect` to link your Google Calendar.",
            ephemeral: true);
    }

    /// <summary>Unlinks the calendar after best-effort provider revocation.</summary>
    [SlashCommand("disconnect", "Disconnect your Google Calendar")]
    public async Task DisconnectAsync()
    {
        await DeferAsync(ephemeral: true);

        var result = await api.DisconnectCalendarAsync((long)Context.User.Id);
        await FollowupAsync(
            result.Success ? "🔌 Disconnected your Google Calendar." : $"❌ {result.Error}",
            ephemeral: true);
    }
}
