using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/settings — view and change server and personal preferences.</summary>
/// <param name="api">The CalCrony API client.</param>
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[RequireBotInGuild]
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
        var publicCalendar = await api.GetPublicCalendarAsync((long)Context.Guild.Id);
        var hours = await api.GetAvailableHoursAsync((long)Context.User.Id);
        if (!guild.Success || !user.Success || !publicCalendar.Success || !hours.Success || hours.Value is null)
        {
            // Never guess a privacy-sensitive setting: a failed read is an error, not "off".
            await FollowupAsync($"❌ {guild.Error ?? user.Error ?? publicCalendar.Error ?? hours.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"**Server timezone:** {guild.Value!.TimeZone}\n" +
            $"**Your timezone:** {user.Value!.TimeZone ?? "(not set — server timezone is used)"}\n" +
            $"**Your DM confirmations:** {(user.Value.DmConfirmations ? "on" : "off")}\n" +
            $"**Native Discord events:** {(guild.Value.MirrorNativeEvents ? "on" : "off")}\n" +
            $"**Public calendar:** {(publicCalendar.Value!.Enabled ? "on" : "off")}\n" +
            $"**Your DM reminders:** {(user.Value.DmReminders == true ? "on" : "off")}{DmRemindersBlockedNote(user.Value)}\n" +
            AvailableHoursText.ViewLine(hours.Value),
            ephemeral: true);
    }

    /// <summary>Turns DM reminders for attended events on or off — a strictly personal opt-in
    /// (default off) that nobody else can flip for you.</summary>
    /// <param name="enabled">Whether to DM reminders and start pings for events you're attending.</param>
    [SlashCommand("dm-reminders", "DM me reminders and start pings for events I'm attending (off by default)")]
    public async Task SetDmRemindersAsync(
        [Summary("enabled", "Turn DM reminders on or off")] bool enabled)
    {
        await DeferAsync(ephemeral: true);

        var userId = (long)Context.User.Id;
        var current = await api.GetUserSettingsAsync(userId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync($"❌ Couldn't load your settings: {current.Error}", ephemeral: true);
            return;
        }

        // Read-modify-write: the timezone, theme, and confirmation settings ride along untouched.
        var result = await api.PutUserSettingsAsync(userId, current.Value with { DmReminders = enabled });
        await FollowupAsync(
            !result.Success
                ? $"❌ {result.Error}"
                : enabled
                    ? "🔔 DM reminders are **on** — reminders and start pings for events you're attending will also arrive by DM. If your DMs are closed to the bot, this switches itself off."
                    : "🔕 DM reminders are **off**.",
            ephemeral: true);
    }

    /// <summary>Explains an automatic switch-off (Discord refused a DM), or nothing.</summary>
    /// <param name="settings">The user's settings.</param>
    /// <returns>The note to append, or an empty string.</returns>
    private static string DmRemindersBlockedNote(UserSettingsDto settings) =>
        settings.DmRemindersBlockedAtUtc is { } blockedAt
            ? $" (turned off <t:{blockedAt.ToUnixTimeSeconds()}:R> because your DMs were closed to the bot)"
            : "";

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
        if (!current.Success || current.Value is null)
        {
            // Proceeding blind would wipe the default channel and the native-events flag.
            await FollowupAsync($"❌ Couldn't load current settings: {current.Error}", ephemeral: true);
            return;
        }

        var result = await api.PutGuildSettingsAsync(
            (long)Context.Guild.Id,
            new GuildSettingsDto(timezone, current.Value.DefaultChannelId, current.Value.MirrorNativeEvents),
            (long)Context.User.Id);
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
            new GuildSettingsDto(current.Value.TimeZone, (long)channel.Id, current.Value.MirrorNativeEvents),
            (long)Context.User.Id);
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
            new GuildSettingsDto(current.Value.TimeZone, current.Value.DefaultChannelId, enabled),
            (long)Context.User.Id);
        await FollowupAsync(
            result.Success
                ? enabled
                    ? "📅 Native Discord events are **on** — new events will appear in the server's Events tab (existing ones mirror when next edited)."
                    : "📅 Native Discord events are **off** — existing mirrored events stay until they finish; new ones won't be created."
                : $"❌ {result.Error}",
            ephemeral: true);
    }
    /// <summary>Choices for <c>/settings public-calendar</c>.</summary>
    public enum PublicCalendarMode
    {
        /// <summary>Turn the public calendar on (keeps an existing link).</summary>
        [ChoiceDisplay("on")] On,

        /// <summary>Turn it off — the link stops working.</summary>
        [ChoiceDisplay("off")] Off,

        /// <summary>Stay on but mint a new link, retiring the old one.</summary>
        [ChoiceDisplay("new-link")] NewLink,
    }

    /// <summary>Turns the opt-in public calendar on or off, or mints a new link (managers only).
    /// Off by default; the reply spells out exactly what a link-holder can see.</summary>
    /// <param name="mode">on, off, or new-link (retires the old link).</param>
    [SlashCommand("public-calendar", "Share a login-free calendar link for this server's events (managers only)")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SetPublicCalendarAsync(
        [Summary("mode", "on · off · new-link (retires the old link)")] PublicCalendarMode mode)
    {
        await DeferAsync(ephemeral: true);

        var result = await api.PutPublicCalendarAsync(
            (long)Context.Guild.Id,
            new PublicCalendarRequest(Enabled: mode != PublicCalendarMode.Off, Regenerate: mode == PublicCalendarMode.NewLink),
            (long)Context.User.Id);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        if (!result.Value.Enabled)
        {
            await FollowupAsync("🔒 Public calendar is **off** — the old link no longer works.", ephemeral: true);
            return;
        }

        var status = mode == PublicCalendarMode.NewLink
            ? "🔗 Public calendar is **on** with a new link — the old one no longer works."
            : "🔗 Public calendar is **on**.";
        await FollowupAsync(
            $"{status}\n{PublicCalendarLinkLine(result.Value)}\n" +
            "Anyone with the link sees event titles, start times, durations, locations, and channel names — no sign-in, and never descriptions, RSVPs, or member names. " +
            "`/settings public-calendar off` turns it off; `new-link` retires a leaked link.",
            ephemeral: true);
    }

    /// <summary>The shareable link, or the web-app path with a hint when the API has no Web:Origin.</summary>
    /// <param name="settings">The public-calendar state (enabled).</param>
    /// <returns>The line to show.</returns>
    private static string PublicCalendarLinkLine(PublicCalendarSettingsDto settings) =>
        settings.Url is { } url
            ? $"<{url}>"
            : $"Path `{settings.Path}` on your CalCrony web app (set `Web:Origin` on the API to get a full link here).";

    /// <summary>/settings available-hours — the hours a member is usually available, one range per
    /// weekday, which free-slot lists are cut from. Every command here acts on the member who runs
    /// it and takes no user option: nobody sets or reads another member's hours. The rules and the
    /// replies are <see cref="AvailableHoursText"/>'s; this group reads, writes and replies. It
    /// carries the module's own context attributes, as every module type must.</summary>
    /// <param name="api">The CalCrony API client.</param>
    [CommandContextType(InteractionContextType.Guild)]
    [IntegrationType(ApplicationIntegrationType.GuildInstall)]
    [RequireBotInGuild]
    [Group("available-hours", "The hours you're usually available; free slots are cut from them")]
    public class AvailableHoursGroup(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
    {
        /// <summary>Gives the named days a range and leaves the others as they were. A first save
        /// starts from an empty week, so days not named are not available.</summary>
        /// <param name="days">The days the range applies to.</param>
        /// <param name="from">The start, picked from the suggestions or typed.</param>
        /// <param name="to">The end, picked from the suggestions or typed; midnight ends the day.</param>
        [SlashCommand("set", "Save your hours for the days you pick; other members can then list your free slots by name")]
        public async Task SetAsync(
            [Summary("days", "The days these hours apply to")] AvailableHoursDays days,
            [Summary(TimeOfDayAutocompleteHandler.StartOptionName, "Start time, e.g. 6 PM or 18:00"), Autocomplete(typeof(TimeOfDayAutocompleteHandler)), MaxLength(TimeOfDayText.MaxInputLength)] string from,
            [Summary(TimeOfDayAutocompleteHandler.EndOptionName, "End time, e.g. 11 PM or 23:00; midnight ends the day"), Autocomplete(typeof(TimeOfDayAutocompleteHandler)), MaxLength(TimeOfDayText.MaxInputLength)] string to)
        {
            await DeferAsync(ephemeral: true);

            if (!AvailableHoursText.TryReadRange(from, to, out var range, out var refusal))
            {
                await FollowupAsync(refusal, ephemeral: true);
                return;
            }

            if (await LoadAsync() is not { } current)
            {
                return;
            }

            await SaveAndReplyAsync(AvailableHoursText.Set(current.Hours, days, range), current);
        }

        /// <summary>Marks the named days as not available. With nothing saved there is nothing to
        /// switch off, and a week with no day left cannot be saved; both are refused.</summary>
        /// <param name="days">The days to mark as not available.</param>
        [SlashCommand("off", "Mark days as not available")]
        public async Task OffAsync(
            [Summary("days", "The days to mark as not available")] AvailableHoursDays days)
        {
            await DeferAsync(ephemeral: true);

            if (await LoadAsync() is not { } current)
            {
                return;
            }

            if (!AvailableHoursText.TryOff(current.Hours, days, out var week, out var refusal))
            {
                await FollowupAsync(refusal, ephemeral: true);
                return;
            }

            await SaveAndReplyAsync(week, current);
        }

        /// <summary>Clears the caller's saved hours: the default applies again, and other members
        /// can no longer list their free slots by name.</summary>
        [SlashCommand("reset", "Go back to the default hours, 8 AM–11 PM every day")]
        public async Task ResetAsync()
        {
            await DeferAsync(ephemeral: true);

            var result = await api.ResetAvailableHoursAsync((long)Context.User.Id);
            await FollowupAsync(result.Success ? AvailableHoursText.ResetReply() : $"❌ {result.Error}", ephemeral: true);
        }

        /// <summary>Reads everything a save needs before anything is written: the caller's hours
        /// and timezone, and the server's timezone when they have none of their own — the reply
        /// has to say which zone the hours are read in. A failed read is reported and stops the
        /// command, so nothing is saved over what could not be read.</summary>
        /// <returns>What was read, or null when a read failed and the caller has been told.</returns>
        private async Task<Loaded?> LoadAsync()
        {
            var userId = (long)Context.User.Id;
            var hours = await api.GetAvailableHoursAsync(userId);
            var settings = await api.GetUserSettingsAsync(userId);
            if (!hours.Success || hours.Value is null || !settings.Success || settings.Value is null)
            {
                await FollowupAsync($"❌ Couldn't load your settings: {hours.Error ?? settings.Error}", ephemeral: true);
                return null;
            }

            string? guildTimeZone = null;
            if (string.IsNullOrWhiteSpace(settings.Value.TimeZone))
            {
                var guild = await api.GetGuildSettingsAsync((long)Context.Guild.Id);
                if (!guild.Success || guild.Value is null)
                {
                    await FollowupAsync($"❌ Couldn't load your settings: {guild.Error}", ephemeral: true);
                    return null;
                }

                guildTimeZone = guild.Value.TimeZone;
            }

            return new Loaded(hours.Value, settings.Value.TimeZone, guildTimeZone);
        }

        /// <summary>Saves the week and replies with what the API stored: the whole week, the zone
        /// it is read in, and what saving hours allows.</summary>
        /// <param name="week">The week to save.</param>
        /// <param name="current">What was read before the save.</param>
        private async Task SaveAndReplyAsync(IReadOnlyList<AvailableHoursDayDto> week, Loaded current)
        {
            var saved = await api.PutAvailableHoursAsync((long)Context.User.Id, new SetAvailableHoursRequest(week));
            await FollowupAsync(
                saved.Success && saved.Value is not null
                    ? AvailableHoursText.SavedReply(saved.Value.Days, current.UserTimeZone, current.GuildTimeZone)
                    : $"❌ {saved.Error}",
                ephemeral: true);
        }

        /// <summary>What a command read before saving.</summary>
        /// <param name="Hours">The caller's hours.</param>
        /// <param name="UserTimeZone">The caller's own timezone, when they have set one.</param>
        /// <param name="GuildTimeZone">The server's timezone, read only when the caller has none.</param>
        private sealed record Loaded(AvailableHoursDto Hours, string? UserTimeZone, string? GuildTimeZone);
    }
}
