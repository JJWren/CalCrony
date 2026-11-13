using System.Globalization;
using CalCrony.Contracts;

namespace CalCrony.Bot;

/// <summary>Text for a time of day given as minutes from midnight — the form available hours take
/// on the wire: how one is shown, and how a typed one is read.</summary>
public static class TimeOfDayText
{
    /// <summary>The most characters a typed time may have. The options that take one declare it,
    /// so Discord turns longer text down before the command runs.</summary>
    public const int MaxInputLength = 20;

    private const int MinutesPerHour = 60;
    private const int Noon = 12 * MinutesPerHour;

    /// <summary>Formats a time of day on the 12-hour clock: "8 AM", "6:30 PM", "12 PM" for noon,
    /// "12 AM" for the start of a day. 1440 — the end of a day — is "midnight".</summary>
    /// <param name="minutes">Minutes from midnight, 0 to 1440.</param>
    /// <returns>The display text.</returns>
    public static string Format(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, AvailableHoursLimits.MinutesPerDay);

        if (minutes == AvailableHoursLimits.MinutesPerDay)
        {
            return "midnight";
        }

        var hour = minutes / MinutesPerHour;
        var minute = minutes % MinutesPerHour;
        var hourOnDial = hour % 12 == 0 ? 12 : hour % 12;
        var suffix = hour < 12 ? "AM" : "PM";
        return minute == 0 ? $"{hourOnDial} {suffix}" : $"{hourOnDial}:{minute:00} {suffix}";
    }

    /// <summary>A time of day on the 24-hour clock, two digits each: "06:00", "18:30", and "24:00"
    /// for the end of a day. This is the value a suggestion sends, and <see cref="TryParse"/> reads
    /// it back to the same time.</summary>
    /// <param name="minutes">Minutes from midnight, 0 to 1440.</param>
    /// <returns>The 24-hour form.</returns>
    public static string To24Hour(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, AvailableHoursLimits.MinutesPerDay);

        return string.Create(CultureInfo.InvariantCulture, $"{minutes / MinutesPerHour:00}:{minutes % MinutesPerHour:00}");
    }

    /// <summary>Reads a typed time of day: the 12-hour clock ("6pm", "6:30 PM", "12am"), the
    /// 24-hour clock ("18:30", "18", and a bare "7" as 7 AM), "noon" and "midnight". Case and
    /// outer spaces do not matter. Midnight has two readings, and where the time stands decides:
    /// "midnight", "12am" and "0:00" are the start of the day as a start and the end of it as an
    /// end, and "24:00" is an end only. The step between selectable times is not checked here —
    /// the caller does that, so it can say what is wrong.</summary>
    /// <param name="text">The typed text, at most <see cref="MaxInputLength"/> characters.</param>
    /// <param name="asEnd">Whether the time ends a range rather than starts one.</param>
    /// <param name="minutes">The time as minutes from midnight, 0 to 1440, when it could be read.</param>
    /// <returns>Whether the text is a time of day.</returns>
    public static bool TryParse(string? text, bool asEnd, out int minutes)
    {
        minutes = 0;
        if (text is null || text.Length > MaxInputLength)
        {
            return false;
        }

        var typed = text.Trim().ToLowerInvariant();
        switch (typed)
        {
            case "":
                return false;
            case "noon":
                minutes = Noon;
                return true;
            case "midnight":
                minutes = asEnd ? AvailableHoursLimits.MinutesPerDay : 0;
                return true;
        }

        bool? afternoon = null;
        if (typed.EndsWith("am", StringComparison.Ordinal) || typed.EndsWith("pm", StringComparison.Ordinal))
        {
            afternoon = typed[^2] == 'p';
            typed = typed[..^2].TrimEnd();
        }

        var colon = typed.IndexOf(':', StringComparison.Ordinal);
        var hourText = colon < 0 ? typed : typed[..colon];
        var minuteText = colon < 0 ? null : typed[(colon + 1)..];
        if (!IsDigits(hourText, 1, 2) || (minuteText is not null && !IsDigits(minuteText, 2, 2)))
        {
            return false;
        }

        var hour = int.Parse(hourText, CultureInfo.InvariantCulture);
        var minute = minuteText is null ? 0 : int.Parse(minuteText, CultureInfo.InvariantCulture);
        if (minute >= MinutesPerHour)
        {
            return false;
        }

        if (afternoon is { } isAfternoon)
        {
            // On the 12-hour clock the hours run 1 to 12, and 12 is the first hour of its half.
            if (hour is < 1 or > 12)
            {
                return false;
            }

            hour = (hour % 12) + (isAfternoon ? 12 : 0);
        }
        else if (hour == 24)
        {
            // Only "24:00" names the end of a day, and only as an end.
            if (minute != 0 || !asEnd)
            {
                return false;
            }

            minutes = AvailableHoursLimits.MinutesPerDay;
            return true;
        }
        else if (hour > 23)
        {
            return false;
        }

        var value = (hour * MinutesPerHour) + minute;
        minutes = value == 0 && asEnd ? AvailableHoursLimits.MinutesPerDay : value;
        return true;
    }

    /// <summary>Whether the text is nothing but ASCII digits, within the given count.</summary>
    /// <param name="text">The text.</param>
    /// <param name="atLeast">The fewest digits.</param>
    /// <param name="atMost">The most digits.</param>
    /// <returns>True when it is.</returns>
    private static bool IsDigits(string text, int atLeast, int atMost) =>
        text.Length >= atLeast && text.Length <= atMost && text.All(char.IsAsciiDigit);
}
