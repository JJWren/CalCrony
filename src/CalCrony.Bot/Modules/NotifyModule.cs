using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/notify — scheduled pings before an event starts.</summary>
/// <param name="api">The CalCrony API client.</param>
[RequireContext(ContextType.Guild)]
public class NotifyModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Adds a pre-event notification to an event found by name/picker.</summary>
    /// <param name="eventName">Event title (or fragment), or an autocomplete-picked event id.</param>
    /// <param name="minutesBefore">How many minutes before start the ping fires.</param>
    /// <param name="message">Optional message text.</param>
    /// <param name="mention">Optional role/user mention to include.</param>
    /// <param name="channel">Target text channel (defaults to the current one).</param>
    [SlashCommand("notify", "Add a scheduled notification before an event starts")]
    public async Task NotifyAsync(
        [Summary("event", "Event title (or part of it)"), Autocomplete(typeof(EventNameAutocompleteHandler))] string eventName,
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
