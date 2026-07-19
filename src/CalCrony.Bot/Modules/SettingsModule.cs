using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/settings — view and change server and personal preferences.</summary>
/// <param name="api">The CalCrony API client.</param>
[RequireContext(ContextType.Guild)]
[Group("settings", "View and change CalCrony settings")]
public class SettingsModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Shows the server timezone plus the caller's personal settings.</summary>
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
            $"**Your DM confirmations:** {(user.Value.DmConfirmations ? "on" : "off")}\n" +
            $"**Native Discord events:** {(guild.Value.MirrorNativeEvents ? "on" : "off")}",
            ephemeral: true);
    }

    /// <summary>Sets the caller's personal timezone (autocomplete-picked or typed).</summary>
    /// <param name="timezone">IANA timezone id (picked from autocomplete or typed).</param>
    [SlashCommand("timezone", "Set your personal timezone")]
    public async Task SetTimezoneAsync(
        [Summary("timezone", "Pick your timezone from the list (or type an IANA id)"), Autocomplete(typeof(TimeZoneAutocompleteHandler))] string timezone)
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

    /// <summary>Sets the server's timezone (managers only).</summary>
    /// <param name="timezone">IANA timezone id (picked from autocomplete or typed).</param>
    [SlashCommand("server-timezone", "Set the server's default timezone (managers only)")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SetServerTimezoneAsync(
        [Summary("timezone", "Pick the server's timezone from the list (or type an IANA id)"), Autocomplete(typeof(TimeZoneAutocompleteHandler))] string timezone)
    {
        await DeferAsync(ephemeral: true);

        var current = await api.GetGuildSettingsAsync((long)Context.Guild.Id);
        var result = await api.PutGuildSettingsAsync(
            (long)Context.Guild.Id,
            new GuildSettingsDto(timezone, current.Value?.DefaultChannelId, current.Value?.MirrorNativeEvents ?? false));
        await FollowupAsync(
            result.Success
                ? $"🌍 Server timezone is now **{result.Value!.TimeZone}**."
                : $"❌ {result.Error}",
            ephemeral: true);
    }

    /// <summary>Sets the default channel web-created embeds post to (managers only).</summary>
    /// <param name="channel">Target text channel (defaults to the current one).</param>
    [SlashCommand("default-channel", "Set the channel for web-created events and reminders (managers only)")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SetDefaultChannelAsync(
        [Summary("channel", "Where web-created events and reminders get posted")] ITextChannel channel)
    {
        await DeferAsync(ephemeral: true);

        var current = await api.GetGuildSettingsAsync((long)Context.Guild.Id);
        if (!current.Success || current.Value is null)
        {
            // Proceeding blind would overwrite the server timezone with the UTC fallback.
            await FollowupAsync($"❌ Couldn't load current settings: {current.Error}", ephemeral: true);
            return;
        }

        var result = await api.PutGuildSettingsAsync(
            (long)Context.Guild.Id,
            new GuildSettingsDto(current.Value.TimeZone, (long)channel.Id, current.Value.MirrorNativeEvents));
        await FollowupAsync(
            result.Success
                ? $"📌 Web-created events and reminders will post in {channel.Mention}."
                : $"❌ {result.Error}",
            ephemeral: true);
    }

    /// <summary>Turns native scheduled-event mirroring on or off (managers only). Enabling
    /// prechecks that the bot actually holds Manage Events so there are no silent failures.</summary>
    /// <param name="enabled">Whether new events should mirror into the server's Events tab.</param>
    [SlashCommand("native-events", "Mirror events into the server's Events tab (managers only)")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SetNativeEventsAsync(
        [Summary("enabled", "Turn mirroring on or off")] bool enabled)
    {
        await DeferAsync(ephemeral: true);

        if (enabled && !Context.Guild.CurrentUser.GuildPermissions.ManageEvents)
        {
            await FollowupAsync(
                "I don't have the **Manage Events** permission here. Re-invite me with the updated " +
                "invite link, or grant Manage Events to my role, then try again.",
                ephemeral: true);
            return;
        }

        var current = await api.GetGuildSettingsAsync((long)Context.Guild.Id);
        if (!current.Success || current.Value is null)
        {
            // Proceeding blind would overwrite the server timezone with the UTC fallback.
            await FollowupAsync($"❌ Couldn't load current settings: {current.Error}", ephemeral: true);
            return;
        }

        var result = await api.PutGuildSettingsAsync(
            (long)Context.Guild.Id,
            new GuildSettingsDto(current.Value.TimeZone, current.Value.DefaultChannelId, enabled));
        await FollowupAsync(
            result.Success
                ? enabled
                    ? "📅 Native Discord events are **on** — new events will appear in the server's Events tab (existing ones mirror when next edited)."
                    : "📅 Native Discord events are **off** — existing mirrored events stay until they finish; new ones won't be created."
                : $"❌ {result.Error}",
            ephemeral: true);
    }
}
