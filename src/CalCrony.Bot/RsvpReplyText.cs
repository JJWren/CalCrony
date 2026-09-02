using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>The ephemeral confirmation an RSVP button click gets back. Pure and static so the
/// wording is directly testable. Single-choice events keep the three texts they always had; an
/// event that allows multiple RSVPs speaks of "your RSVPs" and names the member's other seats,
/// so a click that ADDED a choice never reads as a switch that lost the previous one.</summary>
public static class RsvpReplyText
{
    /// <summary>The text after a seat was taken (the member is now marked on the option).</summary>
    /// <param name="ev">The event as returned by the API AFTER the change.</param>
    /// <param name="option">The option clicked, or null when it vanished under the click.</param>
    /// <param name="userId">The clicking member's Discord id.</param>
    /// <returns>The confirmation text.</returns>
    public static string Marked(EventDto ev, RsvpOptionDto? option, long userId) =>
        ev.AllowMultipleRsvps
            ? $"Added {Choice(option)} to your RSVPs for **{ev.Title}** (<t:{ev.StartsAtUnix}:F>)."
              + AlsoMarked(ev, option, userId)
            : $"You're marked {Choice(option)} for **{ev.Title}** (<t:{ev.StartsAtUnix}:F>).";

    /// <summary>The text after the member was queued on the full attending option.</summary>
    /// <param name="ev">The event as returned by the API AFTER the change.</param>
    /// <param name="option">The option clicked, or null when it vanished under the click.</param>
    /// <param name="userId">The clicking member's Discord id.</param>
    /// <param name="position">The member's zero-based position in the waitlist.</param>
    /// <returns>The confirmation text.</returns>
    public static string Waitlisted(EventDto ev, RsvpOptionDto? option, long userId, int position) =>
        $"{Choice(option)} is full — you're **#{position + 1} on the waitlist** "
        + $"for **{ev.Title}** and will be moved up automatically when a spot frees."
        + (ev.AllowMultipleRsvps ? AlsoMarked(ev, option, userId) : "");

    /// <summary>The text after the member withdrew from the option.</summary>
    /// <param name="ev">The event as returned by the API AFTER the change.</param>
    /// <param name="option">The option clicked, or null when it vanished under the click.</param>
    /// <param name="userId">The clicking member's Discord id.</param>
    /// <returns>The confirmation text.</returns>
    public static string Removed(EventDto ev, RsvpOptionDto? option, long userId)
    {
        if (!ev.AllowMultipleRsvps)
        {
            return $"Removed your RSVP for **{ev.Title}**.";
        }

        var others = Others(ev, option, userId);
        return $"Removed {Choice(option)} from your RSVPs for **{ev.Title}**."
               + (others.Count == 0 ? "" : $" You're still marked: {string.Join(", ", others)}.");
    }

    private static string AlsoMarked(EventDto ev, RsvpOptionDto? option, long userId)
    {
        var others = Others(ev, option, userId);
        return others.Count == 0 ? "" : $" You're also marked: {string.Join(", ", others)}.";
    }

    /// <summary>The member's OTHER rows on the event, as "emote **label**" (a queued one says so),
    /// in RSVP order.</summary>
    private static List<string> Others(EventDto ev, RsvpOptionDto? option, long userId) =>
        [
            .. ev.RsvpsFor(userId)
                .Where(r => r.OptionId != option?.Id)
                .Select(r => (Rsvp: r, Option: ev.Options.FirstOrDefault(o => o.Id == r.OptionId)))
                .Where(pair => pair.Option is not null)
                .Select(pair => Choice(pair.Option) + (pair.Rsvp.Waitlisted ? " (waitlisted)" : "")),
        ];

    private static string Choice(RsvpOptionDto? option) => $"{option?.Emote} **{option?.Label}**";
}
