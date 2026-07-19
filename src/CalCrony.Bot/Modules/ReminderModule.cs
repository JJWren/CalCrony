using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/remind — one-off reminders in the current channel.</summary>
[RequireContext(ContextType.Guild)]
public class ReminderModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Schedules a reminder from natural-language text.</summary>
    [SlashCommand("remind", "Create a one-off reminder in this channel")]
    public async Task RemindAsync(
        [Summary("when", "e.g. \"in 30 minutes\" or \"tomorrow 9am\"")] string when,
        [Summary("about", "What to remind you of")] string about)
    {
        await DeferAsync(ephemeral: true);

        var result = await api.CreateReminderAsync(new CreateReminderRequest(
            (long)Context.Guild.Id, (long)Context.User.Id, (long)Context.Channel.Id, when, about));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"⏰ I'll remind you here <t:{result.Value.FireAtUnix}:R> (<t:{result.Value.FireAtUnix}:F>): {about}",
            ephemeral: true);
    }
}
