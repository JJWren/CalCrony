using CalCrony.Bot;
using CalCrony.Bot.Modules;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class RepeatOptionsTests
{
    [Fact]
    public void Template_edit_with_only_repeat_days_asks_for_repeat_instead_of_reporting_no_change()
    {
        var problem = TemplateModule.PreflightEdit(hasContentChange: false, repeat: null, repeatEvery: 1, repeatDays: "tue,thu", out var rule);

        Assert.Equal("Set `repeat` to use the repeat options.", problem);
        Assert.Null(rule);
    }

    [Fact]
    public void Template_edit_with_only_repeat_every_asks_for_repeat_too()
    {
        var problem = TemplateModule.PreflightEdit(hasContentChange: false, repeat: null, repeatEvery: 2, repeatDays: null, out _);

        Assert.Equal("Set `repeat` to use the repeat options.", problem);
    }

    [Fact]
    public void Template_edit_with_nothing_at_all_is_a_no_op()
    {
        var problem = TemplateModule.PreflightEdit(hasContentChange: false, repeat: null, repeatEvery: 1, repeatDays: null, out _);

        Assert.Equal("Nothing to change — pass at least one field.", problem);
    }

    [Fact]
    public void Template_edit_accepts_none_to_clear_the_day_set()
    {
        var problem = TemplateModule.PreflightEdit(hasContentChange: false, RepeatChoice.Weekly, 1, "none", out var rule);

        Assert.Null(problem);
        Assert.Equal(RecurrenceUnit.Week, rule!.Unit);
        Assert.Equal(RecurrenceDays.None, rule.DaysOfWeek);
    }

    [Fact]
    public void Template_edit_builds_a_weekly_rule_with_the_day_set()
    {
        var problem = TemplateModule.PreflightEdit(hasContentChange: true, RepeatChoice.Weekly, 2, "mon, wed, fri", out var rule);

        Assert.Null(problem);
        Assert.Equal(2, rule!.Interval);
        Assert.Equal(RecurrenceDays.Monday | RecurrenceDays.Wednesday | RecurrenceDays.Friday, rule.DaysOfWeek);
    }

    [Fact]
    public void Day_set_on_a_non_weekly_rule_is_rejected_and_create_does_not_accept_none()
    {
        Assert.Equal(
            "`repeat-days` only applies to `repeat: weekly`.",
            RepeatOptions.TryBuildRule(RepeatChoice.Daily, 1, "tue", allowNoneDays: false, out _));

        // /create has no day set to clear, so "none" is just not a weekday there.
        var problem = RepeatOptions.TryBuildRule(RepeatChoice.Weekly, 1, "none", allowNoneDays: false, out _);
        Assert.Contains("isn't a weekday", problem);
    }

    [Fact]
    public void No_repeat_choice_yields_no_rule()
    {
        Assert.Null(RepeatOptions.TryBuildRule(null, 1, null, allowNoneDays: false, out var rule));
        Assert.Null(rule);
        Assert.Null(RepeatOptions.TryBuildRule(RepeatChoice.None, 1, null, allowNoneDays: false, out var cleared));
        Assert.Null(cleared);
    }
}
