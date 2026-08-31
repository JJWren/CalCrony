using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class EventEmbedBuilderTests
{
    private static EventDto SampleEvent()
    {
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null);
        var notGoing = new RsvpOptionDto(Guid.NewGuid(), "❌", "Not going", 1, null);
        var maybe = new RsvpOptionDto(Guid.NewGuid(), "🤔", "Maybe", 2, 5);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Raid Night", "Bring snacks",
            DateTimeOffset.UtcNow.AddHours(3), "America/Chicago", 90,
            3, null, "Voice chat", null, EventStatus.Scheduled,
            [going, notGoing, maybe],
            [new RsvpDto(42, going.Id), new RsvpDto(43, going.Id)]);
    }

    [Fact]
    public void Embed_contains_title_time_and_option_fields()
    {
        var ev = SampleEvent();

        var embed = EventEmbedBuilder.Build(ev);

        Assert.Equal("Raid Night", embed.Title);
        Assert.Contains($"<t:{ev.StartsAtUnix}:F>", embed.Description);
        Assert.Equal(3, embed.Fields.Length);
        Assert.Contains("(2)", embed.Fields[0].Name);
        Assert.Contains("<@42>", embed.Fields[0].Value);
        Assert.Contains("(0/5)", embed.Fields[2].Name);
        Assert.Equal("—", embed.Fields[2].Value);
    }

    [Fact]
    public void Components_have_one_button_per_option()
    {
        var ev = SampleEvent();

        var components = EventEmbedBuilder.BuildComponents(ev);

        var row = Assert.Single(components.Components.OfType<Discord.ActionRowComponent>());
        Assert.Equal(3, row.Components.Count);
    }
    [Fact]
    public void Recurrence_summary_renders_and_absence_hides_the_line()
    {
        var oneOff = SampleEvent();
        Assert.DoesNotContain("🔁", EventEmbedBuilder.Build(oneOff).Description);

        var repeating = oneOff with { SeriesId = Guid.NewGuid(), RecurrenceSummary = "Repeats weekly on Friday" };
        var description = EventEmbedBuilder.Build(repeating).Description;
        Assert.Contains("🔁 Repeats weekly on Friday", description);
    }

    private static EventDto WaitlistedEvent()
    {
        var raider = new RsvpOptionDto(Guid.NewGuid(), "⚔️", "Raider", 0, 2, IsAttending: true);
        var declined = new RsvpOptionDto(Guid.NewGuid(), "❌", "Out", 1, null);
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Capped Raid", null,
            DateTimeOffset.UtcNow.AddHours(6), "UTC", 60,
            3, null, null, null, EventStatus.Scheduled,
            [raider, declined],
            [
                new RsvpDto(42, raider.Id),
                new RsvpDto(43, raider.Id),
                new RsvpDto(44, raider.Id, Waitlisted: true),
                new RsvpDto(45, raider.Id, Waitlisted: true),
            ]);
    }

    [Fact]
    public void Waitlisted_members_get_their_own_field_and_no_seat()
    {
        var embed = EventEmbedBuilder.Build(WaitlistedEvent());

        // Attending field counts seats only; the waitlist field follows it in queue order.
        Assert.Contains("(2/2)", embed.Fields[0].Name);
        Assert.DoesNotContain("<@44>", embed.Fields[0].Value);
        Assert.Equal("⏳ Waitlist (2)", embed.Fields[1].Name);
        Assert.Equal("<@44>\n<@45>", embed.Fields[1].Value);
        Assert.Equal(3, embed.Fields.Length);
    }

    [Fact]
    public void Cutoff_renders_upcoming_then_closed_and_freezes_the_buttons()
    {
        var closesAt = DateTimeOffset.UtcNow.AddHours(2);
        var ev = SampleEvent() with { RsvpClosesAtUtc = closesAt };

        var before = EventEmbedBuilder.Build(ev, closesAt.AddHours(-1));
        Assert.Contains($"🔒 RSVPs close <t:{closesAt.ToUnixTimeSeconds()}:F>", before.Description);

        var after = EventEmbedBuilder.Build(ev, closesAt.AddMinutes(1));
        Assert.Contains("🔒 RSVPs are closed", after.Description);

        var openRow = Assert.Single(
            EventEmbedBuilder.BuildComponents(ev, closesAt.AddHours(-1)).Components.OfType<Discord.ActionRowComponent>());
        Assert.All(openRow.Components.OfType<Discord.ButtonComponent>(), b => Assert.False(b.IsDisabled));

        var closedRow = Assert.Single(
            EventEmbedBuilder.BuildComponents(ev, closesAt.AddMinutes(1)).Components.OfType<Discord.ActionRowComponent>());
        Assert.All(closedRow.Components.OfType<Discord.ButtonComponent>(), b => Assert.True(b.IsDisabled));
    }

    [Fact]
    public void More_than_five_options_wrap_onto_a_second_button_row()
    {
        var options = Enumerable.Range(0, 7)
            .Select(i => new RsvpOptionDto(Guid.NewGuid(), "🔹", $"Option {i}", i, null))
            .ToList();
        var ev = SampleEvent() with { Options = options };

        var rows = EventEmbedBuilder.BuildComponents(ev).Components.OfType<Discord.ActionRowComponent>().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Components.Count);
        Assert.Equal(2, rows[1].Components.Count);
    }

    [Fact]
    public void Huge_member_lists_stay_inside_discord_limits_with_an_omitted_count()
    {
        // 19-char mentions: 120 seated (~2.4k chars raw) and 100 waitlisted (~2k raw) would blow
        // Discord's 1024-per-field and 6000-total caps without the bounded renderer.
        var going = new RsvpOptionDto(Guid.NewGuid(), "✅", "Going", 0, null, IsAttending: true);
        var rsvps = Enumerable.Range(0, 120)
            .Select(i => new RsvpDto(1_000_000_000_000_000 + i, going.Id))
            .Concat(Enumerable.Range(0, 100)
                .Select(i => new RsvpDto(2_000_000_000_000_000 + i, going.Id, Waitlisted: true)))
            .ToList();
        var ev = SampleEvent() with { Options = [going], Rsvps = rsvps };

        var embed = EventEmbedBuilder.Build(ev);

        Assert.All(embed.Fields, f => Assert.InRange(f.Value.Length, 1, 1024));
        Assert.True(embed.Length <= 6000);
        Assert.Contains("(120)", embed.Fields[0].Name); // full counts survive the truncation
        Assert.EndsWith("more", embed.Fields[0].Value);
        Assert.Equal("⏳ Waitlist (100)", embed.Fields[1].Name);
        Assert.EndsWith("more", embed.Fields[1].Value);
        // The first entries still render before the marker.
        Assert.Contains("<@1000000000000000>", embed.Fields[0].Value);
        Assert.Contains("<@2000000000000000>", embed.Fields[1].Value);
    }

    [Fact]
    public void Role_note_names_the_attending_option()
    {
        var ev = WaitlistedEvent() with { AttendeeRoleId = 777 };

        Assert.Contains("🏷️ Raider grants <@&777>", EventEmbedBuilder.Build(ev).Description);
    }
}
