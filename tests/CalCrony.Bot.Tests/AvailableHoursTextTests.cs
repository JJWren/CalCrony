using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>The hours commands without Discord: which days a choice names, what a command does to
/// a week, the week on one line, the typed range, and every reply.</summary>
public class AvailableHoursTextTests
{
    private static readonly AvailableHoursDayDto[] DefaultWeek =
    [
        .. new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        }.Select(day => new AvailableHoursDayDto(day, AvailableHoursLimits.DefaultStartMinute, AvailableHoursLimits.DefaultEndMinute)),
    ];

    [Theory]
    [InlineData(AvailableHoursDays.EveryDay, "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday")]
    [InlineData(AvailableHoursDays.Weekdays, "Monday,Tuesday,Wednesday,Thursday,Friday")]
    [InlineData(AvailableHoursDays.Weekends, "Saturday,Sunday")]
    [InlineData(AvailableHoursDays.Monday, "Monday")]
    [InlineData(AvailableHoursDays.Tuesday, "Tuesday")]
    [InlineData(AvailableHoursDays.Wednesday, "Wednesday")]
    [InlineData(AvailableHoursDays.Thursday, "Thursday")]
    [InlineData(AvailableHoursDays.Friday, "Friday")]
    [InlineData(AvailableHoursDays.Saturday, "Saturday")]
    [InlineData(AvailableHoursDays.Sunday, "Sunday")]
    public void Each_choice_names_its_days_monday_first(AvailableHoursDays choice, string expected)
    {
        Assert.Equal(expected, string.Join(",", AvailableHoursText.DaysOf(choice)));
    }

    [Fact]
    public void There_are_ten_choices_and_each_names_at_least_one_day()
    {
        var choices = Enum.GetValues<AvailableHoursDays>();

        Assert.Equal(10, choices.Length);
        Assert.All(choices, choice => Assert.NotEmpty(AvailableHoursText.DaysOf(choice)));
    }

    [Fact]
    public void A_first_set_starts_from_an_empty_week_so_days_not_named_are_unavailable()
    {
        var week = AvailableHoursText.Apply([], AvailableHoursDays.Weekdays, (1080, 1380));

        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            week.Select(day => day.Day));
        Assert.All(week, day => Assert.Equal((1080, 1380), (day.StartMinute, day.EndMinute)));
    }

    [Fact]
    public void Set_changes_the_days_named_and_leaves_the_others_as_they_were()
    {
        var weekdays = AvailableHoursText.Apply([], AvailableHoursDays.Weekdays, (1080, 1380));

        var week = AvailableHoursText.Apply(weekdays, AvailableHoursDays.Saturday, (600, 1440));
        week = AvailableHoursText.Apply(week, AvailableHoursDays.Friday, (1020, 1440));

        // Monday first, whatever order the days were set in.
        Assert.Equal(
            [
                new AvailableHoursDayDto(DayOfWeek.Monday, 1080, 1380),
                new AvailableHoursDayDto(DayOfWeek.Tuesday, 1080, 1380),
                new AvailableHoursDayDto(DayOfWeek.Wednesday, 1080, 1380),
                new AvailableHoursDayDto(DayOfWeek.Thursday, 1080, 1380),
                new AvailableHoursDayDto(DayOfWeek.Friday, 1020, 1440),
                new AvailableHoursDayDto(DayOfWeek.Saturday, 600, 1440),
            ],
            week);
    }

    [Fact]
    public void Off_removes_the_days_named_and_leaves_the_others()
    {
        var week = AvailableHoursText.Apply(DefaultWeek, AvailableHoursDays.Weekends, null);

        Assert.Equal(5, week.Count);
        Assert.DoesNotContain(week, day => day.Day is DayOfWeek.Saturday or DayOfWeek.Sunday);

        // A day that was already off stays off, and nothing else moves.
        Assert.Equal(week, AvailableHoursText.Apply(week, AvailableHoursDays.Sunday, null));
    }

    [Fact]
    public void Off_for_every_day_leaves_no_day()
    {
        Assert.Empty(AvailableHoursText.Apply(DefaultWeek, AvailableHoursDays.EveryDay, null));
    }

    [Fact]
    public void Sunday_is_the_last_day_of_the_week_not_the_first()
    {
        var week = AvailableHoursText.Apply([], AvailableHoursDays.Sunday, (600, 720));
        week = AvailableHoursText.Apply(week, AvailableHoursDays.Monday, (600, 720));

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Sunday], week.Select(day => day.Day));
    }

    [Fact]
    public void A_week_reads_as_groups_of_days_that_share_a_range()
    {
        var week = AvailableHoursText.Apply([], AvailableHoursDays.Weekdays, (1080, 1380));
        week = AvailableHoursText.Apply(week, AvailableHoursDays.Weekends, (600, 1440));

        Assert.Equal("Mon–Fri 6 PM–11 PM · Sat, Sun 10 AM–midnight", AvailableHoursText.Describe(week));
    }

    [Fact]
    public void Days_apart_are_joined_by_commas_and_days_off_come_last()
    {
        AvailableHoursDayDto[] week =
        [
            new(DayOfWeek.Monday, 1080, 1320),
            new(DayOfWeek.Wednesday, 1080, 1320),
            new(DayOfWeek.Friday, 1080, 1320),
            new(DayOfWeek.Saturday, 540, 780),
        ];

        Assert.Equal("Mon, Wed, Fri 6 PM–10 PM · Sat 9 AM–1 PM · Tue, Thu, Sun off", AvailableHoursText.Describe(week));
    }

    [Fact]
    public void A_run_of_three_is_a_span_and_a_run_of_two_is_not()
    {
        AvailableHoursDayDto[] week =
        [
            new(DayOfWeek.Monday, 480, 720),
            new(DayOfWeek.Tuesday, 480, 720),
            new(DayOfWeek.Wednesday, 480, 720),
            new(DayOfWeek.Friday, 480, 720),
            new(DayOfWeek.Saturday, 480, 720),
        ];

        Assert.Equal("Mon–Wed, Fri, Sat 8 AM–12 PM · Thu, Sun off", AvailableHoursText.Describe(week));
    }

    [Fact]
    public void The_same_range_on_all_seven_days_is_every_day()
    {
        Assert.Equal("Every day 8 AM–11 PM", AvailableHoursText.Describe(DefaultWeek));
        Assert.Equal("8 AM–11 PM", AvailableHoursText.DefaultRange);
    }

    [Fact]
    public void One_day_reads_as_that_day_and_the_rest_off()
    {
        Assert.Equal(
            "Sun 12 AM–midnight · Mon–Sat off",
            AvailableHoursText.Describe([new AvailableHoursDayDto(DayOfWeek.Sunday, 0, 1440)]));
    }

    [Fact]
    public void Half_hours_show_their_minutes()
    {
        Assert.Equal(
            "Tue 6:30 PM–9:30 PM · Mon, Wed–Sun off",
            AvailableHoursText.Describe([new AvailableHoursDayDto(DayOfWeek.Tuesday, 1110, 1290)]));
    }

    [Theory]
    [InlineData("6pm", "11pm", 1080, 1380)]
    [InlineData("18:00", "24:00", 1080, 1440)]
    [InlineData("midnight", "midnight", 0, 1440)]       // the whole day
    [InlineData("12am", "6:30 AM", 0, 390)]
    [InlineData("9", "17", 540, 1020)]
    public void Two_typed_times_make_a_range(string from, string to, int start, int end)
    {
        Assert.True(AvailableHoursText.TryReadRange(from, to, out var range, out var refusal));

        Assert.Null(refusal);
        Assert.Equal((start, end), range);
    }

    [Fact]
    public void A_time_that_cannot_be_read_is_named_by_its_option_and_not_quoted_back()
    {
        Assert.False(AvailableHoursText.TryReadRange("<@1234> soon", "11pm", out _, out var from));
        Assert.Equal("I couldn't read the `from` time. Try 6 PM, 6:30pm or 18:30.", from);

        Assert.False(AvailableHoursText.TryReadRange("6pm", "later", out _, out var to));
        Assert.Equal("I couldn't read the `to` time. Try 6 PM, 6:30pm or 18:30.", to);

        // 24:00 ends a day; it does not start one.
        Assert.False(AvailableHoursText.TryReadRange("24:00", "11pm", out _, out var start));
        Assert.Equal("I couldn't read the `from` time. Try 6 PM, 6:30pm or 18:30.", start);
    }

    [Theory]
    [InlineData("6:15pm", "11pm")]
    [InlineData("6pm", "10:45pm")]
    public void A_time_off_the_step_is_refused_with_the_step(string from, string to)
    {
        Assert.False(AvailableHoursText.TryReadRange(from, to, out _, out var refusal));

        Assert.Equal("Times are in 30-minute steps — 6 PM or 6:30 PM, for example.", refusal);
        Assert.Contains($"{AvailableHoursLimits.StepMinutes}-minute", AvailableHoursText.OffTheStep);
    }

    [Theory]
    [InlineData("6pm", "6pm")]
    [InlineData("10pm", "2am")]         // no overnight ranges
    [InlineData("11:30pm", "12:30am")]
    public void A_start_that_is_not_before_its_end_is_refused(string from, string to)
    {
        Assert.False(AvailableHoursText.TryReadRange(from, to, out _, out var refusal));

        Assert.Equal(
            "The start has to be before the end. Hours can't run past midnight; for a late night, end at midnight.", refusal);
    }

    [Fact]
    public void The_saved_reply_spells_out_the_week_the_zone_and_what_saving_allows()
    {
        var week = AvailableHoursText.Apply([], AvailableHoursDays.Weekdays, (1080, 1380));

        Assert.Equal(
            "🕒 Your available hours: **Mon–Fri 6 PM–11 PM · Sat, Sun off**\n" +
            "Read in your timezone, **America/Chicago**.\n" +
            "Saved hours can be looked up by name: members of the servers you share with CalCrony can list your free slots " +
            "with `/availability slots`, which shows roughly the hours you keep. `/settings available-hours reset` takes that back.",
            AvailableHoursText.SavedReply(week, "America/Chicago", "Europe/Berlin"));
    }

    [Fact]
    public void Without_a_personal_timezone_the_reply_says_whose_applies_and_how_to_set_one()
    {
        Assert.Equal(
            "You haven't set a timezone, so these hours are read in each server's own — **Europe/Berlin** here. " +
            "`/settings timezone` makes them mean the same everywhere.",
            AvailableHoursText.ZoneLine(null, "Europe/Berlin"));

        // A server CalCrony has no timezone for runs on UTC.
        Assert.Contains("**UTC** here", AvailableHoursText.ZoneLine("  ", null));
    }

    [Fact]
    public void The_reset_reply_says_the_default_applies_and_what_resetting_takes_back()
    {
        Assert.Equal(
            "🕒 Your available hours are back to the default, **8 AM–11 PM every day**. " +
            "Other members can no longer list your free slots by name; a role you hold still counts you while your calendar is linked.",
            AvailableHoursText.ResetReply());
    }

    [Fact]
    public void The_view_line_shows_saved_hours_with_what_saving_allows()
    {
        var saved = new AvailableHoursDto(true, AvailableHoursText.Apply([], AvailableHoursDays.Weekends, (600, 1320)));

        Assert.Equal(
            "**Your available hours:** Sat, Sun 10 AM–10 PM · Mon–Fri off " +
            "(saved — members of servers you share with CalCrony can list your free slots by name)",
            AvailableHoursText.ViewLine(saved));
    }

    [Fact]
    public void The_view_line_says_when_nothing_is_saved_and_what_is_assumed()
    {
        // The API sends the default week along with "not set"; the line names it as assumed.
        Assert.Equal(
            "**Your available hours:** not saved — 8 AM–11 PM every day is assumed",
            AvailableHoursText.ViewLine(new AvailableHoursDto(false, DefaultWeek)));
    }

    [Fact]
    public void The_refusals_for_off_point_at_the_command_that_helps()
    {
        Assert.Equal(
            "You haven't saved any hours yet, so there is nothing to switch off. Start with `/settings available-hours set`.",
            AvailableHoursText.NothingSavedToSwitchOff);
        Assert.Equal(
            "That would leave no day available. To go back to the default hours, use `/settings available-hours reset`.",
            AvailableHoursText.WouldLeaveNoDay);
    }
}
