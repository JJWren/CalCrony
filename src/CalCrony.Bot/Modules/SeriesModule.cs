using System.Text;
using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>End-condition choices for /series edit.</summary>
public enum SeriesEndsChoice
{
    [ChoiceDisplay("never")] Never,
    [ChoiceDisplay("on a date")] Until,
    [ChoiceDisplay("after a number of times")] Count,
}

/// <summary>Like EventModule.RepeatChoice plus a "doesn't repeat" option that stops the series —
/// so converting a series to a one-off is reachable from the edit surface, not just /series stop.</summary>
public enum SeriesRepeatChoice
{
    [ChoiceDisplay("doesn't repeat (stop the series)")] DoesntRepeat,
    [ChoiceDisplay("daily")] Daily,
    [ChoiceDisplay("weekly")] Weekly,
    [ChoiceDisplay("monthly (same date)")] MonthlySameDate,
    [ChoiceDisplay("monthly (nth weekday)")] MonthlyNthWeekday,
}

/// <summary>/series — manage repeating events: edit the rule, skip, stop, and inspect.</summary>
[RequireContext(ContextType.Guild)]
[Group("series", "Manage repeating events")]
public class SeriesModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Edits a series' rule or end condition; can revive an ended series or stop it via "doesn't repeat".</summary>
    [SlashCommand("edit", "Change how a repeating event repeats or ends (can revive an ended series)")]
    public async Task EditAsync(
        [Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name,
        [Summary("repeat", "New repeat rule — or \"doesn't repeat\" to stop the series")] SeriesRepeatChoice? repeat = null,
        [Summary("repeat-every", "New interval: every N days/weeks/months (1-12)"), MinValue(1), MaxValue(12)] int? repeatEvery = null,
        [Summary("ends", "How the series ends — or just pass until/count directly")] SeriesEndsChoice? ends = null,
        [Summary("until", "Last date it repeats, e.g. \"Aug 30\" (implies ends: on a date)")] string? until = null,
        [Summary("count", "Total occurrences including past ones (2-500)"), MinValue(2), MaxValue(500)] int? count = null)
    {
        await DeferAsync(ephemeral: true);

        // includePast so a fully-ended series (all occurrences in the past) is still reachable.
        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, name, includePast: true);
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

        if (!CanManage(ev))
        {
            await FollowupAsync("Only the event creator or a server manager can change this event.", ephemeral: true);
            return;
        }

        if (repeat == SeriesRepeatChoice.DoesntRepeat)
        {
            if (repeatEvery is not null || ends is not null || until is not null || count is not null)
            {
                await FollowupAsync("\"doesn't repeat\" stops the series — drop the other repeat options.", ephemeral: true);
                return;
            }

            var stop = await api.StopSeriesAsync(seriesId);
            if (!stop.Success)
            {
                await FollowupAsync($"❌ {stop.Error}", ephemeral: true);
                return;
            }

            var refreshedStop = await api.GetEventAsync(ev.Id);
            if (refreshedStop.Success && refreshedStop.Value is not null)
            {
                await TryUpdateMessageAsync(refreshedStop.Value);
            }

            await FollowupAsync(
                $"🛑 **{ev.Title}** will no longer repeat — the upcoming occurrence still happens. Use `/delete` to remove it too.",
                ephemeral: true);
            return;
        }

        if (until is not null && count is not null)
        {
            await FollowupAsync("Choose either an end date or a number of times, not both.", ephemeral: true);
            return;
        }

        // Contradictory combinations get an immediate, specific message instead of an API 400.
        if (ends == SeriesEndsChoice.Never && (until is not null || count is not null))
        {
            await FollowupAsync("`ends: never` can't be combined with `until` or `count`.", ephemeral: true);
            return;
        }

        if (ends == SeriesEndsChoice.Until && count is not null)
        {
            await FollowupAsync("`ends: on a date` takes `until`, not `count`.", ephemeral: true);
            return;
        }

        if (ends == SeriesEndsChoice.Count && until is not null)
        {
            await FollowupAsync("`ends: after a number of times` takes `count`, not `until`.", ephemeral: true);
            return;
        }

        if (ends == SeriesEndsChoice.Until && until is null)
        {
            await FollowupAsync("Pass `until` with the date the series should stop repeating.", ephemeral: true);
            return;
        }

        if (ends == SeriesEndsChoice.Count && count is null)
        {
            await FollowupAsync("Pass `count` with how many times the series should run in total.", ephemeral: true);
            return;
        }

        var seriesResult = await api.GetSeriesAsync(seriesId);
        if (!seriesResult.Success || seriesResult.Value is null)
        {
            await FollowupAsync($"❌ {seriesResult.Error}", ephemeral: true);
            return;
        }

        var wasEnded = seriesResult.Value.Ended;
        if (repeat is null && repeatEvery is null && ends is null && until is null && count is null && !wasEnded)
        {
            await FollowupAsync("Nothing to change — pass at least one option.", ephemeral: true);
            return;
        }

        var (unit, mode) = repeat switch
        {
            SeriesRepeatChoice.Daily => ((RecurrenceUnit?)RecurrenceUnit.Day, (MonthlyMode?)MonthlyMode.DayOfMonth),
            SeriesRepeatChoice.Weekly => (RecurrenceUnit.Week, MonthlyMode.DayOfMonth),
            SeriesRepeatChoice.MonthlySameDate => (RecurrenceUnit.Month, MonthlyMode.DayOfMonth),
            SeriesRepeatChoice.MonthlyNthWeekday => (RecurrenceUnit.Month, MonthlyMode.NthWeekday),
            _ => (null, null),
        };
        var end = ends switch
        {
            SeriesEndsChoice.Never => SeriesEndChoice.Never,
            SeriesEndsChoice.Until => SeriesEndChoice.Until,
            SeriesEndsChoice.Count => SeriesEndChoice.Count,
            _ when until is not null => SeriesEndChoice.Until,
            _ when count is not null => SeriesEndChoice.Count,
            _ => SeriesEndChoice.Keep,
        };

        var result = await api.UpdateSeriesAsync(seriesId, new UpdateSeriesRequest(
            unit, repeatEvery, mode, end, until, count));
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var updated = result.Value;
        if (updated.LiveEventId is Guid liveId)
        {
            var refreshed = await api.GetEventAsync(liveId);
            if (refreshed.Success && refreshed.Value is not null)
            {
                await TryUpdateMessageAsync(refreshed.Value);
            }
        }

        var resumedNote = (wasEnded, updated.LiveEventId) switch
        {
            (false, _) => "",
            (true, not null) => " Series resumed — the scheduled occurrence continues it.",
            (true, null) => " Series resumed — the next occurrence posts within ~15 seconds.",
        };
        await FollowupAsync($"🔁 **{updated.Title}** — {updated.Summary}.{resumedNote}", ephemeral: true);
    }

    /// <summary>Cancels the next occurrence and immediately schedules the following one.</summary>
    [SlashCommand("skip", "Skip the next occurrence of a repeating event")]
    public async Task SkipAsync(
        [Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name)
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

    /// <summary>Stops the series; the upcoming occurrence still happens.</summary>
    [SlashCommand("stop", "Stop a repeating event from creating future occurrences")]
    public async Task StopAsync(
        [Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name)
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

    /// <summary>Shows the series' schedule, progress, and next (or final) occurrence.</summary>
    [SlashCommand("info", "Show a repeating event's schedule and progress")]
    public async Task InfoAsync(
        [Summary("name", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string name)
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

    /// <summary>Creator-or-ManageGuild check mirroring the API guard.</summary>
    private bool CanManage(EventDto ev) =>
        (long)Context.User.Id == ev.CreatorId ||
        (Context.User is IGuildUser guildUser && guildUser.GuildPermissions.ManageGuild);

    /// <summary>Re-renders the posted embed in place; tolerates a manually deleted message.</summary>
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
