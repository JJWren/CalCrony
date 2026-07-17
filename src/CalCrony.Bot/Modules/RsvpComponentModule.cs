using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot.Modules;

public class RsvpComponentModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("rsvp:*:*")]
    public async Task RsvpAsync(string eventIdRaw, string optionIdRaw)
    {
        await DeferAsync(ephemeral: true);

        if (!Guid.TryParse(eventIdRaw, out var eventId) || !Guid.TryParse(optionIdRaw, out var optionId))
        {
            await FollowupAsync("This RSVP button is broken — the event may have been recreated.", ephemeral: true);
            return;
        }

        var userId = (long)Context.User.Id;
        var current = await api.GetEventAsync(eventId);
        if (!current.Success || current.Value is null)
        {
            await FollowupAsync("This event no longer exists.", ephemeral: true);
            return;
        }

        // Clicking your current choice clears it; clicking another switches to it.
        var alreadyOnOption = current.Value.Rsvps.Any(r => r.UserId == userId && r.OptionId == optionId);
        var result = alreadyOnOption
            ? await api.DeleteRsvpAsync(eventId, userId)
            : await api.PutRsvpAsync(eventId, userId, new RsvpRequest(optionId));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var ev = result.Value;
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.Message.ModifyAsync(m =>
            {
                m.Embed = EventEmbedBuilder.Build(ev);
                m.Components = EventEmbedBuilder.BuildComponents(ev);
            });
        }

        var option = ev.Options.FirstOrDefault(o => o.Id == optionId);
        await FollowupAsync(
            alreadyOnOption
                ? $"Removed your RSVP for **{ev.Title}**."
                : $"You're marked {option?.Emote} **{option?.Label}** for **{ev.Title}** (<t:{ev.StartsAtUnix}:F>).",
            ephemeral: true);
    }
}
