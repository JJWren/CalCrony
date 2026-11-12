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

        // The hours are read in the server's zone for a member who has set none, and the sentence
        // says so: that moves every time in the list.
        Assert.Contains(
            $"\nFree on their linked calendar, {Sentence8To11} in their timezone (the server's if they haven't set one).\n",
            embed.Description);
    }

    [Fact]
    public void Several_members_counted_read_as_the_time_they_all_share()
    {
        var embed = FreeSlotsEmbedBuilder.Build(
            "@GM", 60, Week(Counted(1), Counted(2), Counted(3), Member(4, FreeSlotMemberState.NotCounted), Member(5, FreeSlotMemberState.Error)));

        Assert.Contains(
            $"\nWhen all 3 counted members are free on their linked calendars, each {Sentence8To11} " +
            "in their own timezone (the server's for anyone who hasn't set one).\n",
            embed.Description);
    }

    [Fact]
    public void One_counted_member_of_several_is_said_plainly()
    {
        var embed = FreeSlotsEmbedBuilder.Build(
            "@GM", 60, Week(Counted(1), Member(2, FreeSlotMemberState.NotCounted), Member(3, FreeSlotMemberState.NotCounted)));

        Assert.Contains(
            $"\nWhen the one counted member is free on their linked calendar, {Sentence8To11} " +
            "in their timezone (the server's if they haven't set one).\n",
            embed.Description);
    }

    [Fact]
    public void When_nobody_is_counted_for_want_of_a_calendar_the_reply_says_how_to_be_counted()
    {
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(6), 0, [],
            [Member(1, FreeSlotMemberState.NotCounted), Member(2, FreeSlotMemberState.NotCounted)]);

        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, response);

        // No days, and no hint about a shorter minimum: there was nothing to measure against.
        Assert.Equal(
            "🗓️ Thu Nov 12 – Wed Nov 18, 2026 · 60 min or longer\n" +
            "Nobody could be counted, so there are no slots to list. " +
            "A member is counted once they link a calendar with `/calendar connect`.",
            embed.Description);
        Assert.Equal("⚪ No calendar linked — <@1>, <@2>", Assert.Single(embed.Fields).Value);
    }

    [Theory]
    [InlineData(FreeSlotMemberState.ReconnectRequired)]
    [InlineData(FreeSlotMemberState.Error)]
    public void When_nobody_is_counted_for_another_reason_the_reply_does_not_tell_them_to_link_a_calendar(
        FreeSlotMemberState state)
    {
        // A member who has linked a calendar is not helped by being told to link one.
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(6), 0, [],
            [Member(1, FreeSlotMemberState.NotCounted), Member(2, state)]);

        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, response);

        Assert.Equal(
            "🗓️ Thu Nov 12 – Wed Nov 18, 2026 · 60 min or longer\n" +
            "Nobody could be counted, so there are no slots to list. The reasons are listed below.",
            embed.Description);

        // Why each member was left out is said under its own reason.
        Assert.Equal(2, Assert.Single(embed.Fields).Value.Split('\n').Length);
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

    [Fact]
    public void A_day_that_begins_inside_an_earlier_days_slot_says_until_when_it_is_free()
    {
        // 11 PM Thursday to 2 PM Friday, as a member fifteen hours behind the caller would show.
        // The slot is listed under Thursday, the day it starts on; nothing starts on Friday, but
        // "nothing free" would be untrue of a day that is free until 2 PM.
        var overnight = Slot(ThursdayMorning.AddHours(15), hours: 15);
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(2), 1,
            [
                new FreeSlotDayDto(Thursday, [Slot(ThursdayMorning, hours: 2), overnight]),
                new FreeSlotDayDto(Thursday.AddDays(1), [], overnight.EndUtc),
                new FreeSlotDayDto(Thursday.AddDays(2), []),
            ],
            [Counted(1)]);

        var lines = FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description.Split('\n');

        Assert.Equal($"**Fri Nov 13** · free until <t:{overnight.EndUnix}:t> (started Thu)", lines[^2]);
        Assert.Equal("**Sat Nov 14** · nothing free", lines[^1]);
    }

    [Fact]
    public void A_day_with_slots_of_its_own_lists_those_and_does_not_repeat_the_one_it_began_inside()
    {
        var overnight = Slot(ThursdayMorning.AddHours(15), hours: 15);
        var fridayEvening = Slot(ThursdayMorning.AddHours(39), hours: 15);
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(1), 1,
            [
                new FreeSlotDayDto(Thursday, [overnight]),
                new FreeSlotDayDto(Thursday.AddDays(1), [fridayEvening], overnight.EndUtc),
            ],
            [Counted(1)]);

        var friday = FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description.Split('\n')[^1];

        // The slot Friday began inside is on Thursday's line, end and all.
        Assert.Equal($"**Fri Nov 13** · <t:{fridayEvening.StartUnix}:t>–<t:{fridayEvening.EndUnix}:t>", friday);
    }

    [Fact]
    public void The_end_of_a_slot_a_day_or_longer_carries_its_date_on_the_day_it_runs_into_too()
    {
        var long30 = Slot(ThursdayMorning, hours: 30);
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(1), 1,
            [new FreeSlotDayDto(Thursday, [long30]), new FreeSlotDayDto(Thursday.AddDays(1), [], long30.EndUtc)],
            [Counted(1)]);

        var friday = FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description.Split('\n')[^1];

        Assert.Equal($"**Fri Nov 13** · free until <t:{long30.EndUnix}:f> (started Thu)", friday);
    }

    [Fact]
    public void A_slot_that_runs_through_whole_days_is_named_on_each_day_it_covers()
    {
        // Midnight Thursday to midnight Sunday in Chicago, as hours saved round the clock give.
        var threeDays = Slot(ThursdayMorning.AddHours(-8), hours: 72);
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(2), 1,
            [
                new FreeSlotDayDto(Thursday, [threeDays]),
                new FreeSlotDayDto(Thursday.AddDays(1), [], threeDays.EndUtc),
                new FreeSlotDayDto(Thursday.AddDays(2), [], threeDays.EndUtc),
            ],
            [Counted(1)]);

        var lines = FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description.Split('\n');

        Assert.Equal($"**Fri Nov 13** · free until <t:{threeDays.EndUnix}:f> (started Thu)", lines[^2]);
        Assert.Equal($"**Sat Nov 14** · free until <t:{threeDays.EndUnix}:f> (started Thu)", lines[^1]);
    }

    [Fact]
    public void A_day_can_begin_inside_a_slot_that_the_day_before_folded_into_its_count()
    {
        // Thursday has seven slots: six are shown, and the seventh — which runs past midnight —
        // is the "+1 more". Friday still says until when that one keeps it free.
        var shown = Enumerable.Range(0, 6).Select(i => Slot(ThursdayMorning.AddHours(2 * i), hours: 1)).ToList();
        var overnight = Slot(ThursdayMorning.AddHours(15), hours: 4);      // 11 PM Thursday to 3 AM Friday
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(1), 1,
            [
                new FreeSlotDayDto(Thursday, [.. shown, overnight]),
                new FreeSlotDayDto(Thursday.AddDays(1), [], overnight.EndUtc),
            ],
            [Counted(1)]);

        var lines = FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description.Split('\n');

        Assert.EndsWith(" · +1 more", lines[^2]);
        Assert.DoesNotContain($"<t:{overnight.StartUnix}:t>", lines[^2]);
        Assert.Equal($"**Fri Nov 13** · free until <t:{overnight.EndUnix}:t> (started Thu)", lines[^1]);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(1440, true)]
    [InlineData(FreeSlotLimits.MinMinimumMinutes, false)]      // already the shortest that can be asked for
    public void When_no_day_has_anything_free_the_reply_suggests_a_shorter_minimum(int minimumMinutes, bool expected)
    {
        // A minimum longer than anyone's hours can never match, and a run of "nothing free" does
        // not say so.
        var lines = FreeSlotsEmbedBuilder.Build("@Sam", minimumMinutes, Week(Counted(1))).Description.Split('\n');

        var hint = $"Nothing {minimumMinutes} min or longer is free in this range — a shorter `duration` may find something.";
        if (expected)
        {
            Assert.Equal(new[] { "**Wed Nov 18** · nothing free", "", hint }, lines[^3..]);
        }
        else
        {
            Assert.Equal("**Wed Nov 18** · nothing free", lines[^1]);
            Assert.DoesNotContain(lines, line => line.Contains("shorter"));
        }
    }

    [Fact]
    public void A_single_free_slot_is_enough_for_the_reply_not_to_suggest_a_shorter_minimum()
    {
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday.AddDays(1), 1,
            [new FreeSlotDayDto(Thursday, [Slot(ThursdayMorning, hours: 2)]), new FreeSlotDayDto(Thursday.AddDays(1), [])],
            [Counted(1)]);

        var description = FreeSlotsEmbedBuilder.Build("@Sam", 120, response).Description;

        Assert.EndsWith("\n**Fri Nov 13** · nothing free", description);
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
    public void When_the_field_holds_every_line_everyone_left_out_is_named()
    {
        // Twenty-five under one reason and one under another: about 700 characters, well inside
        // the field. An equal share per reason would cut the long line at twenty names with half
        // the field empty.
        var members = Enumerable.Range(0, 25).Select(i => Member(UserId(i), FreeSlotMemberState.NotCounted))
            .Append(Member(UserId(25), FreeSlotMemberState.ReconnectRequired))
            .Append(Counted(UserId(26)))
            .ToArray();

        var value = Assert.Single(FreeSlotsEmbedBuilder.Build("@Everyone", 60, Week(members)).Fields).Value;

        Assert.DoesNotContain("more", value);
        Assert.Equal(26, value.Split("<@").Length - 1);
        Assert.Equal(2, value.Split('\n').Length);
    }

    [Fact]
    public void A_long_list_takes_the_room_the_short_ones_leave()
    {
        // Forty-five under one reason — too many for the field — and one under each of the others.
        var members = Enumerable.Range(0, 45).Select(i => Member(UserId(i), FreeSlotMemberState.NotCounted))
            .Append(Member(UserId(45), FreeSlotMemberState.ReconnectRequired))
            .Append(Member(UserId(46), FreeSlotMemberState.Error))
            .Append(Counted(UserId(47)))
            .ToArray();

        var value = Assert.Single(FreeSlotsEmbedBuilder.Build("@Everyone", 60, Week(members)).Fields).Value;

        Assert.True(value.Length <= EmbedFieldBuilder.MaxFieldValueLength, $"field is {value.Length} characters");
        var lines = value.Split('\n');
        Assert.Equal(3, lines.Length);

        // The short lines are whole…
        Assert.Equal($"🔄 Needs to reconnect with `/calendar connect` — <@{UserId(45)}>", lines[1]);
        Assert.Equal($"⚠️ Couldn't check right now — <@{UserId(46)}>", lines[2]);

        // …and the long one has everything they did not need: far more than a third of the field.
        var shown = lines[0].Split("<@").Length - 1;
        Assert.InRange(shown, 30, 44);
        Assert.EndsWith($" +{45 - shown} more", lines[0]);
        Assert.True(value.Length > EmbedFieldBuilder.MaxFieldValueLength - 30, $"field is only {value.Length} characters");
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
