using CalCrony.Api.Data;
using CalCrony.Contracts;

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

    /// <summary>Range check for a duration in minutes (1 to four weeks); null passes.</summary>
    /// <param name="minutes">The submitted duration.</param>
    /// <returns>A 400 result when out of range, else null.</returns>
    public static IResult? BadDuration(int? minutes) =>
        minutes is < 1 or > FieldLimits.MaxMinutes
            ? Results.BadRequest(new ErrorResponse(
                $"The duration must be between 1 and {FieldLimits.MaxMinutes} minutes (4 weeks)."))
            : null;

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
