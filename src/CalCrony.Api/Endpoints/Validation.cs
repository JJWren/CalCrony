using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Shared request-field validation. Checks stay inline per endpoint (house style); this
/// just keeps the error sentences uniform.</summary>
internal static class Validation
{
    /// <summary>Length check for optional text fields; null/empty always passes.</summary>
    /// <param name="field">The user-facing field name for the error message.</param>
    /// <param name="value">The submitted value.</param>
    /// <param name="max">The maximum length (mirrors the column cap).</param>
    /// <returns>A 400 result when the value is too long, else null.</returns>
    public static IResult? TooLong(string field, string? value, int max) =>
        value is { Length: > 0 } && value.Length > max
            ? Results.BadRequest(new ErrorResponse($"The {field} must be at most {max} characters."))
            : null;

    /// <summary>The longest part of a submitted value that a refusal quotes back.</summary>
    public const int MaxQuoted = 64;

    /// <summary>The part of a submitted value that a refusal quotes back: the whole value up to
    /// <see cref="MaxQuoted"/> characters; a longer one cut there, without splitting a character,
    /// and marked with "…".</summary>
    /// <param name="value">The submitted value.</param>
    /// <returns>The text to quote; empty for a missing value.</returns>
    public static string Quotable(string? value)
    {
        if (value is null || value.Length <= MaxQuoted)
        {
            return value ?? string.Empty;
        }

        var cut = char.IsHighSurrogate(value[MaxQuoted - 1]) ? MaxQuoted - 1 : MaxQuoted;
        return string.Concat(value.AsSpan(0, cut), "…");
    }

    /// <summary>Range check for a duration in minutes (1 to four weeks); null passes.</summary>
    /// <param name="minutes">The submitted duration.</param>
    /// <returns>A 400 result when out of range, else null.</returns>
    public static IResult? BadDuration(int? minutes) =>
        minutes is < 1 or > FieldLimits.MaxMinutes
            ? Results.BadRequest(new ErrorResponse(
                $"The duration must be between 1 and {FieldLimits.MaxMinutes} minutes (4 weeks)."))
            : null;

    /// <summary>Length check for a repeating event: an occurrence may not last as long as the
    /// fewest days between two dates of its schedule (<see cref="RecurrenceCalculator.ShortestGapDays"/>),
    /// or longer. The scheduler posts a series' next occurrence only once the one before has
    /// ended, so the date such an occurrence runs into would never be posted. Used where the
    /// length is chosen: at creation and on a change of the whole series' length.</summary>
    /// <param name="minutes">The occurrences' length in minutes; null means
    /// <see cref="EventTimes.DefaultLengthMinutes"/>.</param>
    /// <param name="unit">The schedule's unit.</param>
    /// <param name="interval">Every N units.</param>
    /// <param name="mode">The monthly mode.</param>
    /// <param name="days">The weekly day set.</param>
    /// <param name="anchor">The schedule's anchor date.</param>
    /// <returns>A 400 result that names the longest length the schedule allows, else null.</returns>
    public static IResult? LengthReachesNextDate(
        int? minutes, RecurrenceUnit unit, int interval, MonthlyMode mode, RecurrenceDays days, LocalDate anchor)
    {
        var gap = GapMinutes(unit, interval, mode, days, anchor);
        return (minutes ?? EventTimes.DefaultLengthMinutes) < gap
            ? null
            : Results.BadRequest(new ErrorResponse(
                "An occurrence would still be running when the next date begins, so that date would never be posted: "
                + $"with this schedule an occurrence can last at most {Length(gap - 1)}."));
    }

    /// <summary>The same check where the schedule is chosen, for a series whose length is already
    /// set: its length may not reach the fewest days between two dates of the new schedule.</summary>
    /// <param name="minutes">The series' length in minutes; null means
    /// <see cref="EventTimes.DefaultLengthMinutes"/>.</param>
    /// <param name="unit">The new schedule's unit.</param>
    /// <param name="interval">Every N units.</param>
    /// <param name="mode">The monthly mode.</param>
    /// <param name="days">The weekly day set.</param>
    /// <param name="anchor">The schedule's anchor date.</param>
    /// <param name="reviving">True when the change would revive an ended series, whose length can
    /// no longer be changed: the refusal then asks only for dates further apart.</param>
    /// <returns>A 400 result that names the series' length and the longest the schedule allows,
    /// else null.</returns>
    public static IResult? ScheduleReachesLength(
        int? minutes, RecurrenceUnit unit, int interval, MonthlyMode mode, RecurrenceDays days, LocalDate anchor,
        bool reviving)
    {
        var length = minutes ?? EventTimes.DefaultLengthMinutes;
        var gap = GapMinutes(unit, interval, mode, days, anchor);
        return length < gap
            ? null
            : Results.BadRequest(new ErrorResponse(
                $"An occurrence of this series lasts {Length(length)}, so with this schedule it would still be running "
                + "when the next date begins, and that date would never be posted: this schedule allows at most "
                + $"{Length(gap - 1)}. "
                + (reviving
                    ? "Choose dates further apart to start it again."
                    : "Make the occurrences shorter first, or choose dates further apart.")));
    }

    /// <summary>The fewest minutes between two dates of a schedule, a day being 24 hours.</summary>
    private static long GapMinutes(RecurrenceUnit unit, int interval, MonthlyMode mode, RecurrenceDays days, LocalDate anchor) =>
        RecurrenceCalculator.ShortestGapDays(unit, interval, mode, days, anchor) * 24 * 60;

    /// <summary>A length in words: "1 day 12 h", "6 days 23 h 59 min", "45 min".</summary>
    /// <param name="minutes">The length in minutes; at least one.</param>
    /// <returns>The words.</returns>
    private static string Length(long minutes)
    {
        var parts = new List<string>(3);
        var days = minutes / (24 * 60);
        if (days > 0)
        {
            parts.Add(days == 1 ? "1 day" : $"{days} days");
        }

        if (minutes % (24 * 60) / 60 is var hours and > 0)
        {
            parts.Add($"{hours} h");
        }

        if (minutes % 60 is var rest and > 0)
        {
            parts.Add($"{rest} min");
        }

        return string.Join(' ', parts);
    }

    /// <summary>Day-set check for a repeat rule: the flags must name real days, and a non-empty
    /// set only makes sense on a weekly rule (a daily/monthly/yearly rule has no week to pick
    /// days from). None always passes.</summary>
    /// <param name="unit">The rule's unit.</param>
    /// <param name="days">The submitted day set.</param>
    /// <returns>A 400 result when the set is invalid for the unit, else null.</returns>
    public static IResult? BadRecurrenceDays(RecurrenceUnit unit, RecurrenceDays days)
    {
        if (!RecurrenceDaySets.IsValid(days))
        {
            return Results.BadRequest(new ErrorResponse("Repeat days must be a set of weekdays (Mon–Sun)."));
        }

        if (days != RecurrenceDays.None && unit != RecurrenceUnit.Week)
        {
            return Results.BadRequest(new ErrorResponse("Repeat days only apply to weekly repeats."));
        }

        return null;
    }
}
