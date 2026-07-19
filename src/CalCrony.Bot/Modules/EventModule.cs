using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>Repeat-rule choices for /create, mapped to a RecurrenceRuleDto.</summary>
public enum RepeatChoice
{
    [ChoiceDisplay("daily")] Daily,
    [ChoiceDisplay("weekly")] Weekly,
    [ChoiceDisplay("monthly (same date)")] MonthlySameDate,
    [ChoiceDisplay("monthly (nth weekday)")] MonthlyNthWeekday,
    [ChoiceDisplay("no repeat (ignore template repeat)")] None,
}

/// <summary>Ask-per-edit scope choices for /edit on repeating events.</summary>
public enum EditScopeChoice
{
    [ChoiceDisplay("this occurrence")] Occurrence,
    [ChoiceDisplay("whole series")] Series,
}

/// <summary>Core event slash commands: create, list, edit, delete.</summary>
/// <param name="api">The CalCrony API client.</param>
[RequireContext(ContextType.Guild)]
public class EventModule(CalCronyApiClient api, NativeEventMirror mirror) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Creates an event (optionally recurring), posts its embed, and records the message ids.</summary>
    /// <param name="title">The event title.</param>
    /// <param name="when">Natural-language start time.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="duration">Duration in minutes.</param>
    /// <param name="channel">Target text channel (defaults to the current one).</param>
    /// <param name="location">Optional location text.</param>
    /// <param name="image">Optional image URL for the embed.</param>
    /// <param name="repeat">Repeat-rule choice; null keeps/omits recurrence.</param>
    /// <param name="repeatEvery">Repeat interval (every N units).</param>
    /// <param name="repeatUntil">Natural-language last repeat date.</param>
    /// <param name="repeatCount">Total occurrences including the first.</param>
    /// <param name="template">Template name/fragment or picker id to start from.</param>
    /// <param name="attendeeRole">Existing role granted to "Going" RSVPs, revoked at event end.</param>
    [SlashCommand("create", "Create an event")]
    public async Task CreateAsync(
        [Summary(description: "Event title")] string title,
        [Summary("when", "When it starts, e.g. \"tomorrow 6pm\" or \"in 5 hours\"")] string when,
        [Summary(description: "Event description")] string? description = null,
        [Summary("duration", "Duration in minutes")] int? duration = null,
        [Summary(description: "Channel to post the event in (defaults to here)")] ITextChannel? channel = null,
        [Summary(description: "Where the event happens")] string? location = null,
        [Summary("image", "Image URL for the event embed")] string? image = null,
        [Summary("repeat", "Repeat this event on a schedule anchored to the first occurrence")] RepeatChoice? repeat = null,
        [Summary("repeat-every", "Repeat interval: every N days/weeks/months (1-12)"), MinValue(1), MaxValue(12)] int repeatEvery = 1,
        [Summary("repeat-until", "Last date it repeats, e.g. \"Aug 30\" — leave empty for no end date")] string? repeatUntil = null,
        [Summary("repeat-count", "Total occurrences including the first (2-500)"), MinValue(2), MaxValue(500)] int? repeatCount = null,
        [Summary("template", "Start from a saved template"), Autocomplete(typeof(TemplateNameAutocompleteHandler))] string? template = null,
        [Summary("attendee-role", "Existing role given to \"Going\" RSVPs (removed when the event ends)")] IRole? attendeeRole = null)
    {
        await DeferAsync(ephemeral: true);

        var targetChannel = channel ?? Context.Channel as ITextChannel;
        if (targetChannel is null)
        {
            await FollowupAsync("Events can only be created in text channels.", ephemeral: true);
            return;
        }

        if (attendeeRole is not null && ValidateAttendeeRole(attendeeRole) is { } roleProblem)
        {
            await FollowupAsync(roleProblem, ephemeral: true);
            return;
        }

        EventTemplateDto? resolvedTemplate = null;
        if (template is not null)
        {
            var (found, templateProblem) = await TemplateFinder.FindSingleAsync(api, (long)Context.Guild.Id, template);
            if (found is null)
            {
                await FollowupAsync(templateProblem!, ephemeral: true);
                return;
            }

            resolvedTemplate = found;
        }

        // A template with a rule can legitimately carry the repeat end options; otherwise the
        // API remains the validator of record for the same rule.
        var templateHasRule = resolvedTemplate?.Recurrence is not null;
        if (repeat is null && !templateHasRule
            && (repeatEvery != 1 || repeatUntil is not null || repeatCount is not null))
        {
            await FollowupAsync("Set `repeat` to use the repeat options.", ephemeral: true);
            return;
        }

        var recurrence = repeat switch
        {
            RepeatChoice.Daily => new RecurrenceRuleDto(RecurrenceUnit.Day, repeatEvery),
            RepeatChoice.Weekly => new RecurrenceRuleDto(RecurrenceUnit.Week, repeatEvery),
            RepeatChoice.MonthlySameDate => new RecurrenceRuleDto(RecurrenceUnit.Month, repeatEvery),
            RepeatChoice.MonthlyNthWeekday => new RecurrenceRuleDto(RecurrenceUnit.Month, repeatEvery, MonthlyMode.NthWeekday),
            _ => null,
        };

        var result = await api.CreateEventAsync(
            (long)Context.Guild.Id,
            new CreateEventRequest(
                (long)Context.User.Id, title, when, (long)targetChannel.Id,
                description, duration, location, image,
                recurrence, repeatUntil, repeatCount,
                resolvedTemplate?.Id, NoRecurrence: repeat == RepeatChoice.None,
                AttendeeRoleId: (long?)attendeeRole?.Id));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var ev = result.Value;
        var message = await targetChannel.SendMessageAsync(
            embed: EventEmbedBuilder.Build(ev),
            components: EventEmbedBuilder.BuildComponents(ev));
        var recorded = await api.SetMessageAsync(ev.Id, new SetEventMessageRequest((long)targetChannel.Id, (long)message.Id));
        if (recorded.Success && recorded.Value is not null)
        {
            await mirror.TryUpsertAsync(recorded.Value);
        }

        var repeatNote = ev.RecurrenceSummary is null ? "" : $" · 🔁 {ev.RecurrenceSummary}";
        var roleNote = ev.AttendeeRoleId is null ? "" : $" · 🏷️ Going grants <@&{ev.AttendeeRoleId}>";
        await FollowupAsync(
            $"✅ **{ev.Title}** created in {targetChannel.Mention} for <t:{ev.StartsAtUnix}:F>.{repeatNote}{roleNote}",
            ephemeral: true);
    }

    /// <summary>Lists upcoming events as an ephemeral embed.</summary>
    /// <param name="channel">Target text channel (defaults to the current one).</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    [SlashCommand("list", "List upcoming events")]
    public async Task ListAsync(
        [Summary(description: "Only events posted in this channel")] ITextChannel? channel = null,
        [Summary(description: "Max number of events (1-25)")] int limit = 10)
    {
        await DeferAsync(ephemeral: true);

        var result = await api.ListEventsAsync((long)Context.Guild.Id, (long?)channel?.Id, limit);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        if (result.Value.Count == 0)
        {
            await FollowupAsync("No upcoming events. Create one with `/create`!", ephemeral: true);
            return;
        }

        var lines = result.Value.Select(e =>
        {
            var going = e.Options.FirstOrDefault(o => o.SortOrder == 0);
            var goingCount = going is null ? 0 : e.Rsvps.Count(r => r.OptionId == going.Id);
            return $"**{e.Title}** — <t:{e.StartsAtUnix}:F> (<t:{e.StartsAtUnix}:R>) in <#{e.ChannelId}> · {goingCount} going";
        });

        var embed = new EmbedBuilder()
            .WithTitle("Upcoming events")
            .WithColor(new Color(0x57, 0xB9, 0xE2))
            .WithDescription(string.Join("\n", lines))
            .Build();
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    /// <summary>Deletes an event by name/picker (creator or manager); a live series occurrence stops its series.</summary>
    /// <param name="name">Event title (or fragment), or an autocomplete-picked event id.</param>
    [SlashCommand("delete", "Delete an event you created")]
    public async Task DeleteAsync(
        [Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await FindSingleEventAsync(name);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (!CanManage(ev))
        {
            await FollowupAsync("Only the event creator or a server manager can delete this event.", ephemeral: true);
            return;
        }

        var result = await api.DeleteEventAsync(ev.Id);
        if (!result.Success)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await TryDeleteMessageAsync(ev);
        await mirror.TryDeleteAsync(ev.GuildId, ev.NativeEventId);
        var seriesNote = ev.RecurrenceSummary is null
            ? ""
            : " This was a repeating event, so the series has been stopped.";
        await FollowupAsync($"🗑️ Deleted **{ev.Title}**.{seriesNote}", ephemeral: true);
    }

    /// <summary>Edits an event by name/picker; repeating events require a scope.</summary>
    /// <param name="name">Event title (or fragment), or an autocomplete-picked event id.</param>
    /// <param name="title">The event title.</param>
    /// <param name="when">Natural-language start time.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="duration">Duration in minutes.</param>
    /// <param name="location">Optional location text.</param>
    /// <param name="image">Optional image URL for the embed.</param>
    /// <param name="scope">Whether the change applies to this occurrence or the whole series.</param>
    /// <param name="attendeeRole">Replacement attendee role; existing grants are re-synced.</param>
    /// <param name="clearAttendeeRole">Removes the attendee role (existing grants are revoked).</param>
    [SlashCommand("edit", "Edit an event you created")]
    public async Task EditAsync(
        [Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name,
        [Summary(description: "New title")] string? title = null,
        [Summary("when", "New start time, e.g. \"saturday 7pm\"")] string? when = null,
        [Summary(description: "New description")] string? description = null,
        [Summary("duration", "New duration in minutes")] int? duration = null,
        [Summary(description: "New location")] string? location = null,
        [Summary("image", "New image URL")] string? image = null,
        [Summary("scope", "Repeating events: apply to this occurrence only or the whole series")] EditScopeChoice? scope = null,
        [Summary("attendee-role", "New role given to \"Going\" RSVPs (existing grants move over)")] IRole? attendeeRole = null,
        [Summary("clear-attendee-role", "Remove the attendee role (grants are removed too)")] bool clearAttendeeRole = false)
    {
        await DeferAsync(ephemeral: true);

        if (title is null && when is null && description is null && duration is null && location is null
            && image is null && attendeeRole is null && !clearAttendeeRole)
        {
            await FollowupAsync("Nothing to change — pass at least one field.", ephemeral: true);
            return;
        }

        if (attendeeRole is not null && ValidateAttendeeRole(attendeeRole) is { } roleProblem)
        {
            await FollowupAsync(roleProblem, ephemeral: true);
            return;
        }

        var (ev, problem) = await FindSingleEventAsync(name);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (!CanManage(ev))
        {
            await FollowupAsync("Only the event creator or a server manager can edit this event.", ephemeral: true);
            return;
        }

        // Friendlier than the API's 400 for the same rule (which still enforces it regardless).
        if (ev.RecurrenceSummary is not null
            && ev.Status is EventStatus.Scheduled or EventStatus.Started
            && scope is null)
        {
            await FollowupAsync(
                $"✋ **{ev.Title}** repeats — run again with `scope` set to *this occurrence* or *whole series*.",
                ephemeral: true);
            return;
        }

        var result = await api.UpdateEventAsync(ev.Id, new UpdateEventRequest(
            (long)Context.User.Id, title, when, description, duration, location, image,
            Scope: scope switch
            {
                EditScopeChoice.Occurrence => EditScope.Occurrence,
                EditScopeChoice.Series => EditScope.Series,
                _ => null,
            },
            AttendeeRoleId: (long?)attendeeRole?.Id,
            ClearAttendeeRole: clearAttendeeRole));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await TryUpdateMessageAsync(result.Value);
        await mirror.TryUpsertAsync(result.Value);
        await FollowupAsync($"✏️ Updated **{result.Value.Title}**.", ephemeral: true);
    }

    /// <summary>Creator-or-ManageGuild check mirroring the API guard.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>True for the creator or a ManageGuild holder.</returns>
    private bool CanManage(EventDto ev) =>
        (long)Context.User.Id == ev.CreatorId ||
        (Context.User is IGuildUser guildUser && guildUser.GuildPermissions.ManageGuild);

    /// <summary>Friendly pre-check that the bot can actually assign the picked role — grants are
    /// best-effort later, so a bad pick would otherwise fail silently.</summary>
    /// <param name="role">The picked role.</param>
    /// <returns>Null when assignable, else the refusal message.</returns>
    private string? ValidateAttendeeRole(IRole role) => AttendeeRoleSpec.Validate(
        role.Name,
        Context.Guild.CurrentUser.GuildPermissions.ManageRoles,
        Context.Guild.CurrentUser.Hierarchy,
        role.Position,
        role.Id == Context.Guild.Id,
        role.IsManaged);

    private Task<(EventDto? Event, string? Problem)> FindSingleEventAsync(string name) =>
        EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);

    /// <summary>Re-renders the posted embed in place; tolerates a manually deleted message.</summary>
    /// <param name="ev">The event.</param>
    private async Task TryUpdateMessageAsync(EventDto ev)
    {
        if (ev.MessageId is not long messageId)
        {
            return;
        }

        try
        {
            var channel = Context.Guild.GetTextChannel((ulong)ev.ChannelId);
            if (channel is not null && await channel.GetMessageAsync((ulong)messageId) is IUserMessage message)
            {
                await message.ModifyAsync(m =>
                {
                    m.Embed = EventEmbedBuilder.Build(ev);
                    m.Components = EventEmbedBuilder.BuildComponents(ev);
                });
            }
        }
        catch
        {
            // The posted message may have been deleted manually; not fatal.
        }
    }

    /// <summary>Deletes the posted embed; tolerates it already being gone.</summary>
    /// <param name="ev">The event.</param>
    private async Task TryDeleteMessageAsync(EventDto ev)
    {
        if (ev.MessageId is not long messageId)
        {
            return;
        }

        try
        {
            var channel = Context.Guild.GetTextChannel((ulong)ev.ChannelId);
            if (channel is not null && await channel.GetMessageAsync((ulong)messageId) is IMessage message)
            {
                await message.DeleteAsync();
            }
        }
        catch
        {
            // Already gone; fine.
        }
    }
}
