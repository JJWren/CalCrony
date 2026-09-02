using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot.Modules;

/// <summary>Handles event RSVP buttons.</summary>
/// <param name="api">The CalCrony API client.</param>
public class RsvpComponentModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Sets or clears the clicker's RSVP and re-renders the embed.</summary>
    /// <param name="eventIdRaw">The event id from the component custom id.</param>
    /// <param name="optionIdRaw">The option id from the component custom id.</param>
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

        // Clicking a choice you hold clears THAT choice; clicking another switches to it (single
        // choice, the default) or adds it (an event allowing multiple RSVPs).
        var alreadyOnOption = current.Value.Rsvps.Any(r => r.UserId == userId && r.OptionId == optionId);

        // Signup restriction, checked live before the API call (ADR 0004): the socket cache is
        // Discord's own answer, so a refused click never reaches the API. Withdrawing is never gated.
        if (!alreadyOnOption
            && current.Value.Options.FirstOrDefault(o => o.Id == optionId) is { IsRestricted: true } restricted
            && RoleRestrictionCheck.Denied(Context.User, current.Value.CreatorId, restricted.AllowedRoles, out var effective))
        {
            await FollowupAsync(RoleRestrictionCheck.Refusal($"**{restricted.Label}**", effective), ephemeral: true);
            return;
        }

        // The restriction the live check ran against rides along, so an edit that restricted
        // the option in between makes the API refuse and this click re-read rather than bypass.
        var checkedRoles = current.Value.Options.FirstOrDefault(o => o.Id == optionId)?.AllowedRoles ?? [];
        // The withdrawal is option-scoped in every mode: in single-choice mode the one row IS on
        // this option, and with multiple RSVPs the member's other seats must survive the click.
        var result = alreadyOnOption
            ? await api.DeleteRsvpOptionAsync(eventId, userId, optionId)
            : await api.PutRsvpAsync(eventId, userId, new RsvpRequest(optionId, [.. checkedRoles.Select(r => r.Id)]));

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
        // A full attending option queues instead of seating — tell the clicker where they stand.
        // Judged on the row for THIS option: with multiple RSVPs a member queued elsewhere can
        // still land a seat here, and that must not read as "still waitlisted".
        var waitlistPosition = RsvpReplyText.WaitlistPosition(ev, userId, optionId) ?? -1;
        var confirmation = (alreadyOnOption, waitlistPosition) switch
        {
            (true, _) => RsvpReplyText.Removed(ev, option, userId),
            (false, >= 0) => RsvpReplyText.Waitlisted(ev, option, userId, waitlistPosition),
            _ => RsvpReplyText.Marked(ev, option, userId),
        };
        await FollowupAsync(confirmation, ephemeral: true);

        // The first time someone lands a SEAT on the attending option, offer DM reminders — once,
        // ever, right here as an ephemeral prompt. Never an unsolicited DM, and the API decides
        // whether the offer is still owed (it isn't if they already opted in elsewhere).
        if (!alreadyOnOption && waitlistPosition < 0 && option?.IsAttending == true)
        {
            var offer = await api.OfferDmRemindersAsync(userId);
            if (offer.Success && offer.Value?.Offer == true)
            {
                await FollowupAsync(DmReminderPrompt.Text, components: DmReminderPrompt.Buttons(), ephemeral: true);
            }
        }
    }
}
