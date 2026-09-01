namespace CalCrony.Contracts;

/// <summary>A set of weekdays a weekly rule fires on ("Tue + Thu"), as bit flags so the set
/// travels as one integer through JSON and the database. None means "no day set" — the rule
/// fires on its anchor's weekday only, which is the pre-day-set behaviour every stored series
/// already has.</summary>
[Flags]
public enum RecurrenceDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,

    /// <summary>The Mon–Fri preset.</summary>
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,

    /// <summary>Every flag that names a real day; anything outside this mask is invalid input.</summary>
    All = Weekdays | Saturday | Sunday,
}

/// <summary>Shared day-set helpers, so the API's summary, the bot's parser, and the web form's
/// live preview all speak the same "Tue, Thu" / "weekdays" language.</summary>
public static class RecurrenceDaySets
{
    /// <summary>The single-day flags in display order (Monday first — the week the interval
    /// cadence counts in starts on Monday, matching RFC 5545's default WKST).</summary>
    public static readonly IReadOnlyList<RecurrenceDays> Ordered =
    [
        RecurrenceDays.Monday,
        RecurrenceDays.Tuesday,
        RecurrenceDays.Wednesday,
        RecurrenceDays.Thursday,
        RecurrenceDays.Friday,
        RecurrenceDays.Saturday,
        RecurrenceDays.Sunday,
    ];

    /// <summary>Whether the value uses only real-day bits (None counts as valid: "no set").</summary>
    /// <param name="days">The day set to check.</param>
    /// <returns>True when every set bit names a weekday.</returns>
    public static bool IsValid(RecurrenceDays days) => (days & ~RecurrenceDays.All) == 0;

    /// <summary>The flag for a BCL weekday.</summary>
    /// <param name="day">The weekday.</param>
    /// <returns>The matching single-day flag.</returns>
    public static RecurrenceDays FromDayOfWeek(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => RecurrenceDays.Monday,
        DayOfWeek.Tuesday => RecurrenceDays.Tuesday,
        DayOfWeek.Wednesday => RecurrenceDays.Wednesday,
        DayOfWeek.Thursday => RecurrenceDays.Thursday,
        DayOfWeek.Friday => RecurrenceDays.Friday,
        DayOfWeek.Saturday => RecurrenceDays.Saturday,
        _ => RecurrenceDays.Sunday,
    };

    /// <summary>The BCL weekday of a single-day flag.</summary>
    /// <param name="day">A single-day flag (exactly one bit set).</param>
    /// <returns>The matching weekday.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The flag isn't a single day.</exception>
    public static DayOfWeek ToDayOfWeek(RecurrenceDays day) => day switch
    {
        RecurrenceDays.Monday => DayOfWeek.Monday,
        RecurrenceDays.Tuesday => DayOfWeek.Tuesday,
        RecurrenceDays.Wednesday => DayOfWeek.Wednesday,
        RecurrenceDays.Thursday => DayOfWeek.Thursday,
        RecurrenceDays.Friday => DayOfWeek.Friday,
        RecurrenceDays.Saturday => DayOfWeek.Saturday,
        RecurrenceDays.Sunday => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Expected a single-day flag."),
    };

    /// <summary>Whether the set includes a weekday.</summary>
    /// <param name="days">The day set.</param>
    /// <param name="day">The weekday to look for.</param>
    /// <returns>True when the day is in the set.</returns>
    public static bool Contains(RecurrenceDays days, DayOfWeek day) => (days & FromDayOfWeek(day)) != 0;

    /// <summary>The set's days in Monday-first order.</summary>
    /// <param name="days">The day set.</param>
    /// <returns>The single-day flags present in the set.</returns>
    public static IEnumerable<RecurrenceDays> Split(RecurrenceDays days) => Ordered.Where(d => (days & d) != 0);

    /// <summary>Three-letter label ("Tue").</summary>
    /// <param name="day">A single-day flag.</param>
    /// <returns>The abbreviated day name.</returns>
    public static string Abbreviate(RecurrenceDays day) => ToDayOfWeek(day).ToString()[..3];

    /// <summary>Human-readable set: "weekdays" for the Mon–Fri preset, otherwise "Tue, Thu";
    /// null for an empty set (the caller falls back to the anchor weekday).</summary>
    /// <param name="days">The day set.</param>
    /// <returns>The label, or null for None.</returns>
    public static string? Describe(RecurrenceDays days)
    {
        if (days == RecurrenceDays.None)
        {
            return null;
        }

        if (days == RecurrenceDays.Weekdays)
        {
            return "weekdays";
        }

        return string.Join(", ", Split(days).Select(Abbreviate));
    }
}
