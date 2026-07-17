using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

[RequireContext(ContextType.Guild)]
public class EventModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("create", "Create an event")]
    public async Task CreateAsync(
        [Summary(description: "Event title")] string title,
        [Summary("when", "When it starts, e.g. \"tomorrow 6pm\" or \"in 5 hours\"")] string when,
        [Summary(description: "Event description")] string? description = null,
        [Summary("duration", "Duration in minutes")] int? duration = null,
        [Summary(description: "Channel to post the event in (defaults to here)")] ITextChannel? channel = null,
        [Summary(description: "Where the event happens")] string? location = null,
        [Summary("image", "Image URL for the event embed")] string? image = null)
    {
        await DeferAsync(ephemeral: true);

        var targetChannel = channel ?? Context.Channel as ITextChannel;
        if (targetChannel is null)
        {
            await FollowupAsync("Events can only be created in text channels.", ephemeral: true);
            return;
        }

        var result = await api.CreateEventAsync(
            (long)Context.Guild.Id,
            new CreateEventRequest(
                (long)Context.User.Id, title, when, (long)targetChannel.Id,
                description, duration, location, image));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var ev = result.Value;
        var message = await targetChannel.SendMessageAsync(
            embed: EventEmbedBuilder.Build(ev),
            components: EventEmbedBuilder.BuildComponents(ev));
        await api.SetMessageAsync(ev.Id, new SetEventMessageRequest((long)targetChannel.Id, (long)message.Id));

        await FollowupAsync(
            $"✅ **{ev.Title}** created in {targetChannel.Mention} for <t:{ev.StartsAtUnix}:F>.",
            ephemeral: true);
    }

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

    [SlashCommand("delete", "Delete an event you created")]
    public async Task DeleteAsync(
        [Summary("name", "Event title (or part of it)")] string name)
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
        await FollowupAsync($"🗑️ Deleted **{ev.Title}**.", ephemeral: true);
    }

    [SlashCommand("edit", "Edit an event you created")]
    public async Task EditAsync(
        [Summary("name", "Event title (or part of it)")] string name,
        [Summary(description: "New title")] string? title = null,
        [Summary("when", "New start time, e.g. \"saturday 7pm\"")] string? when = null,
        [Summary(description: "New description")] string? description = null,
        [Summary("duration", "New duration in minutes")] int? duration = null,
        [Summary(description: "New location")] string? location = null,
        [Summary("image", "New image URL")] string? image = null)
    {
        await DeferAsync(ephemeral: true);

        if (title is null && when is null && description is null && duration is null && location is null && image is null)
        {
            await FollowupAsync("Nothing to change — pass at least one field.", ephemeral: true);
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

        var result = await api.UpdateEventAsync(ev.Id, new UpdateEventRequest(
            (long)Context.User.Id, title, when, description, duration, location, image));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await TryUpdateMessageAsync(result.Value);
        await FollowupAsync($"✏️ Updated **{result.Value.Title}**.", ephemeral: true);
    }

    private bool CanManage(EventDto ev) =>
        (long)Context.User.Id == ev.CreatorId ||
        (Context.User is IGuildUser guildUser && guildUser.GuildPermissions.ManageGuild);

    private async Task<(EventDto? Event, string? Problem)> FindSingleEventAsync(string name)
    {
        var result = await api.ListEventsAsync((long)Context.Guild.Id, limit: 25);
        if (!result.Success || result.Value is null)
        {
            return (null, $"❌ {result.Error}");
        }

        var matches = result.Value
            .Where(e => e.Title.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => (null, $"No upcoming event matching \"{name}\"."),
            1 => (matches[0], null),
            _ => (null, $"Multiple events match \"{name}\": {string.Join(", ", matches.Select(m => $"**{m.Title}**"))}. Be more specific."),
        };
    }

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
