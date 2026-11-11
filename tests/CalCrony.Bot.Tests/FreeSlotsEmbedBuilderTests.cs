using CalCrony.Bot;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot.Tests;

/// <summary>The /availability slots reply: range and basis, one line per day, and the members
/// who could not be counted with the reason for each — inside Discord's embed limits at its worst.</summary>
public class FreeSlotsEmbedBuilderTests
{
    private const string Sentence8To11 = "between 8 AM and 11 PM";

    private static readonly DateOnly Thursday = new(2026, 11, 12);

    /// <summary>08:00 on Thursday in America/Chicago (UTC-6).</summary>
    private static readonly DateTimeOffset ThursdayMorning = new(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Title_names_the_target_and_the_first_line_states_range_and_minimum()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(Counted(1)));

        Assert.Equal("Free slots for @GM", embed.Title);
        Assert.StartsWith("🗓️ Thu Nov 12 – Wed Nov 18, 2026 · 60 min or longer\n", embed.Description);
        Assert.Equal("Days are in America/Chicago · times show in your local time", embed.Footer!.Value.Text);
    }

    [Fact]
    public void A_single_day_is_not_written_as_a_range()
    {
        var response = new FreeSlotsResponse(
            "UTC", Thursday, Thursday, 1, [new FreeSlotDayDto(Thursday, [])], [Counted(1)]);

        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 90, response);

        Assert.StartsWith("🗓️ Thu Nov 12, 2026 · 90 min or longer\n", embed.Description);
    }

    [Fact]
    public void A_range_across_new_year_shows_both_years()
    {
        // A bare date already behind us rolls to next year; the year is what makes that visible.
        var first = new DateOnly(2026, 12, 29);
        var response = new FreeSlotsResponse(
            "UTC", first, first.AddDays(6), 1, Days(first, 7), [Counted(1)]);

        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, response);

        Assert.StartsWith("🗓️ Tue Dec 29, 2026 – Mon Jan 4, 2027 · 60 min or longer\n", embed.Description);
    }

    [Fact]
    public void One_member_counted_reads_as_their_own_free_time()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, Week(Counted(1)));

        Assert.Contains($"\nFree on their linked calendar, {Sentence8To11} in their timezone.\n", embed.Description);
    }

    [Fact]
    public void Several_members_counted_read_as_the_time_they_all_share()
    {
        var embed = FreeSlotsEmbedBuilder.Build(
            "@GM", 60, Week(Counted(1), Counted(2), Counted(3), Member(4, FreeSlotMemberState.NotCounted), Member(5, FreeSlotMemberState.Error)));

        Assert.Contains(
            $"\nWhen all 3 counted members are free on their linked calendars, each {Sentence8To11} in their own timezone.\n",
            embed.Description);
    }

    [Fact]
    public void One_counted_member_of_several_is_said_plainly()
    {
        var embed = FreeSlotsEmbedBuilder.Build(
            "@GM", 60, Week(Counted(1), Member(2, FreeSlotMemberState.NotCounted), Member(3, FreeSlotMemberState.NotCounted)));

        Assert.Contains(
            $"\nWhen the one counted member is free on their linked calendar, {Sentence8To11} in their timezone.\n",
            embed.Description);
    }

    [Fact]
    public void When_nobody_is_counted_the_reply_says_so_and_lists_no_days()
    {
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(6), 0, [],
            [Member(1, FreeSlotMemberState.NotCounted), Member(2, FreeSlotMemberState.ReconnectRequired)]);

        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, response);

        Assert.Equal(
            "🗓️ Thu Nov 12 – Wed Nov 18, 2026 · 60 min or longer\n" +
            "Nobody could be counted, so there are no slots to list. " +
            "A member is counted once they link a calendar with `/calendar connect`.",
            embed.Description);

        // Why each member was left out is still said.
        var field = Assert.Single(embed.Fields);
        Assert.Equal("⚪ No calendar linked — <@1>\n🔄 Needs to reconnect with `/calendar connect` — <@2>", field.Value);
    }

    [Fact]
    public void Each_day_lists_its_slots_in_order_as_timestamps_or_says_nothing_is_free()
    {
        var morning = Slot(ThursdayMorning, hours: 2);
        var evening = Slot(ThursdayMorning.AddHours(10), hours: 5);
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(1), 1,
            [new FreeSlotDayDto(Thursday, [morning, evening]), new FreeSlotDayDto(Thursday.AddDays(1), [])],
            [Counted(1)]);

        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, response);

        // Times are Discord timestamps, so every reader sees them in their own local time; the
        // day labels are literal, cut in the zone the footer names.
        Assert.EndsWith(
            $"\n\n**Thu Nov 12** · <t:{morning.StartUnix}:t>–<t:{morning.EndUnix}:t> · <t:{evening.StartUnix}:t>–<t:{evening.EndUnix}:t>" +
            "\n**Fri Nov 13** · nothing free",
            embed.Description);
    }

    [Theory]
    [InlineData(6, null)]
    [InlineData(7, "+1 more")]
    [InlineData(20, "+14 more")]
    public void A_day_shows_at_most_six_slots_then_counts_the_rest(int slotCount, string? expectedTail)
    {
        var slots = Enumerable.Range(0, slotCount).Select(i => Slot(ThursdayMorning.AddMinutes(40 * i), hours: 0.5)).ToList();
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday, 1, [new FreeSlotDayDto(Thursday, slots)], [Counted(1)]);

        var dayLine = FreeSlotsEmbedBuilder.Build("@Sam", 15, response).Description.Split('\n')[^1];

        Assert.Equal(FreeSlotsEmbedBuilder.MaxSlotsPerDay, dayLine.Split("–").Length - 1);
        Assert.Contains($"<t:{slots[0].StartUnix}:t>", dayLine);
        Assert.Contains($"<t:{slots[5].StartUnix}:t>", dayLine);
        if (expectedTail is null)
        {
            Assert.DoesNotContain("more", dayLine);
        }
        else
        {
            Assert.EndsWith($" · {expectedTail}", dayLine);
            Assert.DoesNotContain($"<t:{slots[6].StartUnix}:t>", dayLine);
        }
    }

    [Theory]
    [InlineData(23.5, 't')]     // shorter than a day: the end's time alone is unambiguous
    [InlineData(24, 'f')]       // a day or longer would read "8:00 AM–8:00 AM": the end carries its date
    [InlineData(30, 'f')]
    public void A_slot_a_day_long_or_more_shows_its_end_with_the_date(double hours, char expectedEndStyle)
    {
        var slot = Slot(ThursdayMorning, hours);
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday, 1, [new FreeSlotDayDto(Thursday, [slot])], [Counted(1)]);

        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, response);

        Assert.EndsWith($"<t:{slot.StartUnix}:t>–<t:{slot.EndUnix}:{expectedEndStyle}>", embed.Description);
    }

    [Fact]
    public void Members_left_out_are_named_under_their_reason_in_a_fixed_order()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(
            Member(5, FreeSlotMemberState.Error),
            Member(4, FreeSlotMemberState.ReconnectRequired),
            Counted(1),
            Member(2, FreeSlotMemberState.NotCounted),
            Member(3, FreeSlotMemberState.NotCounted)));

        var field = Assert.Single(embed.Fields);
        Assert.Equal("Not counted", field.Name);
        Assert.Equal(
            "⚪ No calendar linked — <@2>, <@3>\n" +
            "🔄 Needs to reconnect with `/calendar connect` — <@4>\n" +
            "⚠️ Couldn't check right now — <@5>",
            field.Value);
    }

    [Fact]
    public void A_reason_with_no_members_has_no_line_and_a_full_count_has_no_field()
    {
        var onlyOneReason = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(Counted(1), Member(2, FreeSlotMemberState.Error)));
        Assert.Equal("⚠️ Couldn't check right now — <@2>", Assert.Single(onlyOneReason.Fields).Value);

        var everyoneCounted = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(Counted(1), Counted(2)));
        Assert.Empty(everyoneCounted.Fields);
    }

    [Fact]
    public void A_long_list_under_one_reason_is_cut_with_a_count_inside_the_field_limit()
    {
        var members = Enumerable.Range(0, 50).Select(i => Member(UserId(i), FreeSlotMemberState.NotCounted)).ToArray();
        var response = new FreeSlotsResponse("America/Chicago", Thursday, Thursday.AddDays(6), 0, [], members);

        var value = Assert.Single(FreeSlotsEmbedBuilder.Build("@Everyone", 60, response).Fields).Value;

        Assert.True(value.Length <= EmbedFieldBuilder.MaxFieldValueLength, $"field is {value.Length} characters");
        var shown = value.Split("<@").Length - 1;
        Assert.InRange(shown, 30, 49);      // most of the field is used, and not everyone fits
        Assert.EndsWith($" +{50 - shown} more", value);
    }

    [Fact]
    public void Three_long_lists_share_the_field_so_no_reason_is_pushed_out()
    {
        var states = new[] { FreeSlotMemberState.NotCounted, FreeSlotMemberState.ReconnectRequired, FreeSlotMemberState.Error };
        var members = Enumerable.Range(0, 48).Select(i => Member(UserId(i), states[i % 3])).Append(Counted(UserId(48))).ToArray();

        var value = Assert.Single(FreeSlotsEmbedBuilder.Build("@Everyone", 60, Week(members)).Fields).Value;

        Assert.True(value.Length <= EmbedFieldBuilder.MaxFieldValueLength, $"field is {value.Length} characters");
        var lines = value.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("⚪ No calendar linked — <@", lines[0]);
        Assert.StartsWith("🔄 Needs to reconnect with `/calendar connect` — <@", lines[1]);
        Assert.StartsWith("⚠️ Couldn't check right now — <@", lines[2]);

        // Sixteen members under each reason: every line names some and counts the rest.
        Assert.All(lines, line =>
        {
            var shown = line.Split("<@").Length - 1;
            Assert.InRange(shown, 1, 15);
            Assert.EndsWith($" +{16 - shown} more", line);
        });
    }

    [Theory]
    [InlineData(49)]    // the longest sentence: "When all 49 counted members…"
    [InlineData(1)]     // the fullest field: 49 members not counted
    public void The_worst_case_stays_inside_discords_limits(int countedMembers)
    {
        // Fourteen days across New Year, each with far more slots than a line shows, the longest
        // minimum, a role name at Discord's 100-character limit and the longest zone id in tzdb.
        var first = new DateOnly(2026, 12, 25);
        var days = Enumerable.Range(0, 14)
            .Select(offset => first.AddDays(offset))
            .Select(day => new FreeSlotDayDto(day, [.. Enumerable.Range(0, 60).Select(i =>
                Slot(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(24 * i), hours: 0.25))]))
            .ToList();
        var states = new[] { FreeSlotMemberState.NotCounted, FreeSlotMemberState.ReconnectRequired, FreeSlotMemberState.Error };
        var members = Enumerable.Range(0, 50)
            .Select(i => i < countedMembers ? Counted(UserId(i)) : Member(UserId(i), states[i % 3]))
            .ToArray();
        var response = new FreeSlotsResponse(
            "America/Argentina/ComodRivadavia", first, first.AddDays(13), countedMembers, days, members);

        // Discord.Net itself refuses to build an embed over either limit, so building is the test;
        // the numbers are asserted as well so a regression says by how much.
        var embed = FreeSlotsEmbedBuilder.Build($"@{new string('x', 100)}", FreeSlotLimits.MaxMinimumMinutes, response);

        Assert.True(embed.Description.Length <= EmbedBuilder.MaxDescriptionLength, $"description is {embed.Description.Length} characters");
        Assert.True(embed.Length <= EmbedBuilder.MaxEmbedLength, $"embed is {embed.Length} characters");
        Assert.Equal(14, embed.Description.Split("\n**").Length - 1);
        Assert.All(embed.Fields, field => Assert.True(field.Value.Length <= EmbedFieldBuilder.MaxFieldValueLength));
    }

    [Fact]
    public void The_reply_wears_the_availability_grids_colour()
    {
        var grid = AvailabilityEmbedBuilder.Build("@GM", ThursdayMorning, ThursdayMorning.AddHours(1), []);

        Assert.Equal(grid.Color, FreeSlotsEmbedBuilder.Build("@GM", 60, Week(Counted(1))).Color);
    }

    private static FreeSlotMemberDto Counted(long userId) => new(userId, FreeSlotMemberState.DefaultHours);

    private static FreeSlotMemberDto Member(long userId, FreeSlotMemberState state) => new(userId, state);

    /// <summary>A realistic 19-digit Discord id, so mention lengths are what production sees.</summary>
    private static long UserId(int index) => 1_234_567_890_123_456_000 + index;

    private static FreeSlotDto Slot(DateTimeOffset start, double hours) => new(start, start.AddHours(hours));

    private static List<FreeSlotDayDto> Days(DateOnly first, int count) =>
        [.. Enumerable.Range(0, count).Select(offset => new FreeSlotDayDto(first.AddDays(offset), []))];

    /// <summary>Seven days from Thursday with nothing free, answered for the given members.</summary>
    private static FreeSlotsResponse Week(params FreeSlotMemberDto[] members) => new(
        "America/Chicago",
        Thursday,
        Thursday.AddDays(6),
        members.Count(m => m.State == FreeSlotMemberState.DefaultHours),
        Days(Thursday, 7),
        members);
}
