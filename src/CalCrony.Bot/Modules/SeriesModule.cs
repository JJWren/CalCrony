using System.Text;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

[RequireContext(ContextType.Guild)]
[Group("series", "Manage repeating events")]
public class SeriesModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("skip", "Skip the next occurrence of a repeating event")]
    public async Task SkipAsync(
        [Summary("name", "Event title (or part of it)")] string name)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (ev.RecurrenceSummary is null)
        {
            await FollowupAsync($"**{ev.Title}** doesn't repeat.", ephemeral: true);
            return;
        }

        if (!CanManage(ev))
        {
            await FollowupAsync("Only the event creator or a server manager can change this event.", ephemeral: true);
            return;
        }

        var result = await api.SkipOccurrenceAsync(ev.Id);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        // The old embed's removal and the replacement's post both ride the ~15s outbox.
        var reply = result.Value.NextEvent is { } next
            ? $"⏭️ Skipped **{ev.Title}** — next occurrence is <t:{next.StartsAtUnix}:F>; its embed posts shortly."
            : $"⏭️ Skipped **{ev.Title}** — that was the final occurrence, so the series has ended.";
        await FollowupAsync(reply, ephemeral: true);
    }

    [SlashCommand("stop", "Stop a repeating event from creating future occurrences")]
    public async Task StopAsync(
        [Summary("name", "Event title (or part of it)")] string name)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (ev.SeriesId is not Guid seriesId || ev.RecurrenceSummary is null)
        {
            await FollowupAsync($"**{ev.Title}** doesn't repeat.", ephemeral: true);
            return;
        }

        if (!CanManage(ev))
        {
            await FollowupAsync("Only the event creator or a server manager can change this event.", ephemeral: true);
            return;
        }

        var result = await api.StopSeriesAsync(seriesId);
        if (!result.Success)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        // Re-render in place so the 🔁 line drops immediately (bot callers skip the outbox sync).
        var refreshed = await api.GetEventAsync(ev.Id);
        if (refreshed.Success && refreshed.Value is not null)
        {
            await TryUpdateMessageAsync(refreshed.Value);
        }

        await FollowupAsync(
            $"🛑 **{ev.Title}** will no longer repeat — the upcoming occurrence still happens. Use `/delete` to remove it too.",
            ephemeral: true);
    }

    [SlashCommand("info", "Show a repeating event's schedule and progress")]
    public async Task InfoAsync(
        [Summary("name", "Event title (or part of it)")] string name)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        if (ev.SeriesId is not Guid seriesId)
        {
            await FollowupAsync($"**{ev.Title}** doesn't repeat.", ephemeral: true);
            return;
        }

        var result = await api.GetSeriesAsync(seriesId);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var series = result.Value;
        var lines = new StringBuilder();
        lines.AppendLine($"🔁 {series.Summary}");

        // A stopped series can still have its final occurrence scheduled — keep showing when it
        // happens rather than just "ended".
        if (series.LiveEventId is Guid liveId)
        {
            var live = liveId == ev.Id ? ev : (await api.GetEventAsync(liveId)).Value;
            if (live is not null)
            {
                var label = series.Ended ? "Final occurrence" : "Next";
                lines.AppendLine($"{label}: <t:{live.StartsAtUnix}:F> (<t:{live.StartsAtUnix}:R>)");
            }
        }

        if (series.Ended)
        {
            lines.AppendLine("Series ended — no new occurrences will be scheduled.");
        }

        lines.AppendLine($"Occurrences so far: {series.OccurrenceCount}{(series.MaxOccurrences is int max ? $" of {max}" : "")}");
        if (series.UntilDate is not null)
        {
            lines.AppendLine($"Until: {series.UntilDate}");
        }

        lines.AppendLine($"Channel: <#{series.ChannelId}>");

        var embed = new EmbedBuilder()
            .WithTitle(series.Title)
            .WithColor(new Color(0x57, 0xB9, 0xE2))
            .WithDescription(lines.ToString())
            .WithFooter($"Series {series.Id}")
            .Build();
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private bool CanManage(EventDto ev) =>
        (long)Context.User.Id == ev.CreatorId ||
        (Context.User is IGuildUser guildUser && guildUser.GuildPermissions.ManageGuild);

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
}
