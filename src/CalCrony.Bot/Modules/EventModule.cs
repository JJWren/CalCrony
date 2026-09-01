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
    [ChoiceDisplay("yearly")] Yearly,
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
/// <param name="mirror">The native scheduled-event mirror.</param>
/// <param name="threadManager">The event-thread manager.</param>
[RequireContext(ContextType.Guild)]
public class EventModule(CalCronyApiClient api, NativeEventMirror mirror, EventThreadManager threadManager)
    : InteractionModuleBase<SocketInteractionContext>
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
    /// <param name="repeatDays">Weekly day set (see <see cref="RepeatDaysSyntax"/>); weekly only.</param>
    /// <param name="repeatUntil">Natural-language last repeat date.</param>
    /// <param name="repeatCount">Total occurrences including the first.</param>
    /// <param name="template">Template name/fragment or picker id to start from.</param>
    /// <param name="attendeeRole">Existing role granted to attending RSVPs, revoked at event end.</param>
    /// <param name="thread">Opens a discussion thread on the event message.</param>
    /// <param name="rsvpOptions">Custom RSVP buttons (see <see cref="RsvpOptionSyntax"/>).</param>
    /// <param name="attendeeLimit">Cap on the attending option; extras join the waitlist.</param>
    /// <param name="rsvpClose">RSVP cutoff — relative ("2h before") or absolute natural language.</param>
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
        [Summary("repeat-every", "Repeat interval: every N days/weeks/months/years (1-12)"), MinValue(1), MaxValue(12)] int repeatEvery = 1,
        [Summary("repeat-days", "Weekly only: days it repeats on, e.g. \"tue,thu\" or \"weekdays\"")] string? repeatDays = null,
        [Summary("repeat-until", "Last date it repeats, e.g. \"Aug 30\" — leave empty for no end date")] string? repeatUntil = null,
        [Summary("repeat-count", "Total occurrences including the first (2-500)"), MinValue(2), MaxValue(500)] int? repeatCount = null,
        [Summary("template", "Start from a saved template"), Autocomplete(typeof(TemplateNameAutocompleteHandler))] string? template = null,
        [Summary("attendee-role", "Existing role given to attending RSVPs (removed when the event ends)")] IRole? attendeeRole = null,
        [Summary("thread", "Open a discussion thread on the event message (attending RSVPs are added)")] bool thread = false,
        [Summary("rsvp-options", "Custom RSVP buttons, e.g. \"⚔️ Raider x10, 🛡️ Standby, ❌ Out\" — first is the attending one")] string? rsvpOptions = null,
        [Summary("attendee-limit", "Max attendees — extra RSVPs join a waitlist and are promoted when a spot frees"), MinValue(1)] int? attendeeLimit = null,
        [Summary("rsvp-close", "When RSVPs stop, e.g. \"2h before\" or \"friday 5pm\"")] string? rsvpClose = null)
    {
        await DeferAsync(ephemeral: true);

        List<RsvpOptionSpec>? optionSpecs = null;
        if (rsvpOptions is not null)
        {
            if (!RsvpOptionSyntax.TryParse(rsvpOptions, out var parsed, out var syntaxProblem))
            {
                await FollowupAsync($"❌ {syntaxProblem}", ephemeral: true);
                return;
            }

            optionSpecs = parsed;
            if (ValidateSpecRoles(optionSpecs) is { } specRoleProblem)
            {
                await FollowupAsync(specRoleProblem, ephemeral: true);
                return;
            }
        }

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

        if (thread && !Context.Guild.CurrentUser.GetPermissions(targetChannel).CreatePublicThreads)
        {
            await FollowupAsync(
                $"I need the **Create Public Threads** permission in {targetChannel.Mention} to open a discussion thread.",
                ephemeral: true);
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
            && (repeatEvery != 1 || repeatUntil is not null || repeatCount is not null || repeatDays is not null))
        {
            await FollowupAsync("Set `repeat` to use the repeat options.", ephemeral: true);
            return;
        }

        // A template's rule can't be re-shaped from here, so a day set always needs an explicit
        // weekly choice (RepeatOptions enforces that).
        if (RepeatOptions.TryBuildRule(repeat, repeatEvery, repeatDays, allowNoneDays: false, out var recurrence) is { } optionsProblem)
        {
            await FollowupAsync(optionsProblem, ephemeral: true);
            return;
        }

        var result = await api.CreateEventAsync(
            (long)Context.Guild.Id,
            new CreateEventRequest(
                (long)Context.User.Id, title, when, (long)targetChannel.Id,
                description, duration, location, image,
                recurrence, repeatUntil, repeatCount,
                resolvedTemplate?.Id, NoRecurrence: repeat == RepeatChoice.None,
                AttendeeRoleId: (long?)attendeeRole?.Id,
                WantsThread: thread,
                RsvpOptions: optionSpecs,
                AttendeeLimit: attendeeLimit,
                RsvpCloseText: rsvpClose));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var ev = result.Value;
        var message = await targetChannel.SendMessageAsync(
            embed: EventEmbedBuilder.Build(ev),
            components: EventEmbedBuilder.BuildComponents(ev));
        var recorded = await api.SetMessageAsync(
            ev.Id, new SetEventMessageRequest((long)targetChannel.Id, (long)message.Id, targetChannel.Name));
        if (recorded.Success && recorded.Value is not null)
        {
            await mirror.TryUpsertAsync(recorded.Value);
            await threadManager.TryCreateAsync(recorded.Value, message);
        }

        var repeatNote = ev.RecurrenceSummary is null ? "" : $" · 🔁 {ev.RecurrenceSummary}";
        var roleNote = ev.RoleGrantingOptions is { Count: > 0 } roleOptions
            ? " · 🏷️ " + string.Join(
                " · ", roleOptions.Select(o => $"{o.Label} grants <@&{o.AttendeeRoleId}>"))
            : "";
        // "opening", not "opened" — thread creation is best-effort and may still fail.
        var threadNote = ev.WantsThread ? " · 🧵 opening a discussion thread" : "";
        var limitNote = ev.AttendingOption?.Capacity is int cap ? $" · 👥 limited to {cap} (waitlist after)" : "";
        var closeNote = ev.RsvpCloseUnix is long closeUnix ? $" · 🔒 RSVPs close <t:{closeUnix}:f>" : "";
        await FollowupAsync(
            $"✅ **{ev.Title}** created in {targetChannel.Mention} for <t:{ev.StartsAtUnix}:F>.{repeatNote}{roleNote}{threadNote}{limitNote}{closeNote}",
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

        // Same line format the live list uses, so the two surfaces read identically.
        var lines = result.Value.Select(LiveListEmbedBuilder.FormatLine);

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

        var result = await api.DeleteEventAsync(ev.Id, (long)Context.User.Id);
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
    /// <param name="rsvpOptions">Replacement RSVP buttons — labels match existing options to keep their RSVPs.</param>
    /// <param name="attendeeLimit">New cap on the attending option.</param>
    /// <param name="clearAttendeeLimit">Removes the cap (the waitlist is seated).</param>
    /// <param name="rsvpClose">New RSVP cutoff — relative ("2h before") or absolute.</param>
    /// <param name="clearRsvpClose">Removes the cutoff (RSVPs reopen).</param>
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
        [Summary("attendee-role", "New role given to attending RSVPs (existing grants move over)")] IRole? attendeeRole = null,
        [Summary("clear-attendee-role", "Remove the attendee role (grants are removed too)")] bool clearAttendeeRole = false,
        [Summary("rsvp-options", "Replacement RSVP buttons — same labels keep their RSVPs; options with RSVPs can't be dropped")] string? rsvpOptions = null,
        [Summary("attendee-limit", "New max attendees (raising it seats waitlisted members)"), MinValue(1)] int? attendeeLimit = null,
        [Summary("clear-attendee-limit", "Remove the attendee limit — the whole waitlist is seated")] bool clearAttendeeLimit = false,
        [Summary("rsvp-close", "New RSVP cutoff, e.g. \"2h before\" or \"friday 5pm\"")] string? rsvpClose = null,
        [Summary("clear-rsvp-close", "Remove the RSVP cutoff (RSVPs reopen)")] bool clearRsvpClose = false)
    {
        await DeferAsync(ephemeral: true);

        if (title is null && when is null && description is null && duration is null && location is null
            && image is null && attendeeRole is null && !clearAttendeeRole
            && rsvpOptions is null && attendeeLimit is null && !clearAttendeeLimit
            && rsvpClose is null && !clearRsvpClose)
        {
            await FollowupAsync("Nothing to change — pass at least one field.", ephemeral: true);
            return;
        }

        List<RsvpOptionSpec>? optionSpecs = null;
        if (rsvpOptions is not null)
        {
            if (!RsvpOptionSyntax.TryParse(rsvpOptions, out var parsed, out var syntaxProblem))
            {
                await FollowupAsync($"❌ {syntaxProblem}", ephemeral: true);
                return;
            }

            optionSpecs = parsed;
            if (ValidateSpecRoles(optionSpecs) is { } specRoleProblem)
            {
                await FollowupAsync(specRoleProblem, ephemeral: true);
                return;
            }
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
            ClearAttendeeRole: clearAttendeeRole,
            RsvpOptions: optionSpecs,
            AttendeeLimit: attendeeLimit,
            ClearAttendeeLimit: clearAttendeeLimit,
            RsvpCloseText: rsvpClose,
            ClearRsvpClose: clearRsvpClose));
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

    /// <summary>Validates the roles mentioned inside <c>rsvp-options</c> the same way the
    /// <c>attendee-role</c> argument is validated. The syntax parser is pure and guild-less, so it
    /// yields raw snowflakes; without this a pasted id for a deleted, managed, @everyone, or
    /// above-the-bot role would report success and then be dropped silently at grant time.</summary>
    /// <param name="specs">The parsed option specs.</param>
    /// <returns>The user-facing problem, or null when every mentioned role is assignable.</returns>
    private string? ValidateSpecRoles(IEnumerable<RsvpOptionSpec> specs)
    {
        foreach (var roleId in specs.Select(s => s.AttendeeRoleId).OfType<long>().Distinct())
        {
            if (Context.Guild.GetRole((ulong)roleId) is not { } role)
            {
                return $"❌ <@&{roleId}> isn't a role in this server — pick one that exists here.";
            }

            if (ValidateAttendeeRole(role) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

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
