using CalCrony.Bot;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot.Tests;

/// <summary>The /availability slots reply: range and basis, one line per day, the members who
/// were counted on hours or a calendar alone, and the members who could not be counted with the
/// reason for each — inside Discord's embed limits at its worst.</summary>
public class FreeSlotsEmbedBuilderTests
{
    private const string Sentence8To11 = "between 8 AM and 11 PM";

    /// <summary>The default hours as the notes and the mixed sentence write them.</summary>
    private const string Range8To11 = "8 AM–11 PM";

    private static readonly DateOnly Thursday = new(2026, 11, 12);

    /// <summary>08:00 on Thursday in America/Chicago (UTC-6).</summary>
    private static readonly DateTimeOffset ThursdayMorning = new(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Title_names_the_target_and_the_first_line_states_range_and_minimum()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnDefaultHours(1)));

        Assert.Equal("Free slots for @GM", embed.Title);
        Assert.StartsWith("🗓️ Thu Nov 12 – Wed Nov 18, 2026 · 60 min or longer\n", embed.Description);
        Assert.Equal("Days are in America/Chicago · times show in your local time", embed.Footer!.Value.Text);
    }

    [Fact]
    public void A_single_day_is_not_written_as_a_range()
    {
        var response = new FreeSlotsResponse(
            "UTC", Thursday, Thursday, 1, [new FreeSlotDayDto(Thursday, [])], [OnDefaultHours(1)]);

        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 90, response);

        Assert.StartsWith("🗓️ Thu Nov 12, 2026 · 90 min or longer\n", embed.Description);
    }

    [Fact]
    public void A_range_across_new_year_shows_both_years()
    {
        // A bare date already behind us rolls to next year; the year is what makes that visible.
        var first = new DateOnly(2026, 12, 29);
        var response = new FreeSlotsResponse(
            "UTC", first, first.AddDays(6), 1, Days(first, 7), [OnDefaultHours(1)]);

        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, response);

        Assert.StartsWith("🗓️ Tue Dec 29, 2026 – Mon Jan 4, 2027 · 60 min or longer\n", embed.Description);
    }

    [Fact]
    public void One_member_counted_reads_as_their_own_free_time()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, Week(OnDefaultHours(1)));

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
            "@GM", 60, Week(OnDefaultHours(1), OnDefaultHours(2), OnDefaultHours(3), Member(4, FreeSlotMemberState.NotCounted), Member(5, FreeSlotMemberState.Error)));

        Assert.Contains(
            $"\nWhen all 3 counted members are free on their linked calendars, each {Sentence8To11} " +
            "in their own timezone (the server's for anyone who hasn't set one).\n",
            embed.Description);
    }

    [Fact]
    public void One_counted_member_of_several_is_said_plainly()
    {
        var embed = FreeSlotsEmbedBuilder.Build(
            "@GM", 60, Week(OnDefaultHours(1), Member(2, FreeSlotMemberState.NotCounted), Member(3, FreeSlotMemberState.NotCounted)));

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
        // Either of the two things a member can do gets them counted.
        Assert.Equal(
            "🗓️ Thu Nov 12 – Wed Nov 18, 2026 · 60 min or longer\n" +
            "Nobody could be counted, so there are no slots to list. " +
            "A member is counted once they link a calendar with `/calendar connect` " +
            "or save their available hours with `/settings available-hours set`.",
            embed.Description);
        Assert.Equal("⚪ No hours saved and no calendar linked — <@1>, <@2>", Assert.Single(embed.Fields).Value);
    }

    [Fact]
    public void One_member_on_their_calendar_and_saved_hours_reads_as_inside_those_hours()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, Week(OnOwnHours(1)));

        Assert.Contains(
            "\nFree on their linked calendar, inside the available hours they saved, " +
            "read in their timezone (the server's if they haven't set one).\n",
            embed.Description);

        // The sentence says all there is to say about the one member.
        Assert.Empty(embed.Fields);
    }

    [Fact]
    public void One_member_on_saved_hours_alone_is_said_to_have_no_calendar_behind_the_list()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, Week(HoursOnly(1)));

        Assert.Contains(
            "\nInside the available hours they saved, read in their timezone (the server's if they haven't set one). " +
            "No calendar is linked, so anything else they have on isn't taken into account.\n",
            embed.Description);
        Assert.Empty(embed.Fields);
    }

    [Fact]
    public void One_counted_member_of_several_on_their_own_hours_is_said_plainly()
    {
        var linked = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnOwnHours(1), Member(2, FreeSlotMemberState.NotCounted)));
        var hoursOnly = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(HoursOnly(1), Member(2, FreeSlotMemberState.Error)));

        Assert.Contains(
            "\nWhen the one counted member is free on their linked calendar, inside the available hours they saved, " +
            "read in their timezone (the server's if they haven't set one).\n",
            linked.Description);
        Assert.Contains(
            "\nInside the one counted member's saved available hours, read in their timezone (the server's if they haven't set one). " +
            "They have no calendar linked, so anything else they have on isn't taken into account.\n",
            hoursOnly.Description);

        // With one member counted the sentence says what they rest on; only who was left out is listed.
        Assert.Equal("Not counted", Assert.Single(linked.Fields).Name);
        Assert.Equal("Not counted", Assert.Single(hoursOnly.Fields).Name);
    }

    [Fact]
    public void Several_counted_members_with_saved_hours_read_as_each_inside_their_own()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnOwnHours(1), OnOwnHours(2), HoursOnly(3)));

        // Nobody is on the default hours, so they are not mentioned.
        Assert.Contains(
            "\nWhen all 3 counted members are free: each inside their available hours, " +
            "read in their own timezone (the server's for anyone who hasn't set one), with nothing on a linked calendar.\n",
            embed.Description);
    }

    [Fact]
    public void When_some_counted_members_have_not_saved_hours_the_sentence_says_what_is_assumed_for_them()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnOwnHours(1), OnDefaultHours(2)));

        Assert.Contains(
            $"\nWhen all 2 counted members are free: each inside their available hours ({Range8To11} for anyone who hasn't saved theirs), " +
            "read in their own timezone (the server's for anyone who hasn't set one), with nothing on a linked calendar.\n",
            embed.Description);
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
            [OnDefaultHours(1)]);

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
            [OnDefaultHours(1)]);

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
            [OnDefaultHours(1)]);

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
            [OnDefaultHours(1)]);

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
            [OnDefaultHours(1)]);

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
            [OnDefaultHours(1)]);

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
        var lines = FreeSlotsEmbedBuilder.Build("@Sam", minimumMinutes, Week(OnDefaultHours(1))).Description.Split('\n');

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
            [OnDefaultHours(1)]);

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
            "America/Chicago", Thursday, Thursday, 1, [new FreeSlotDayDto(Thursday, slots)], [OnDefaultHours(1)]);

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
            "America/Chicago", Thursday, Thursday, 1, [new FreeSlotDayDto(Thursday, [slot])], [OnDefaultHours(1)]);

        var embed = FreeSlotsEmbedBuilder.Build("@Sam", 60, response);

        Assert.EndsWith($"<t:{slot.StartUnix}:t>–<t:{slot.EndUnix}:{expectedEndStyle}>", embed.Description);
    }

    [Fact]
    public void Members_left_out_are_named_under_their_reason_in_a_fixed_order()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(
            Member(5, FreeSlotMemberState.Error),
            Member(4, FreeSlotMemberState.ReconnectRequired),
            OnDefaultHours(1),
            Member(2, FreeSlotMemberState.NotCounted),
            Member(3, FreeSlotMemberState.NotCounted)));

        var field = Assert.Single(embed.Fields);
        Assert.Equal("Not counted", field.Name);
        Assert.Equal(
            "⚪ No hours saved and no calendar linked — <@2>, <@3>\n" +
            "🔄 Needs to reconnect with `/calendar connect` — <@4>\n" +
            "⚠️ Couldn't check right now — <@5>",
            field.Value);
    }

    [Fact]
    public void A_reason_with_no_members_has_no_line_and_a_full_count_has_no_field()
    {
        var onlyOneReason = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnDefaultHours(1), Member(2, FreeSlotMemberState.Error)));
        Assert.Equal("⚠️ Couldn't check right now — <@2>", Assert.Single(onlyOneReason.Fields).Value);

        var everyoneCounted = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnDefaultHours(1), OnDefaultHours(2)));
        Assert.Empty(everyoneCounted.Fields);
    }

    [Fact]
    public void Counted_members_with_less_to_go_on_are_named_ahead_of_those_left_out()
    {
        var embed = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(
            Member(6, FreeSlotMemberState.NotCounted),
            OnDefaultHours(4),
            HoursOnly(2),
            OnOwnHours(1),
            HoursOnly(3),
            OnDefaultHours(5)));

        // Two fields: who was counted on hours or a calendar alone, then who was not counted. A
        // member on both their calendar and their own hours needs no note.
        Assert.Equal(["Counted with less to go on", "Not counted"], embed.Fields.Select(f => f.Name));
        Assert.Equal(
            "🕒 Saved hours only, no calendar linked — <@2>, <@3>\n" +
            $"ℹ️ No hours saved, {Range8To11} assumed — <@4>, <@5>",
            embed.Fields[0].Value);
        Assert.Equal("⚪ No hours saved and no calendar linked — <@6>", embed.Fields[1].Value);
    }

    [Fact]
    public void A_note_with_no_members_has_no_line()
    {
        var hoursOnly = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnOwnHours(1), HoursOnly(2)));
        Assert.Equal("🕒 Saved hours only, no calendar linked — <@2>", Assert.Single(hoursOnly.Fields).Value);

        var onTheDefault = FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnOwnHours(1), OnDefaultHours(2)));
        Assert.Equal($"ℹ️ No hours saved, {Range8To11} assumed — <@2>", Assert.Single(onTheDefault.Fields).Value);
    }

    [Fact]
    public void When_every_counted_member_has_both_a_calendar_and_saved_hours_there_is_nothing_to_note()
    {
        Assert.Empty(FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnOwnHours(1), OnOwnHours(2))).Fields);
    }

    [Fact]
    public void While_everyone_counted_is_on_the_default_hours_the_sentence_says_so_and_nobody_is_listed_for_it()
    {
        // Until a member saves hours, every role lookup would otherwise list its whole membership.
        var embed = FreeSlotsEmbedBuilder.Build(
            "@GM", 60, Week(OnDefaultHours(1), OnDefaultHours(2), OnDefaultHours(3), Member(4, FreeSlotMemberState.NotCounted)));

        Assert.Contains($"each {Sentence8To11} in their own timezone", embed.Description);
        Assert.Equal("Not counted", Assert.Single(embed.Fields).Name);
    }

    [Fact]
    public void Each_field_shares_its_own_room()
    {
        // Twenty on saved hours alone, twenty on the default, ten left out, and two with both.
        var members = Enumerable.Range(0, 20).Select(i => HoursOnly(UserId(i)))
            .Concat(Enumerable.Range(20, 20).Select(i => OnDefaultHours(UserId(i))))
            .Concat(Enumerable.Range(40, 10).Select(i => Member(UserId(i), FreeSlotMemberState.NotCounted)))
            .Append(OnOwnHours(UserId(50)))
            .ToArray();

        var embed = FreeSlotsEmbedBuilder.Build("@Everyone", 60, Week(members));

        Assert.Equal(2, embed.Fields.Length);
        Assert.All(embed.Fields, field => Assert.True(
            field.Value.Length <= EmbedFieldBuilder.MaxFieldValueLength, $"{field.Name} is {field.Value.Length} characters"));

        // Forty names do not fit the first field, so its two lines share it; ten fit the second.
        var notes = embed.Fields[0].Value.Split('\n');
        Assert.Equal(2, notes.Length);
        Assert.All(notes, line => Assert.Contains(" more", line));
        Assert.DoesNotContain("more", embed.Fields[1].Value);
        Assert.Equal(10, embed.Fields[1].Value.Split("<@").Length - 1);
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
            .Append(OnDefaultHours(UserId(26)))
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
            .Append(OnDefaultHours(UserId(47)))
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
        var members = Enumerable.Range(0, 48).Select(i => Member(UserId(i), states[i % 3])).Append(OnDefaultHours(UserId(48))).ToArray();

        var value = Assert.Single(FreeSlotsEmbedBuilder.Build("@Everyone", 60, Week(members)).Fields).Value;

        Assert.True(value.Length <= EmbedFieldBuilder.MaxFieldValueLength, $"field is {value.Length} characters");
        var lines = value.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("⚪ No hours saved and no calendar linked — <@", lines[0]);
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
            .Select(i => i < countedMembers ? OnDefaultHours(UserId(i)) : Member(UserId(i), states[i % 3]))
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
    public void The_worst_case_with_both_fields_full_stays_inside_discords_limits()
    {
        // The same fourteen days, the longest sentence there is, and both fields with more names
        // than they hold: twenty-four members on saved hours alone and twenty-four on the default
        // make the first field's two lines both long, and forty-eight left out fill the second.
        var first = new DateOnly(2026, 12, 25);
        var days = Enumerable.Range(0, 14)
            .Select(offset => first.AddDays(offset))
            .Select(day => new FreeSlotDayDto(day, [.. Enumerable.Range(0, 60).Select(i =>
                Slot(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(24 * i), hours: 0.25))]))
            .ToList();
        var states = new[] { FreeSlotMemberState.NotCounted, FreeSlotMemberState.ReconnectRequired, FreeSlotMemberState.Error };
        var members = Enumerable.Range(0, 24).Select(i => HoursOnly(UserId(i)))
            .Concat(Enumerable.Range(24, 24).Select(i => OnDefaultHours(UserId(i))))
            .Concat(Enumerable.Range(48, 48).Select(i => Member(UserId(i), states[i % 3])))
            .Append(OnOwnHours(UserId(96)))
            .ToArray();
        var response = new FreeSlotsResponse(
            "America/Argentina/ComodRivadavia", first, first.AddDays(13), members.Count(IsCounted), days, members);

        // More members than one lookup allows, on purpose: the fields must hold whatever they are
        // given. Discord.Net refuses to build an embed over either limit, so building is the test.
        var embed = FreeSlotsEmbedBuilder.Build($"@{new string('x', 100)}", FreeSlotLimits.MaxMinimumMinutes, response);

        Assert.True(embed.Description.Length <= EmbedBuilder.MaxDescriptionLength, $"description is {embed.Description.Length} characters");
        Assert.True(embed.Length <= EmbedBuilder.MaxEmbedLength, $"embed is {embed.Length} characters");
        Assert.Equal(["Counted with less to go on", "Not counted"], embed.Fields.Select(f => f.Name));
        Assert.All(embed.Fields, field => Assert.True(field.Value.Length <= EmbedFieldBuilder.MaxFieldValueLength));

        // The field for those left out keeps a field's full room; the notes take what is left
        // over, and still name people.
        // Three reasons of sixteen names each share that field, so each line stops a name short
        // of its share; the field is still nearly full.
        Assert.True(embed.Fields[1].Value.Length > 900, $"not counted is {embed.Fields[1].Value.Length}");
        Assert.Contains("<@", embed.Fields[0].Value);
        Assert.Equal(2, embed.Fields[0].Value.Split('\n').Length);
        Assert.All(embed.Fields[0].Value.Split('\n'), line => Assert.Contains(" more", line));
    }

    [Fact]
    public void A_slot_cut_where_the_lookup_stopped_looking_is_said_to_end_then_or_later()
    {
        // Hours that run to midnight and resume at midnight can carry a slot to the edge of the
        // lookup, one day past the last day listed. Its real end is that instant or later.
        var edge = ThursdayMorning.AddDays(2);
        var ordinary = Slot(ThursdayMorning, hours: 2);
        var cut = new FreeSlotDto(ThursdayMorning.AddHours(4), edge);
        var response = new FreeSlotsResponse(
            "America/Chicago",
            Thursday,
            Thursday.AddDays(1),
            1,
            [new FreeSlotDayDto(Thursday, [ordinary, cut]), new FreeSlotDayDto(Thursday.AddDays(1), [], edge)],
            [HoursOnly(1)],
            LookedUntilUtc: edge);

        var lines = FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description.Split('\n');

        // Only the end that is the edge of the lookup is qualified; a slot that ends inside it is not.
        Assert.Contains(
            $"**Thu Nov 12** · <t:{ordinary.StartUnix}:t>–<t:{ordinary.EndUnix}:t> · " +
            $"<t:{cut.StartUnix}:t>–<t:{cut.EndUnix}:f> or later",
            lines);
        Assert.Contains($"**Fri Nov 13** · free until <t:{edge.ToUnixTimeSeconds()}:f> or later (started Thu)", lines);
    }

    [Fact]
    public void An_end_that_is_only_the_edge_of_the_lookup_carries_its_date_however_short_the_slot()
    {
        // Where the day after the last one listed is a 23-hour day, a slot can reach the edge in
        // under a day. "11:30 PM–12:00 AM or later" would not say which midnight is meant.
        var edge = ThursdayMorning.AddHours(23.5);
        var cut = new FreeSlotDto(ThursdayMorning, edge);
        var response = new FreeSlotsResponse(
            "America/Chicago",
            Thursday,
            Thursday.AddDays(1),
            1,
            [new FreeSlotDayDto(Thursday, [cut]), new FreeSlotDayDto(Thursday.AddDays(1), [], edge)],
            [HoursOnly(1)],
            LookedUntilUtc: edge);

        var lines = FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description.Split('\n');

        Assert.Contains($"**Thu Nov 12** · <t:{cut.StartUnix}:t>–<t:{cut.EndUnix}:f> or later", lines);
        Assert.Contains($"**Fri Nov 13** · free until <t:{edge.ToUnixTimeSeconds()}:f> or later (started Thu)", lines);
    }

    [Fact]
    public void An_end_past_the_edge_of_the_lookup_is_qualified_too()
    {
        // The API cuts a slot at the edge, so it sends none that ends later. The reply does not
        // lean on that: an end at the edge or past it is one the lookup did not see.
        var edge = ThursdayMorning.AddHours(30);
        var past = new FreeSlotDto(ThursdayMorning, edge.AddHours(1));
        var response = new FreeSlotsResponse(
            "America/Chicago", Thursday, Thursday, 1, [new FreeSlotDayDto(Thursday, [past])], [HoursOnly(1)], LookedUntilUtc: edge);

        Assert.EndsWith(
            $"<t:{past.StartUnix}:t>–<t:{past.EndUnix}:f> or later", FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description);
    }

    [Fact]
    public void A_free_until_line_with_no_earlier_day_to_name_gives_the_time_and_its_date_only_at_the_edge()
    {
        // The API never sends this: the first day listed cannot begin inside a slot from a day
        // before it. The reply still has to read sensibly should an answer ever do so.
        var until = ThursdayMorning.AddHours(2);
        FreeSlotsResponse Answer(DateTimeOffset? lookedUntil) => new(
            "America/Chicago",
            Thursday,
            Thursday,
            1,
            [new FreeSlotDayDto(Thursday, [], until)],
            [HoursOnly(1)],
            LookedUntilUtc: lookedUntil);

        Assert.Contains(
            $"**Thu Nov 12** · free until <t:{until.ToUnixTimeSeconds()}:t>",
            FreeSlotsEmbedBuilder.Build("@Sam", 60, Answer(null)).Description.Split('\n'));
        Assert.Contains(
            $"**Thu Nov 12** · free until <t:{until.ToUnixTimeSeconds()}:f> or later",
            FreeSlotsEmbedBuilder.Build("@Sam", 60, Answer(until)).Description.Split('\n'));
    }

    [Fact]
    public void When_the_answer_does_not_say_how_far_it_looked_no_end_is_qualified()
    {
        // An API that does not send the edge: the reply reads as it did before.
        var edge = ThursdayMorning.AddDays(2);
        var response = new FreeSlotsResponse(
            "America/Chicago",
            Thursday,
            Thursday.AddDays(1),
            1,
            [new FreeSlotDayDto(Thursday, [new FreeSlotDto(ThursdayMorning, edge)]), new FreeSlotDayDto(Thursday.AddDays(1), [], edge)],
            [HoursOnly(1)]);

        Assert.DoesNotContain("or later", FreeSlotsEmbedBuilder.Build("@Sam", 60, response).Description);
    }

    [Fact]
    public void The_reply_wears_the_availability_grids_colour()
    {
        var grid = AvailabilityEmbedBuilder.Build("@GM", ThursdayMorning, ThursdayMorning.AddHours(1), []);

        Assert.Equal(grid.Color, FreeSlotsEmbedBuilder.Build("@GM", 60, Week(OnDefaultHours(1))).Color);
    }

    /// <summary>A member counted on a linked calendar and the default hours.</summary>
    private static FreeSlotMemberDto OnDefaultHours(long userId) => new(userId, FreeSlotMemberState.DefaultHours);

    /// <summary>A member counted on a linked calendar and hours they saved.</summary>
    private static FreeSlotMemberDto OnOwnHours(long userId) => new(userId, FreeSlotMemberState.Counted);

    /// <summary>A member counted on saved hours alone, with no calendar linked.</summary>
    private static FreeSlotMemberDto HoursOnly(long userId) => new(userId, FreeSlotMemberState.HoursOnly);

    private static FreeSlotMemberDto Member(long userId, FreeSlotMemberState state) => new(userId, state);

    private static bool IsCounted(FreeSlotMemberDto member) =>
        member.State is FreeSlotMemberState.Counted or FreeSlotMemberState.DefaultHours or FreeSlotMemberState.HoursOnly;

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
        members.Count(IsCounted),
        Days(Thursday, 7),
        members);
}
