using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>How signup restrictions render: one 🔒 line when every option shares a set, one per
/// restricted option otherwise, a poll header chip — and buttons that stay enabled throughout
/// (disabled-with-explanation on click, never hidden).</summary>
public class RoleRestrictionEmbedTests
{
    private static readonly RoleRefDto Raiders = new(9, "Raiders");
    private static readonly RoleRefDto Officers = new(8, null);

    private static EventDto Event(params RsvpOptionDto[] options) => new(
        Guid.NewGuid(), 1, 2, "Restricted Raid", null, DateTimeOffset.UtcNow.AddHours(3), "UTC", 90,
        3, null, null, null, EventStatus.Scheduled, options, []);

    [Fact]
    public void A_restriction_every_option_shares_is_one_line()
    {
        var ev = Event(
            new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null, IsAttending: true, AllowedRoles: [Raiders, Officers]),
            new RsvpOptionDto(Guid.NewGuid(), "❌", "Out", 1, null, AllowedRoles: [Officers, Raiders]));

        var description = EventEmbedBuilder.Build(ev).Description;

        Assert.Contains("🔒 Limited to <@&9>, <@&8>", description);
        Assert.DoesNotContain("only", description);
    }

    [Fact]
    public void Differing_restrictions_get_a_line_per_restricted_option_and_unrestricted_events_none()
    {
        var mixed = Event(
            new RsvpOptionDto(Guid.NewGuid(), "🛡️", "Tank", 0, null, IsAttending: true, AllowedRoles: [Raiders]),
            new RsvpOptionDto(Guid.NewGuid(), "❌", "Out", 1, null));
        Assert.Contains("🔒 Tank — <@&9> only", EventEmbedBuilder.Build(mixed).Description);

        var open = Event(new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null, IsAttending: true));
        Assert.DoesNotContain("🔒", EventEmbedBuilder.Build(open).Description);

        // Buttons are never hidden or disabled by a restriction.
        var row = Assert.Single(EventEmbedBuilder.BuildComponents(mixed).Components.OfType<Discord.ActionRowComponent>());
        Assert.All(row.Components.OfType<Discord.ButtonComponent>(), b => Assert.False(b.IsDisabled));
    }

    [Fact]
    public void A_restricted_poll_shows_the_chip_and_keeps_its_buttons()
    {
        var options = new List<PollOptionDto>
        {
            new(Guid.NewGuid(), "a", null, null, 0, 0),
            new(Guid.NewGuid(), "b", null, null, 1, 0),
        };
        var poll = new PollDto(
            Guid.NewGuid(), 1, 2, "Raid night?", false, false, false, false, 3, 4,
            PollStatus.Open, null, null, "UTC", null, options, [], AllowedRoles: [Raiders]);

        Assert.Contains("🔒 <@&9> only", PollEmbedBuilder.Build(poll).Description);
        var row = Assert.Single(PollEmbedBuilder.BuildComponents(poll).Components.OfType<Discord.ActionRowComponent>());
        Assert.Equal(2, row.Components.Count);

        var open = poll with { AllowedRoles = [] };
        Assert.DoesNotContain("🔒", PollEmbedBuilder.Build(open).Description);
    }
}
