using CalCrony.Contracts;
using CalCrony.Api.Data;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Pure, anchor-based recurrence math. Every candidate slot is computed from the series
/// anchor — never chained from a previous (possibly clamped) result — so "monthly on the 31st"
/// clamps to Feb 28 yet returns to Mar 31 without drifting.</summary>
public static class RecurrenceCalculator
{
    /// <summary>First schedule date strictly after <paramref name="after"/> — except when
    /// <paramref name="after"/> precedes the anchor, where the anchor (the first slot of the
    /// schedule) is returned as-is.</summary>
    public static LocalDate NextDate(
        RecurrenceUnit unit, int interval, MonthlyMode mode, LocalDate anchor, LocalDate after)
    {
        if (after < anchor)
        {
            return anchor;
        }

        switch (unit)
        {
            case RecurrenceUnit.Day:
            case RecurrenceUnit.Week:
            {
                var stepDays = unit == RecurrenceUnit.Day ? interval : interval * 7;
                var k = Period.DaysBetween(anchor, after) / stepDays + 1;
                var candidate = anchor.PlusDays(k * stepDays);
                while (candidate <= after)
                {
                    candidate = candidate.PlusDays(stepDays);
                }

                return candidate;
            }

            default:
            {
                var k = Math.Max(0, Period.Between(anchor, after, PeriodUnits.Months).Months / interval);
                var candidate = MonthCandidate(anchor, mode, k * interval);
                while (candidate <= after)
                {
                    k++;
                    candidate = MonthCandidate(anchor, mode, k * interval);
                }

                return candidate;
            }
        }
    }

    /// <summary>First (date, instant) with instant strictly after <paramref name="now"/> and date
    /// strictly after <paramref name="currentOccurrenceDate"/>; honors <paramref name="untilDate"/>
    /// (inclusive). Null = end condition exhausted. Skips over downtime-missed slots without
    /// materializing them.</summary>
    public static (LocalDate Date, Instant Instant)? NextOccurrence(
        RecurrenceUnit unit, int interval, MonthlyMode mode, LocalDate anchor, LocalTime startTime,
        DateTimeZone zone, LocalDate currentOccurrenceDate, LocalDate? untilDate, Instant now)
    {
        var cursor = currentOccurrenceDate;

        // Fast-forward a stale cursor (long downtime) so the loop below runs at most a few
        // times instead of one iteration per missed slot. The slot grid is anchored, so jumping
        // the cursor can't shift it; yesterday-in-zone keeps today's not-yet-due slot reachable.
        var yesterday = now.InZone(zone).Date.PlusDays(-1);
        if (cursor < yesterday)
        {
            cursor = yesterday;
        }

        while (true)
        {
            var next = NextDate(unit, interval, mode, anchor, cursor);
            if (untilDate is { } until && next > until)
            {
                return null;
            }

            // Lenient = the parser's DST policy: spring-forward gaps shift forward by the gap
            // length; fall-back ambiguity picks the earlier offset.
            var instant = (next + startTime).InZoneLeniently(zone).ToInstant();
            if (instant > now)
            {
                return (next, instant);
            }

            cursor = next;
        }
    }

    /// <summary>Human-readable rule, e.g. "Repeats every 2 weeks on Friday · 3 of 10".</summary>
    public static string Describe(EventSeries series)
    {
        var every = series.Interval == 1 ? null : $"every {series.Interval} ";
        var body = series.Unit switch
        {
            RecurrenceUnit.Day => series.Interval == 1 ? "daily" : $"every {series.Interval} days",
            RecurrenceUnit.Week =>
                $"{(every is null ? "weekly" : every + "weeks")} on {series.AnchorDate.DayOfWeek}",
            _ when series.MonthlyMode == MonthlyMode.DayOfMonth =>
                $"{(every is null ? "monthly" : every + "months")} on day {series.AnchorDate.Day}",
            _ =>
                $"{(every is null ? "monthly" : every + "months")} on the {NthLabel(series.AnchorDate)} {series.AnchorDate.DayOfWeek}",
        };

        var suffix = series.MaxOccurrences is int max
            ? $" · {series.OccurrenceCount} of {max}"
            : series.UntilDate is { } until
                ? $" · until {until:MMM d, yyyy}"
                : "";
        return $"Repeats {body}{suffix}";
    }

    /// <summary>The month-mode slot for anchor + monthsAhead: same day-of-month (clamped) or nth weekday (5th falls back to last).</summary>
    private static LocalDate MonthCandidate(LocalDate anchor, MonthlyMode mode, int monthsAhead)
    {
        if (mode == MonthlyMode.DayOfMonth)
        {
            // PlusMonths clamps per-candidate (Jan 31 + 1 → Feb 28), and because we always add to
            // the anchor the clamp never sticks (Jan 31 + 2 → Mar 31).
            return anchor.PlusMonths(monthsAhead);
        }

        var weekday = anchor.DayOfWeek;
        var nth = (anchor.Day + 6) / 7; // 1..5
        var month = anchor.PlusMonths(monthsAhead);
        var candidate = new LocalDate(month.Year, month.Month, 1)
            .With(DateAdjusters.NextOrSame(weekday))
            .PlusWeeks(nth - 1);
        // Months without a 5th such weekday get the last one instead.
        return candidate.Month == month.Month ? candidate : candidate.PlusWeeks(-1);
    }

    /// <summary>Ordinal label for the anchor's weekday position; a 5th weekday reads as "last".</summary>
    private static string NthLabel(LocalDate anchor) => ((anchor.Day + 6) / 7) switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        4 => "4th",
        _ => "last",
    };
}
