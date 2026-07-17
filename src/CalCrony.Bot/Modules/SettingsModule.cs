using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

[RequireContext(ContextType.Guild)]
[Group("settings", "View and change CalCrony settings")]
public class SettingsModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("view", "Show server and personal settings")]
    public async Task ViewAsync()
    {
        await DeferAsync(ephemeral: true);

        var guild = await api.GetGuildSettingsAsync((long)Context.Guild.Id);
        var user = await api.GetUserSettingsAsync((long)Context.User.Id);
        if (!guild.Success || !user.Success)
        {
            await FollowupAsync($"❌ {guild.Error ?? user.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"**Server timezone:** {guild.Value!.TimeZone}\n" +
            $"**Your timezone:** {user.Value!.TimeZone ?? "(not set — server timezone is used)"}\n" +
            $"**Your DM confirmations:** {(user.Value.DmConfirmations ? "on" : "off")}",
            ephemeral: true);
    }

    [SlashCommand("timezone", "Set your personal timezone (IANA id, e.g. America/Chicago)")]
    public async Task SetTimezoneAsync([Summary("timezone", "IANA timezone id")] string timezone)
    {
        await DeferAsync(ephemeral: true);

        var current = await api.GetUserSettingsAsync((long)Context.User.Id);
        var result = await api.PutUserSettingsAsync(
            (long)Context.User.Id,
            new UserSettingsDto(timezone, current.Value?.DmConfirmations ?? true));
        await FollowupAsync(
            result.Success
                ? $"🌍 Your timezone is now **{result.Value!.TimeZone}**."
                : $"❌ {result.Error}",
            ephemeral: true);
    }

    [SlashCommand("server-timezone", "Set the server's default timezone (managers only)")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SetServerTimezoneAsync([Summary("timezone", "IANA timezone id")] string timezone)
    {
        await DeferAsync(ephemeral: true);

        var current = await api.GetGuildSettingsAsync((long)Context.Guild.Id);
        var result = await api.PutGuildSettingsAsync(
            (long)Context.Guild.Id,
            new GuildSettingsDto(timezone, current.Value?.DefaultChannelId));
        await FollowupAsync(
            result.Success
                ? $"🌍 Server timezone is now **{result.Value!.TimeZone}**."
                : $"❌ {result.Error}",
            ephemeral: true);
    }
}
