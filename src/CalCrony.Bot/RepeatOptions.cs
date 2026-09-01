using CalCrony.Bot.Modules;
using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>Pure resolution of the <c>repeat</c> / <c>repeat-every</c> / <c>repeat-days</c>
/// slash options into a rule, shared by /create and /template edit so both validate the same
/// way and the ordering of their checks is unit-testable without a Discord context.</summary>
public static class RepeatOptions
{
    /// <summary>Whether repeat-shaping options were passed without a rule choice — they would
    /// otherwise be silently ignored.</summary>
    /// <param name="repeat">The repeat choice, if any.</param>
    /// <param name="repeatEvery">The interval option (1 = default).</param>
    /// <param name="repeatDays">The raw day-set option, if any.</param>
    /// <returns>True when an interval or day set was given with no rule.</returns>
    public static bool ShapingWithoutRule(RepeatChoice? repeat, int repeatEvery, string? repeatDays) =>
        repeat is null && (repeatEvery != 1 || repeatDays is not null);

    /// <summary>Builds the rule for a repeat choice, validating the day set against it.</summary>
    /// <param name="repeat">The repeat choice; null or None yields no rule.</param>
    /// <param name="repeatEvery">The interval option.</param>
    /// <param name="repeatDays">The raw day-set option, if any (weekly only).</param>
    /// <param name="allowNoneDays">Whether "none" clears the day set (edit commands).</param>
    /// <param name="rule">The built rule, or null for no rule.</param>
    /// <returns>A user-facing problem, or null when the options resolved.</returns>
    public static string? TryBuildRule(
        RepeatChoice? repeat, int repeatEvery, string? repeatDays, bool allowNoneDays, out RecurrenceRuleDto? rule)
    {
        rule = null;

        // A day set needs an explicit weekly choice — on any other unit the days would be
        // silently meaningless.
        var days = RecurrenceDays.None;
        if (repeatDays is not null)
        {
            if (repeat != RepeatChoice.Weekly)
            {
                return "`repeat-days` only applies to `repeat: weekly`.";
            }

            if (!RepeatDaysSyntax.TryParse(repeatDays, out days, out var daysProblem, allowNoneDays))
            {
                return $"❌ {daysProblem}";
            }
        }

        rule = repeat switch
        {
            RepeatChoice.Daily => new RecurrenceRuleDto(RecurrenceUnit.Day, repeatEvery),
            RepeatChoice.Weekly => new RecurrenceRuleDto(RecurrenceUnit.Week, repeatEvery, DaysOfWeek: days),
            RepeatChoice.MonthlySameDate => new RecurrenceRuleDto(RecurrenceUnit.Month, repeatEvery),
            RepeatChoice.MonthlyNthWeekday => new RecurrenceRuleDto(RecurrenceUnit.Month, repeatEvery, MonthlyMode.NthWeekday),
            RepeatChoice.Yearly => new RecurrenceRuleDto(RecurrenceUnit.Year, repeatEvery),
            _ => null,
        };
        return null;
    }
}
