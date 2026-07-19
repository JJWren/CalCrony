using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Contracts;
using Ical.Net;
using Ical.Net.DataTypes;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Translates a series' schedule into an RFC 5545 recurrence rule for the ICS feed.
/// The mapping is exact against RecurrenceCalculator's semantics: clamped day-of-month rules use
/// the BYMONTHDAY=28..d + BYSETPOS=-1 idiom (a plain BYMONTHDAY=31 would SKIP short months where
/// our engine clamps), and a 5th-weekday anchor maps to BYDAY=-1 (a 5th weekday, when it exists,
/// is always also the last).</summary>
public static class IcsRecurrence
{
    /// <summary>RRULE for a non-ended series, anchored on the emitted DTSTART instance.</summary>
    /// <param name="series">The series row.</param>
    /// <param name="anchorIsCounted">True when DTSTART is the live (already materialized and
    /// counted) occurrence; false when it is a computed next slot that hasn't spawned yet —
    /// this shifts the remaining-COUNT math by one.</param>
    /// <returns>The mapped recurrence pattern.</returns>
    public static RecurrencePattern BuildPattern(EventSeries series, bool anchorIsCounted)
    {
        var pattern = series.Unit switch
        {
            RecurrenceUnit.Day => new RecurrencePattern(FrequencyType.Daily, series.Interval),
            RecurrenceUnit.Week => WeeklyPattern(series),
            _ => MonthlyPattern(series),
        };

        if (series.UntilDate is { } until)
        {
            // Ical.Net 5.x requires a UTC Until; the inclusive local date becomes its final
            // instant in the series zone.
            var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
            var lastInstant = (until + new LocalTime(23, 59, 59)).InZoneLeniently(zone).ToInstant();
            pattern.Until = new CalDateTime(lastInstant.ToDateTimeUtc());
        }
        else if (series.MaxOccurrences is int max)
        {
            var remaining = max - series.OccurrenceCount + (anchorIsCounted ? 1 : 0);
            pattern.Count = Math.Max(1, remaining);
        }

        return pattern;
    }

    private static RecurrencePattern WeeklyPattern(EventSeries series)
    {
        var pattern = new RecurrencePattern(FrequencyType.Weekly, series.Interval);
        pattern.ByDay.Add(new WeekDay(ToDayOfWeek(series.AnchorDate.DayOfWeek)));
        return pattern;
    }

    private static RecurrencePattern MonthlyPattern(EventSeries series)
    {
        var pattern = new RecurrencePattern(FrequencyType.Monthly, series.Interval);
        if (series.MonthlyMode == MonthlyMode.DayOfMonth)
        {
            var day = series.AnchorDate.Day;
            if (day <= 28)
            {
                pattern.ByMonthDay.Add(day);
            }
            else
            {
                // Clamp semantics: the last existing day of {28..d} == min(d, month length).
                for (var d = 28; d <= day; d++)
                {
                    pattern.ByMonthDay.Add(d);
                }

                pattern.BySetPosition.Add(-1);
            }
        }
        else
        {
            // Same nth derivation as RecurrenceCalculator; 5th → last (-1) is exact.
            var nth = (series.AnchorDate.Day + 6) / 7;
            pattern.ByDay.Add(new WeekDay(ToDayOfWeek(series.AnchorDate.DayOfWeek), nth == 5 ? -1 : nth));
        }

        return pattern;
    }

    private static DayOfWeek ToDayOfWeek(IsoDayOfWeek iso) =>
        iso == IsoDayOfWeek.Sunday ? DayOfWeek.Sunday : (DayOfWeek)(int)iso;
}
