using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>The one-time DM-reminder opt-in prompt (shown once, ever, after a user's first seated
/// attending RSVP) and its two buttons. Only the user's own click turns the preference on.</summary>
/// <param name="api">The CalCrony API client.</param>
public class DmReminderComponentModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Applies the user's choice and replaces the prompt with a confirmation.</summary>
    /// <param name="choice">"on" or "off" from the component custom id.</param>
    [ComponentInteraction("dmrem:*")]
    public async Task ChooseAsync(string choice)
    {
        await DeferAsync(); // update-defer: the prompt itself is edited in place below

        var userId = (long)Context.User.Id;
        var enable = choice == "on";
        var current = await api.GetUserSettingsAsync(userId);
        if (!current.Success || current.Value is null)
        {
            await KeepPromptWithErrorAsync($"Couldn't load your settings: {current.Error}");
            return;
        }

        // Read-modify-write so the timezone/theme/confirmation settings ride along untouched.
        var result = await api.PutUserSettingsAsync(userId, current.Value with { DmReminders = enable });
        if (!result.Success)
        {
            await KeepPromptWithErrorAsync(result.Error ?? "unknown error");
            return;
        }

        await ReplacePromptAsync(enable
            ? "🔔 DM reminders are **on** for events you're attending. Turn them off any time with `/settings dm-reminders enabled:false`."
            : "👍 No DMs. You can turn reminders on later with `/settings dm-reminders enabled:true`.");
    }

    /// <summary>A transient failure must not eat the user's only chance to answer: the offer was
    /// already consumed when the prompt was shown, so the buttons stay until a choice is saved
    /// (and the command is named as the fallback).</summary>
    /// <param name="error">The failure to show.</param>
    private Task KeepPromptWithErrorAsync(string error) =>
        ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"{DmReminderPrompt.Text}\n❌ {error} — try again, or use `/settings dm-reminders` any time.";
            m.Components = DmReminderPrompt.Buttons();
        });

    private Task ReplacePromptAsync(string text) =>
        ModifyOriginalResponseAsync(m =>
        {
            m.Content = text;
            m.Components = new ComponentBuilder().Build();
        });
}

/// <summary>The prompt's wording and buttons, shared with the RSVP handler that shows it.</summary>
public static class DmReminderPrompt
{
    /// <summary>The prompt text — says it is off by default, personal, and asked only once.</summary>
    public const string Text =
        "Want a DM before events you're attending start? DM reminders are **off** by default and only you can turn them on — this is the one time we'll ask.";

    /// <summary>The Yes / No buttons.</summary>
    /// <returns>The component rows.</returns>
    public static MessageComponent Buttons() => new ComponentBuilder()
        .WithButton("Yes, DM me reminders", "dmrem:on", ButtonStyle.Primary)
        .WithButton("No thanks", "dmrem:off", ButtonStyle.Secondary)
        .Build();
}
