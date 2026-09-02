using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>Wording of the ephemeral RSVP confirmations (RSVP v2 §3.3): single-choice events keep
/// their three texts verbatim; an event allowing multiple RSVPs says what was added or removed
/// and names the member's other seats.</summary>
public class RsvpReplyTextTests
{
    private const long Me = 42;

    private static readonly RsvpOptionDto Tank = new(Guid.NewGuid(), "🛡️", "Tank", 0, 1, IsAttending: true);
    private static readonly RsvpOptionDto Healer = new(Guid.NewGuid(), "💚", "Healer", 1, null);
    private static readonly RsvpOptionDto Dps = new(Guid.NewGuid(), "⚔️", "DPS", 2, null);

    private static EventDto Event(bool multi, params RsvpDto[] rsvps) => new(
        Guid.NewGuid(), 1, 2, "Raid Night", null,
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), "UTC", 90,
        3, null, null, null, EventStatus.Scheduled,
        [Tank, Healer, Dps], rsvps, AllowMultipleRsvps: multi);

    [Fact]
    public void Single_choice_texts_are_unchanged()
    {
        var marked = Event(multi: false, new RsvpDto(Me, Healer.Id));
        Assert.Equal(
            "You're marked 💚 **Healer** for **Raid Night** (<t:1800000000:F>).",
            RsvpReplyText.Marked(marked, Healer, Me));

        var queued = Event(multi: false, new RsvpDto(7, Tank.Id), new RsvpDto(Me, Tank.Id, Waitlisted: true));
        Assert.Equal(
            "🛡️ **Tank** is full — you're **#1 on the waitlist** for **Raid Night** and will be moved up automatically when a spot frees.",
            RsvpReplyText.Waitlisted(queued, Tank, Me, 0));

        var removed = Event(multi: false);
        Assert.Equal("Removed your RSVP for **Raid Night**.", RsvpReplyText.Removed(removed, Healer, Me));
    }

    [Fact]
    public void Multi_mode_add_names_the_seat_taken_and_the_others_held()
    {
        // First seat: nothing else to mention.
        var first = Event(multi: true, new RsvpDto(Me, Tank.Id));
        Assert.Equal(
            "Added 🛡️ **Tank** to your RSVPs for **Raid Night** (<t:1800000000:F>).",
            RsvpReplyText.Marked(first, Tank, Me));

        // Second seat: the other one is listed, in RSVP order, without the one just taken.
        var second = Event(multi: true, new RsvpDto(Me, Tank.Id), new RsvpDto(Me, Healer.Id));
        Assert.Equal(
            "Added 💚 **Healer** to your RSVPs for **Raid Night** (<t:1800000000:F>). You're also marked: 🛡️ **Tank**.",
            RsvpReplyText.Marked(second, Healer, Me));

        var third = Event(multi: true, new RsvpDto(Me, Tank.Id), new RsvpDto(Me, Healer.Id), new RsvpDto(Me, Dps.Id));
        Assert.EndsWith("You're also marked: 🛡️ **Tank**, 💚 **Healer**.", RsvpReplyText.Marked(third, Dps, Me));
    }

    [Fact]
    public void Multi_mode_removal_names_the_seat_dropped_and_what_is_still_held()
    {
        var stillHolding = Event(multi: true, new RsvpDto(Me, Tank.Id));
        Assert.Equal(
            "Removed 💚 **Healer** from your RSVPs for **Raid Night**. You're still marked: 🛡️ **Tank**.",
            RsvpReplyText.Removed(stillHolding, Healer, Me));

        var nothingLeft = Event(multi: true);
        Assert.Equal(
            "Removed 💚 **Healer** from your RSVPs for **Raid Night**.",
            RsvpReplyText.Removed(nothingLeft, Healer, Me));
    }

    [Fact]
    public void Multi_mode_waitlist_text_is_the_single_choice_one_plus_the_other_seats()
    {
        var queued = Event(multi: true, new RsvpDto(7, Tank.Id), new RsvpDto(Me, Healer.Id), new RsvpDto(Me, Tank.Id, Waitlisted: true));
        Assert.Equal(
            "🛡️ **Tank** is full — you're **#1 on the waitlist** for **Raid Night** and will be moved up automatically when a spot frees. You're also marked: 💚 **Healer**.",
            RsvpReplyText.Waitlisted(queued, Tank, Me, 0));

        // A queued seat elsewhere is named as such, and other members' rows are never mine.
        var queuedElsewhere = Event(multi: true, new RsvpDto(7, Tank.Id), new RsvpDto(Me, Tank.Id, Waitlisted: true), new RsvpDto(Me, Healer.Id), new RsvpDto(8, Dps.Id));
        Assert.EndsWith("You're also marked: 🛡️ **Tank** (waitlisted).", RsvpReplyText.Marked(queuedElsewhere, Healer, Me));
    }

    [Fact]
    public void Waitlist_position_is_judged_on_the_clicked_option_not_on_the_member()
    {
        // Queued for Tank behind 7, seated on Healer.
        var ev = Event(multi: true, new RsvpDto(7, Tank.Id), new RsvpDto(9, Tank.Id, Waitlisted: true), new RsvpDto(Me, Tank.Id, Waitlisted: true), new RsvpDto(Me, Healer.Id));

        Assert.Equal(1, RsvpReplyText.WaitlistPosition(ev, Me, Tank.Id));      // second in the queue
        Assert.Null(RsvpReplyText.WaitlistPosition(ev, Me, Healer.Id));       // that seat is a seat
        Assert.Null(RsvpReplyText.WaitlistPosition(ev, Me, Dps.Id));          // not held at all
        Assert.Null(RsvpReplyText.WaitlistPosition(ev, 7, Tank.Id));          // seated, not queued
    }
}
