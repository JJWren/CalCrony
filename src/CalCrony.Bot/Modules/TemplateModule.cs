using System.Text;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/template — save, list, edit, and delete reusable event templates.</summary>
/// <param name="api">The CalCrony API client.</param>
[RequireContext(ContextType.Guild)]
[Group("template", "Reusable event templates")]
public class TemplateModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Saves a template from an existing event's current content, reminders, and repeat rule.</summary>
    /// <param name="name">The template name (1-64 chars, unique per server).</param>
    /// <param name="eventName">Event title (or fragment), or an autocomplete-picked event id.</param>
    [SlashCommand("save", "Save an event's setup as a reusable template")]
    public async Task SaveAsync(
        [Summary("name", "Template name (unique per server)"), MaxLength(64)] string name,
        [Summary("event", "The event to capture"), Autocomplete(typeof(EventNameAutocompleteHandler))] string eventName)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, eventName, includePast: true);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        var result = await api.SaveTemplateAsync(
            (long)Context.Guild.Id, new SaveTemplateRequest((long)Context.User.Id, name, ev.Id));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var saved = result.Value;
        var markers = new StringBuilder();
        if (saved.Notifications.Count > 0)
        {
            markers.Append($" · 🔔 {saved.Notifications.Count} reminder{(saved.Notifications.Count == 1 ? "" : "s")}");
        }

        if (saved.Recurrence is not null)
        {
            markers.Append(" · 🔁 repeats");
        }

        await FollowupAsync(
            $"✅ Saved template **{saved.Name}** from **{ev.Title}**.{markers} Use it with `/create template:{saved.Name}`.",
            ephemeral: true);
    }

    /// <summary>Lists the server's templates with content summaries.</summary>
    [SlashCommand("list", "List this server's event templates")]
    public async Task ListAsync()
    {
        await DeferAsync(ephemeral: true);

        var result = await api.ListTemplatesAsync((long)Context.Guild.Id);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        if (result.Value.Count == 0)
        {
            await FollowupAsync("No templates yet — save one with `/template save`.", ephemeral: true);
            return;
        }

        var lines = result.Value.Select(t =>
        {
            var parts = new List<string> { t.Title };
            if (t.DurationMinutes is int minutes)
            {
                parts.Add($"{minutes} min");
            }

            if (t.Recurrence is not null)
            {
                parts.Add("🔁");
            }

            if (t.Notifications.Count > 0)
            {
                parts.Add($"🔔{t.Notifications.Count}");
            }

            return $"**{t.Name}** — {string.Join(" · ", parts)}";
        });

        var embed = new EmbedBuilder()
            .WithTitle("Event templates")
            .WithColor(new Color(0x57, 0xB9, 0xE2))
            .WithDescription(string.Join("\n", lines))
            .Build();
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    /// <summary>Edits a template's content or repeat rule (creator or server manager). Reminder
    /// specs are edited on the web (list-shaped) or by re-saving from an event.</summary>
    /// <param name="name">Template name (or fragment), or an autocomplete-picked template id.</param>
    /// <param name="newName">The new template name.</param>
    /// <param name="title">The event title.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="duration">Duration in minutes.</param>
    /// <param name="location">Optional location text.</param>
    /// <param name="image">Optional image URL.</param>
    /// <param name="repeat">Replacement repeat rule; "no repeat" clears it.</param>
    /// <param name="repeatEvery">Repeat interval (every N units).</param>
    [SlashCommand("edit", "Edit a template (creator or manager)")]
    public async Task EditAsync(
        [Summary("name", "The template to edit"), Autocomplete(typeof(TemplateNameAutocompleteHandler))] string name,
        [Summary("new-name", "New template name (unique per server)"), MaxLength(64)] string? newName = null,
        [Summary(description: "New title")] string? title = null,
        [Summary(description: "New description")] string? description = null,
        [Summary("duration", "New duration in minutes")] int? duration = null,
        [Summary(description: "New location")] string? location = null,
        [Summary("image", "New image URL")] string? image = null,
        [Summary("repeat", "New repeat rule (\"no repeat\" clears it)")] RepeatChoice? repeat = null,
        [Summary("repeat-every", "Repeat interval: every N days/weeks/months (1-12)"), MinValue(1), MaxValue(12)] int repeatEvery = 1)
    {
        await DeferAsync(ephemeral: true);

        if (newName is null && title is null && description is null && duration is null
            && location is null && image is null && repeat is null)
        {
            await FollowupAsync("Nothing to change — pass at least one field.", ephemeral: true);
            return;
        }

        if (repeat is null && repeatEvery != 1)
        {
            // Without a rule choice, repeat-every would be silently ignored — mirroring /create.
            await FollowupAsync("Set `repeat` to use the repeat options.", ephemeral: true);
            return;
        }

        var (template, problem) = await TemplateFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (template is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (!CanManage(template))
        {
            await FollowupAsync("Only the template creator or a server manager can edit this template.", ephemeral: true);
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

        var result = await api.UpdateTemplateAsync(template.Id, new UpdateTemplateRequest(
            (long)Context.User.Id, newName, title, description, duration, location, image,
            recurrence, ClearRecurrence: repeat == RepeatChoice.None));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync($"✏️ Updated template **{result.Value.Name}**.", ephemeral: true);
    }

    /// <summary>Deletes a template (creator or server manager).</summary>
    /// <param name="name">Template name (or fragment), or an autocomplete-picked template id.</param>
    [SlashCommand("delete", "Delete a template (creator or manager)")]
    public async Task DeleteAsync(
        [Summary("name", "The template to delete"), Autocomplete(typeof(TemplateNameAutocompleteHandler))] string name)
    {
        await DeferAsync(ephemeral: true);

        var (template, problem) = await TemplateFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (template is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (!CanManage(template))
        {
            await FollowupAsync("Only the template creator or a server manager can delete this template.", ephemeral: true);
            return;
        }

        var result = await api.DeleteTemplateAsync(template.Id);
        await FollowupAsync(
            result.Success ? $"🗑️ Deleted template **{template.Name}**." : $"❌ {result.Error}",
            ephemeral: true);
    }

    /// <summary>Creator-or-ManageGuild check mirroring the API guard.</summary>
    /// <param name="template">The template.</param>
    /// <returns>True for the creator or a ManageGuild holder.</returns>
    private bool CanManage(EventTemplateDto template) =>
        (long)Context.User.Id == template.CreatorId ||
        (Context.User is IGuildUser guildUser && guildUser.GuildPermissions.ManageGuild);
}
