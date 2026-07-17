using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

[RequireContext(ContextType.Guild)]
public class NotifyModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("notify", "Add a scheduled notification before an event starts")]
    public async Task NotifyAsync(
        [Summary("event", "Event title (or part of it)")] string eventName,
        [Summary("minutes-before", "How many minutes before start to ping")] int minutesBefore,
        [Summary("message", "Optional extra message")] string? message = null,
        [Summary("mention", "Role or user to mention")] IMentionable? mention = null,
        [Summary("channel", "Channel to ping in (defaults to the event's channel)")] ITextChannel? channel = null)
    {
        await DeferAsync(ephemeral: true);

        var (ev, problem) = await EventFinder.FindSingleAsync(api, (long)Context.Guild.Id, eventName);
        if (ev is null)
        {
            await FollowupAsync(problem!, ephemeral: true);
            return;
        }

        var result = await api.CreateNotificationAsync(ev.Id, new CreateEventNotificationRequest(
            minutesBefore,
            message,
            mention?.Mention,
            (long?)channel?.Id));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"🔔 Notification added: **{ev.Title}** will be announced {minutesBefore} min before start.",
            ephemeral: true);
    }
}
