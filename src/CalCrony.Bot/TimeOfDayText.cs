namespace CalCrony.Bot;

/// <summary>Display text for a time of day given as minutes from midnight — the form available
/// hours take on the wire.</summary>
public static class TimeOfDayText
{
    private const int MinutesPerDay = 1440;

    /// <summary>Formats a time of day on the 12-hour clock: "8 AM", "6:30 PM", "12 PM" for noon,
    /// "12 AM" for the start of a day. 1440 — the end of a day — is "midnight".</summary>
    /// <param name="minutes">Minutes from midnight, 0 to 1440.</param>
    /// <returns>The display text.</returns>
    public static string Format(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, MinutesPerDay);

        if (minutes == MinutesPerDay)
        {
            return "midnight";
        }

        var hour = minutes / 60;
        var minute = minutes % 60;
        var hourOnDial = hour % 12 == 0 ? 12 : hour % 12;
        var suffix = hour < 12 ? "AM" : "PM";
        return minute == 0 ? $"{hourOnDial} {suffix}" : $"{hourOnDial}:{minute:00} {suffix}";
    }
}
