using System.Globalization;
using System.Text.RegularExpressions;
using CalCrony.Contracts;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Services;

/// <summary>
/// Parses natural-language datetimes ("in 5 hours", "tomorrow 6pm", "6 PM on Wednesday")
/// using Microsoft.Recognizers.Text, resolved in a caller-supplied IANA time zone. A trailing
/// zone abbreviation ("10am CST") overrides the caller zone. Always resolves to the nearest
/// future occurrence; past-only results are rejected. Text longer than
/// <see cref="DateTextLimits.MaxLength"/> characters is refused before anything reads it.
/// </summary>
/// <param name="clock">The time source.</param>
public sealed partial class NaturalDateTimeParser(IClock clock)
{
    private static readonly LocalDateTimePattern LocalPattern =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss");

    // Recognizers silently ignores zone abbreviations, which would otherwise resolve the text in
    // the caller's zone — for unconfigured servers that's UTC, turning "10:00 AM CST" into 10:00
    // UTC (often "in the past"). Abbreviations map to the IANA zone, so a "CST" typed in July
    // correctly gets daylight-time rules — the wall-clock the user meant, not the strict offset.
    private static readonly Dictionary<string, string> ZoneAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "UTC",
        ["GMT"] = "UTC",
        ["EST"] = "America/New_York",
        ["EDT"] = "America/New_York",
        ["ET"] = "America/New_York",
        ["CST"] = "America/Chicago",
        ["CDT"] = "America/Chicago",
        ["CT"] = "America/Chicago",
        ["MST"] = "America/Denver",
        ["MDT"] = "America/Denver",
        ["MT"] = "America/Denver",
        ["PST"] = "America/Los_Angeles",
        ["PDT"] = "America/Los_Angeles",
        ["PT"] = "America/Los_Angeles",
        ["AKST"] = "America/Anchorage",
        ["AKDT"] = "America/Anchorage",
        ["HST"] = "Pacific/Honolulu",
    };

    /// <summary>Matches supported zone-abbreviation tokens on word boundaries.</summary>
    [GeneratedRegex(@"\b(UTC|GMT|AKST|AKDT|EST|EDT|CST|CDT|MST|MDT|PST|PDT|HST|ET|CT|MT|PT)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ZoneAbbreviationRegex();

    /// <summary>The refusal for text over <see cref="DateTextLimits.MaxLength"/> characters. Unlike
    /// the other errors it does not quote the text.</summary>
    private static readonly string TooLongError =
        $"The date/time text must be at most {DateTextLimits.MaxLength} characters.";

    private readonly DateTimeModel model =
        new DateTimeRecognizer(Culture.English).GetDateTimeModel();

    // Named TryResolve (not TryParse) deliberately: ASP.NET minimal APIs treat any type
    // with a TryParse method as a bindable route/query primitive, which breaks DI injection.
    /// <summary>Resolves text to the nearest future instant in the given zone (or the zone a
    /// contained abbreviation selects). False with a user-facing error on over-long, unparseable
    /// or past-only input. Named TryResolve, not TryParse — minimal APIs treat TryParse types
    /// as bindable primitives, which breaks DI injection.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="zone">The IANA zone to resolve in.</param>
    /// <param name="result">The resolved future instant, when successful.</param>
    /// <param name="error">The user-facing error text.</param>
    /// <param name="referenceInstant">The "now" relative text resolves against. Callers parsing
    /// several texts in one request pass one captured instant so the results are mutually
    /// consistent — separate per-call clock reads can straddle a minute boundary and make
    /// "in 2 hours" and "in 120 minutes" resolve a minute apart. Null = read the clock.</param>
    /// <returns>True when text resolved to a future instant.</returns>
    public bool TryResolve(
        string text, DateTimeZone zone, out Instant result, out string? error, Instant? referenceInstant = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        result = default;
        error = null;

        // Before anything reads the text: a date and a time take a few words, so longer text is
        // refused unread. The length is the text's as typed, a zone abbreviation included.
        if (text.Length > DateTextLimits.MaxLength)
        {
            error = TooLongError;
            return false;
        }

        var zoneRegex = ZoneAbbreviationRegex();
        var zoneMatch = zoneRegex.Match(text);
        if (zoneMatch.Success
            && DateTimeZoneProviders.Tzdb.GetZoneOrNull(ZoneAbbreviations[zoneMatch.Value]) is { } explicitZone)
        {
            zone = explicitZone;
            // Strip only the token that selected the zone — any further abbreviations in the text
            // ("6pm ET / 3pm PT") stay put for Recognizers rather than being silently removed.
            text = zoneRegex.Replace(text, " ", 1).Trim();
        }

        var now = referenceInstant ?? clock.GetCurrentInstant();
        var localNow = now.InZone(zone).LocalDateTime;
        var reference = localNow.ToDateTimeUnspecified();

        var candidates = new List<Instant>();
        foreach (var recognized in model.Parse(text, reference))
        {
            if (recognized.Resolution is null ||
                !recognized.Resolution.TryGetValue("values", out var rawValues) ||
                rawValues is not List<Dictionary<string, string>> values)
            {
                continue;
            }

            foreach (var value in values)
            {
                candidates.AddRange(ResolveValue(value, zone, localNow.Date));
            }
        }

        if (candidates.Count == 0)
        {
            error = $"Could not understand \"{text}\" as a date/time.";
            return false;
        }

        // Prefer the earliest future interpretation (e.g. "6pm Wednesday" resolves to next Wednesday,
        // not last week's). Small grace window tolerates "now"-ish inputs.
        var graceFloor = now.Minus(Duration.FromMinutes(1));
        var future = candidates.Where(c => c > graceFloor).OrderBy(c => c).ToList();
        if (future.Count == 0)
        {
            error = $"\"{text}\" resolves to a time in the past.";
            return false;
        }

        result = future[0];
        return true;
    }

    /// <summary>Turns one recognizer resolution into candidate instants (range starts, 9:00 for bare dates, today+tomorrow for bare times).</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <param name="zone">The IANA zone to resolve in.</param>
    /// <param name="today">Today's local date in the target zone.</param>
    /// <returns>Candidate instants for the resolution.</returns>
    private static IEnumerable<Instant> ResolveValue(
        Dictionary<string, string> value, DateTimeZone zone, LocalDate today)
    {
        if (!value.TryGetValue("type", out var type))
        {
            yield break;
        }

        // For ranges ("tomorrow evening") use the range start.
        var key = type.EndsWith("range", StringComparison.Ordinal) ? "start" : "value";
        if (!value.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var full = LocalPattern.Parse(raw);
        if (full.Success)
        {
            yield return full.Value.InZoneLeniently(zone).ToInstant();
            yield break;
        }

        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            // Bare dates ("next friday") default to 9:00 local rather than midnight.
            yield return LocalDateTime.FromDateTime(dateOnly).PlusHours(9).InZoneLeniently(zone).ToInstant();
            yield break;
        }

        if (DateTime.TryParseExact(raw, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOnly))
        {
            // Bare times ("6pm") anchor to today; also offer tomorrow so the
            // future-preference filter picks the next occurrence when today's has passed.
            var time = LocalTime.FromTicksSinceMidnight(timeOnly.TimeOfDay.Ticks);
            yield return (today + time).InZoneLeniently(zone).ToInstant();
            yield return (today.PlusDays(1) + time).InZoneLeniently(zone).ToInstant();
        }
    }

    /// <summary>Resolves text to a calendar day in the given zone — the day-level sibling of
    /// <see cref="TryResolve"/>, for callers that want whole days. Today stays valid until it ends
    /// ("today" at 11 PM is today, where TryResolve's 09:00 has long passed) and any time of day in
    /// the text is ignored. Text can mean today without naming it — a time with no date ("6pm"), or
    /// a range that is already under way ("this week" on a Wednesday, meaning the rest of it) —
    /// but a day the text does name always outranks that: "friday at night" is Friday. A zone
    /// abbreviation in the text does not move the day — days are always the caller's. False with a
    /// user-facing error when the text is over-long, when nothing is recognized, or when the only
    /// days named have passed.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="zone">The IANA zone whose calendar days are meant.</param>
    /// <param name="day">The resolved day — today or later — when successful.</param>
    /// <param name="error">The user-facing error text.</param>
    /// <param name="referenceInstant">The "now" that decides which day is today. Null = read the clock.</param>
    /// <returns>True when text resolved to today or a later day.</returns>
    public bool TryResolveDay(
        string text, DateTimeZone zone, out LocalDate day, out string? error, Instant? referenceInstant = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        day = default;
        error = null;

        // The same limit as TryResolve, and as there checked before anything reads the text.
        if (text.Length > DateTextLimits.MaxLength)
        {
            error = TooLongError;
            return false;
        }

        var now = referenceInstant ?? clock.GetCurrentInstant();
        var localNow = now.InZone(zone).LocalDateTime;
        var today = localNow.Date;

        // What each recognized phrase says, one phrase at a time. The recognizer offers several
        // readings of a phrase whose year or week is open ("saturday", "nov 5", "nov 11 to
        // nov 13"); they are alternatives for that phrase, not separate days.
        var daysAhead = new List<LocalDate>();
        var rangeUnderWay = false;
        var onlyPastDays = false;
        var timeAlone = false;
        foreach (var recognized in model.Parse(text, localNow.ToDateTimeUnspecified()))
        {
            if (recognized.Resolution is null ||
                !recognized.Resolution.TryGetValue("values", out var rawValues) ||
                rawValues is not List<Dictionary<string, string>> values)
            {
                continue;
            }

            var readings = values
                .Select(value => ReadDay(value, today))
                .OfType<(DayReading Reading, LocalDate? Day)>()
                .ToList();
            if (readings.Count == 0)
            {
                continue;
            }

            // If one reading is a range under way, that is what the phrase means: on Nov 13,
            // "nov 11 to nov 13" is this year's, not next year's.
            if (readings.Any(reading => reading.Reading == DayReading.RangeUnderWay))
            {
                rangeUnderWay = true;
                continue;
            }

            var named = readings.Select(reading => reading.Day).OfType<LocalDate>().ToList();
            if (named.Count == 0)
            {
                timeAlone = true;
                continue;
            }

            // The earliest reading that hasn't passed ("saturday" offers last week's and this
            // week's; "nov 5" offers this year's and next year's).
            var ahead = named.Where(candidate => candidate >= today).ToList();
            if (ahead.Count > 0)
            {
                daysAhead.Add(ahead.Min());
            }
            else
            {
                onlyPastDays = true;
            }
        }

        // A day the text names outranks a today that is only implied: "friday at night" is Friday
        // although "at night" alone would be tonight, and "friday, this week" is Friday.
        if (daysAhead.Count > 0)
        {
            day = daysAhead.Min();
            return true;
        }

        if (rangeUnderWay)
        {
            day = today;
            return true;
        }

        // A day that has passed is not rescued by a time beside it: "yesterday 6pm" is yesterday.
        if (onlyPastDays)
        {
            error = $"\"{text}\" is a day that has already passed.";
            return false;
        }

        if (timeAlone)
        {
            day = today;
            return true;
        }

        error = $"Could not understand \"{text}\" as a day. Try \"today\", \"saturday\" or \"nov 12\".";
        return false;
    }

    /// <summary>What one recognizer value says about the day.</summary>
    private enum DayReading
    {
        /// <summary>The value carries a date: that day is named.</summary>
        Named,

        /// <summary>A range that has begun and not ended — it means the rest of it, so today.</summary>
        RangeUnderWay,

        /// <summary>A time, or a stretch of times, with no date — it names no day, so today.</summary>
        TimeAlone,
    }

    /// <summary>Reads one recognizer resolution for its day: a date is itself, a date with a time
    /// is its date, a time alone names none, and a range is its first day — unless it has begun
    /// and not ended.</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <param name="today">Today's local date in the target zone.</param>
    /// <returns>The reading and, when a day is named, that day; null when the value carries
    /// nothing usable.</returns>
    private static (DayReading Reading, LocalDate? Day)? ReadDay(Dictionary<string, string> value, LocalDate today)
    {
        if (!value.TryGetValue("type", out var type))
        {
            return null;
        }

        var isRange = type.EndsWith("range", StringComparison.Ordinal);
        if (!value.TryGetValue(isRange ? "start" : "value", out var raw) || ParseDay(raw) is not { } start)
        {
            return null;
        }

        if (start.Day is not { } startDay)
        {
            return (DayReading.TimeAlone, null);
        }

        if (isRange && startDay < today && value.TryGetValue("end", out var rawEnd) && ParseDay(rawEnd) is { Day: { } endDay } end)
        {
            // The recognizer ends a calendar unit exclusively ("this week" ends on next Monday),
            // so its last day is the day before. A range the text spells out ("nov 11 to nov 13",
            // a timex in parentheses counted in days) ends on the last day that was named.
            var endsExclusively = end.IsBareDate && !IsSpelledOutRange(value);
            if (today <= (endsExclusively ? endDay.PlusDays(-1) : endDay))
            {
                return (DayReading.RangeUnderWay, null);
            }
        }

        return (DayReading.Named, startDay);
    }

    /// <summary>Whether a range value was spelled out by the text ("monday to friday", "nov 11-13")
    /// rather than being a calendar unit ("this week", "december", "last quarter"). The recognizer
    /// writes the former as a timex in parentheses whose length is counted in days —
    /// (start,end,PnD). A quarter is written in parentheses too, but its length is in months, and
    /// like every other calendar unit it ends exclusively.</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <returns>True for a spelled-out range.</returns>
    private static bool IsSpelledOutRange(Dictionary<string, string> value) =>
        value.TryGetValue("timex", out var timex)
        && timex.StartsWith('(')
        && timex.EndsWith("D)", StringComparison.Ordinal);

    /// <summary>Reads the day out of one recognizer value text: a full datetime, a bare date, or a
    /// bare time, which carries no day.</summary>
    /// <param name="raw">The recognizer's value text.</param>
    /// <returns>The day (null for a bare time) and whether the text was a bare date; null when it
    /// is none of the three forms.</returns>
    private static (LocalDate? Day, bool IsBareDate)? ParseDay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var full = LocalPattern.Parse(raw);
        if (full.Success)
        {
            return (full.Value.Date, false);
        }

        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            return (LocalDate.FromDateTime(dateOnly), true);
        }

        if (DateTime.TryParseExact(raw, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return (null, false);
        }

        return null;
    }
}
