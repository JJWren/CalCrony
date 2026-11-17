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
/// "sat" and "sun" standing as words of their own read as "saturday" and "sunday" do.
/// Where a date and time is asked for, a deadline ("by friday 6pm") reads as the date and time it
/// names, a weekday typed on its own day is next week's once its time is behind, and a time
/// written apart from its day ("fri (6pm)") is read on that day. Text that repeats ("every
/// friday") is refused in words that say so.
/// </summary>
/// <param name="clock">The time source.</param>
public sealed partial class NaturalDateTimeParser(IClock clock)
{
    private static readonly LocalDateTimePattern LocalPattern =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss");

    /// <summary>The refusal for date/time text that is missing altogether.</summary>
    private const string MissingError = "The date/time text is required.";

    /// <summary>The refusal for text that is not understood as a date and time.</summary>
    /// <param name="text">The text, as the refusal quotes it.</param>
    /// <returns>The user-facing error text.</returns>
    internal static string NotUnderstoodError(string text) => $"Could not understand \"{text}\" as a date/time.";

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

    /// <summary>Matches <c>sat</c> or <c>sun</c> standing as a word of its own, in any letter case —
    /// never inside a longer word ("saturday", "sunrise", "sats"). A word ends where the regex
    /// engine's <c>\b</c> ends it, which is where the recognizer's own weekday patterns end one.</summary>
    [GeneratedRegex(@"\b(?:(?<sat>sat)|sun)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShortWeekendDayRegex();

    /// <summary>The refusal for text over <see cref="DateTextLimits.MaxLength"/> characters. Unlike
    /// the other errors it does not quote the text.</summary>
    private static readonly string TooLongError =
        $"The date/time text must be at most {DateTextLimits.MaxLength} characters.";

    /// <summary>The recognizer's type for a phrase that repeats ("every friday", "weekly").</summary>
    private const string RepeatTypeName = "datetimeV2.set";

    /// <summary>The recognizer's type for a length of time ("2 hours", "30 min").</summary>
    private const string LengthTypeName = "datetimeV2.duration";

    /// <summary>The time of day a date takes when the text gives it none.</summary>
    private static readonly LocalTime BareDateTime = new(9, 0);

    /// <summary>How the recognizer begins the timex of a weekday whose week is left open:
    /// "XXXX-WXX-5" is any Friday.</summary>
    private const string OpenWeekTimex = "XXXX-WXX-";

    /// <summary>The refusal from <see cref="TryResolve"/> for text that repeats. The example in it
    /// is fixed, not taken from the text.</summary>
    /// <param name="text">The text as the other refusals of <see cref="TryResolve"/> quote it: as
    /// typed, less the zone abbreviation that chose the zone.</param>
    /// <returns>The user-facing error text.</returns>
    private static string RepeatError(string text) =>
        $"\"{text}\" repeats. Type one date and time here, for example \"saturday 8pm\"; to make an event repeat, use its repeat option.";

    /// <summary>The refusal from <see cref="TryResolveDay"/> for text that repeats.</summary>
    /// <param name="text">The text as it was typed.</param>
    /// <returns>The user-facing error text.</returns>
    private static string RepeatDayError(string text) =>
        $"\"{text}\" repeats. Type one day here, for example \"today\", \"saturday\" or \"nov 12\".";

    private readonly DateTimeModel model =
        new DateTimeRecognizer(Culture.English).GetDateTimeModel();

    /// <summary>The text the recognizer is given: <paramref name="text"/> with <c>sat</c> and
    /// <c>sun</c>, where they stand as words of their own, spelled out as <c>saturday</c> and
    /// <c>sunday</c>. The recognizer reads the other short weekdays wherever they stand, but these
    /// two only in some forms ("next sat", "on sun"); elsewhere it drops them and reads the rest,
    /// so "sat 6pm" would be today at 6 PM. Spelled out, each reads exactly as its full name does.
    /// Where the full name is refused ("last saturday"), so is the short form. Text that spelling
    /// out would take past <see cref="DateTextLimits.MaxLength"/> comes back as typed, with no
    /// short form spelled out, so the recognizer is never given more than the limit; text with
    /// neither short form comes back unchanged. Refusals go on quoting the text they quoted
    /// before, never the spelled-out text.</summary>
    /// <param name="text">The text to read, already within the limit: as typed, or in
    /// <see cref="TryResolve"/> with the zone abbreviation that chose the zone taken out.</param>
    /// <returns>The text to hand to the recognizer.</returns>
    private static string SpellOutWeekendDays(string text)
    {
        var spelledOut = ShortWeekendDayRegex().Replace(
            text, static match => match.Groups["sat"].Success ? "saturday" : "sunday");
        return spelledOut.Length <= DateTextLimits.MaxLength ? spelledOut : text;
    }

    // Named TryResolve (not TryParse) deliberately: ASP.NET minimal APIs treat any type
    // with a TryParse method as a bindable route/query primitive, which breaks DI injection.
    /// <summary>Resolves text to the nearest future instant in the given zone (or the zone a
    /// contained abbreviation selects). False with a user-facing error on over-long, unparseable,
    /// repeating or past-only input. Named TryResolve, not TryParse — minimal APIs treat TryParse
    /// types as bindable primitives, which breaks DI injection.
    /// <para>The recognizer returns one or more phrases, each with one or more values. A value is
    /// read as a day and a time, a day with no time of its own, or a time with no day of its own,
    /// and the readings are put together. A day alone is 9:00. A time alone is today's, or
    /// tomorrow's once today's has passed; but a day the text names outranks a today that is only
    /// implied, so a time written apart from its day ("fri (6pm)") is read on that day, as the two
    /// written together are. A part of the day ("friday night") takes such a time as its own, and
    /// begins at its start when there is none. Where a day already has its time ("friday 6pm, ends
    /// 9pm"), a further time is not read. A phrase that names no single day — a range that has
    /// begun ("this week" on a Wednesday) or a whole period longer than a week ("may") — is read by
    /// its start, and a time beside it stays a time alone. The earliest reading that is still
    /// ahead is the answer.</para></summary>
    /// <param name="text">The text to parse. Text that is missing (null) is refused.</param>
    /// <param name="zone">The IANA zone to resolve in.</param>
    /// <param name="result">The resolved future instant, when successful.</param>
    /// <param name="error">The user-facing error text.</param>
    /// <param name="referenceInstant">The "now" relative text resolves against. Callers parsing
    /// several texts in one request pass one captured instant so the results are mutually
    /// consistent — separate per-call clock reads can straddle a minute boundary and make
    /// "in 2 hours" and "in 120 minutes" resolve a minute apart. Null = read the clock.</param>
    /// <returns>True when text resolved to a future instant.</returns>
    public bool TryResolve(
        string? text, DateTimeZone zone, out Instant result, out string? error, Instant? referenceInstant = null)
    {
        result = default;
        error = null;

        // No text at all is refused in a sentence of its own.
        if (text is null)
        {
            error = MissingError;
            return false;
        }

        // Before anything reads the text: a date and a time take a few words, so longer text is
        // refused unread. The length is the text's as typed, a zone abbreviation included.
        if (text.Length > DateTextLimits.MaxLength)
        {
            error = TooLongError;
            return false;
        }

        var zoneRegex = ZoneAbbreviationRegex();
        var zoneMatch = zoneRegex.Match(text);
        if (zoneMatch.Success)
        {
            // The pattern finds the abbreviation and the table says which zone it means. Text
            // whose abbreviation the table has no zone for is refused as not understood.
            if (!ZoneAbbreviations.TryGetValue(zoneMatch.Value, out var zoneId))
            {
                error = NotUnderstoodError(text);
                return false;
            }

            if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId) is { } explicitZone)
            {
                zone = explicitZone;
                // Strip only the token that selected the zone — any further abbreviations in the
                // text ("6pm ET / 3pm PT") stay put for Recognizers rather than being silently
                // removed.
                text = zoneRegex.Replace(text, " ", 1).Trim();
            }
        }

        var now = referenceInstant ?? clock.GetCurrentInstant();
        var localNow = now.InZone(zone).LocalDateTime;
        var today = localNow.Date;

        var phrases = model.Parse(SpellOutWeekendDays(text), localNow.ToDateTimeUnspecified());

        // A repeat is not one date and time. It is refused wherever it stands, beside a date that
        // could be read too: reading the rest would drop the repeat and say nothing.
        if (phrases.Exists(IsRepeat))
        {
            error = RepeatError(text);
            return false;
        }

        // A deadline's end is read (ReadValue) unless the text holds a length of time: read by
        // its end, "2 hours before friday 6pm" would be Friday at 6 PM with the two hours lost.
        var readEnds = !phrases.Exists(phrase => phrase.TypeName == LengthTypeName);

        // What the text names, by what each reading carries: a day and a time together, a day
        // with no time of its own, or a time with no day of its own. The days are kept phrase by
        // phrase, because what a phrase can mean decides which of its days a time is read on.
        var dayTimes = new List<LocalDateTime>();
        var dayPhrases = new List<List<(LocalDate Day, bool NextWeek)>>();
        var times = new List<LocalTime>();
        var namesADayWithItsTime = false;
        var partsOfDay = new List<(List<(LocalDate Day, bool NextWeek)> Days, List<LocalDateTime> Starts)>();
        foreach (var phrase in phrases)
        {
            if (phrase.Resolution is null ||
                !phrase.Resolution.TryGetValue("values", out var rawValues) ||
                rawValues is not List<Dictionary<string, string>> values)
            {
                continue;
            }

            // A phrase that names no single day is read as it always was, by its start, and a time
            // beside it stays a time alone. A range that has begun and not ended ("this week" on a
            // Wednesday, "this weekend" on the Saturday, "tonight" at 9 PM) means the rest of it,
            // now; a whole period longer than a week ("may", "2027") is not the day meant.
            var namesNoDay = values.Exists(value => IsUnderWay(value, localNow) || IsWholePeriod(value));

            // A part of the day ("friday night", "tonight") that has not begun; one that is under
            // way names no day, as above. Whether its start is its time is decided once the whole
            // text has been read.
            var partOfDay = values.Exists(IsPartOfDay);

            var days = new List<(LocalDate Day, bool NextWeek)>();
            var starts = new List<LocalDateTime>();
            foreach (var reading in ReadPhrase(values, readEnds, today))
            {
                switch (reading)
                {
                    case { Day: { } day, Time: { } time } when namesNoDay:
                        dayTimes.Add(day + time);
                        break;
                    case { Day: { } day, Time: { } time } when partOfDay:
                        days.Add((day, reading.NextWeek));
                        starts.Add(day + time);
                        break;
                    case { Day: { } day, Time: { } time }:
                        dayTimes.Add(day + time);
                        namesADayWithItsTime = true;
                        break;
                    case { Day: { } day } when namesNoDay:
                        dayTimes.Add(day + BareDateTime);
                        break;
                    case { Day: { } day }:
                        days.Add((day, reading.NextWeek));
                        break;
                    case { Time: { } time }:
                        times.Add(time);
                        break;
                }
            }

            if (starts.Count > 0)
            {
                partsOfDay.Add((days, starts));
            }
            else if (days.Count > 0)
            {
                // A deadline that can mean today means today: "by jul 15" typed on July 15 is that
                // day, as "by today" is, and not July 15 of next year.
                var deadline = values.Exists(value => IsReadByItsEnd(value, readEnds));
                dayPhrases.Add(deadline ? TodayWhereNamed(days, today) : days);
            }
        }

        // A part of the day takes a time written beside it as its own ("tonight (7pm)", "friday
        // night (9pm)"), as it does when the two are written together. With none, it begins at its
        // start: "friday night" is Friday at 8 PM.
        foreach (var (partDays, partStarts) in partsOfDay)
        {
            if (times.Count > 0)
            {
                dayPhrases.Add(partDays);
            }
            else
            {
                dayTimes.AddRange(partStarts);
            }
        }

        var locals = new List<LocalDateTime>(dayTimes);
        if (times.Count > 0 && dayPhrases.Count > 0)
        {
            // A day the text names outranks a today that is only implied: a time written apart
            // from the day ("fri (6pm)", "friday from 6pm") is read on that day, as the same two
            // written together are, and the day then has no need of 9:00.
            locals.AddRange(dayPhrases
                .SelectMany(days => TodayWhereNamed(days, today))
                .SelectMany(named => times.Select(time => named.Day + time)));
        }
        else
        {
            // Here the text has days or times alone, never both. Each day is 9:00.
            locals.AddRange(dayPhrases.SelectMany(days => days).Select(named => named.Day + BareDateTime));

            // A time with no day named anywhere means today, or tomorrow once today's has passed.
            // Both are offered, and the future-preference filter below picks. Where a day has its
            // own time ("friday 6pm, ends 9pm") a further time is not read: alone it would be
            // today's, and the day that was named outranks that.
            if (!namesADayWithItsTime)
            {
                locals.AddRange(times.SelectMany(time => new[] { today + time, today.PlusDays(1) + time }));
            }
        }

        var candidates = locals.Select(local => local.InZoneLeniently(zone).ToInstant()).ToList();
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

    /// <summary>One recognizer value as <see cref="TryResolve"/> reads it: a day, a time of day,
    /// or both.</summary>
    /// <param name="Day">The day, when the value carries one.</param>
    /// <param name="Time">The time of day, when the value carries one.</param>
    /// <param name="NextWeek">True for the reading <see cref="ReadPhrase"/> adds for a weekday
    /// whose week is left open.</param>
    private readonly record struct MomentReading(LocalDate? Day, LocalTime? Time, bool NextWeek = false);

    /// <summary>Whether a recognizer value is a range that has begun and not ended: a range of
    /// days that began before today, as <see cref="ReadDay"/> judges it ("this week" on a
    /// Wednesday), or that begins today ("this weekend" on the Saturday, "mon to sat" on the
    /// Monday); or a range of times that holds the present moment ("tonight" at 9 PM, "this
    /// afternoon" at 1 PM). Such a phrase means the rest of the range and names no day.</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <param name="localNow">The present moment in the target zone.</param>
    /// <returns>True for a range under way.</returns>
    private static bool IsUnderWay(Dictionary<string, string> value, LocalDateTime localNow)
    {
        if (ReadDay(value, localNow.Date) is { Reading: DayReading.RangeUnderWay })
        {
            return true;
        }

        if (!value.TryGetValue("start", out var rawStart))
        {
            return false;
        }

        // A range of days whose first day is today began with the day.
        if (ParseDay(rawStart) is { Day: { } startDay, IsBareDate: true } && startDay == localNow.Date)
        {
            return true;
        }

        return LocalPattern.Parse(rawStart) is { Success: true } start
            && value.TryGetValue("end", out var rawEnd) && LocalPattern.Parse(rawEnd) is { Success: true } end
            && start.Value <= localNow && localNow < end.Value;
    }

    /// <summary>Whether a recognizer value is a whole period longer than a week ("may", "next
    /// month", "summer", "2027"): a range from one day to another more than seven days on, which
    /// the text does not spell out. It names no single day: in "6pm (may change)" the first of May
    /// is not the day meant. A span the text spells out ("jul 20 to jul 31") names its first day,
    /// however long it is, and so does a week.</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <returns>True for a whole period longer than a week.</returns>
    private static bool IsWholePeriod(Dictionary<string, string> value) =>
        !IsSpelledOutRange(value)
        && value.TryGetValue("start", out var rawStart) && ParseDay(rawStart) is { Day: { } start, IsBareDate: true }
        && value.TryGetValue("end", out var rawEnd) && ParseDay(rawEnd) is { Day: { } end, IsBareDate: true }
        && Period.DaysBetween(start, end) > 7;

    /// <summary>Whether a recognizer value is a part of the day ("friday night", "tonight",
    /// "tomorrow morning"), which the recognizer writes as a timex that ends in TMO, TAF, TEV or
    /// TNI: morning, afternoon, evening or night.</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <returns>True for a part of the day.</returns>
    private static bool IsPartOfDay(Dictionary<string, string> value) =>
        value.GetValueOrDefault("timex") is { } timex
        && (timex.EndsWith("TMO", StringComparison.Ordinal) || timex.EndsWith("TAF", StringComparison.Ordinal)
            || timex.EndsWith("TEV", StringComparison.Ordinal) || timex.EndsWith("TNI", StringComparison.Ordinal));

    /// <summary>Whether a recognized phrase is a repeat ("every friday", "fridays at 8pm",
    /// "weekly"), which carries no date of its own.</summary>
    /// <param name="phrase">One recognized phrase.</param>
    /// <returns>True for a repeat.</returns>
    private static bool IsRepeat(ModelResult phrase) => phrase.TypeName == RepeatTypeName;

    /// <summary>Reads the values of one recognized phrase for <see cref="TryResolve"/>. For a
    /// weekday whose week is left open ("wed", "wed 6pm", "friday night") the recognizer offers
    /// last week's and this week's, so on the day itself, once its time is behind, both have
    /// passed. Next week's is added: the text then means the next time that weekday and time
    /// come round, as a time alone already does.</summary>
    /// <param name="values">The phrase's resolution values.</param>
    /// <param name="readEnds">Whether a range with an end and no start is read as its end.</param>
    /// <param name="today">Today's local date in the target zone.</param>
    /// <returns>The readings, next week's among them where the phrase is such a weekday.</returns>
    private static IEnumerable<MomentReading> ReadPhrase(
        List<Dictionary<string, string>> values, bool readEnds, LocalDate today)
    {
        // The values of one way to read the phrase share a timex: "friday at 5" has two, the
        // morning's and the afternoon's, each with last week's value and this week's.
        foreach (var sameTimex in values.GroupBy(value => value.GetValueOrDefault("timex")))
        {
            var readings = sameTimex.Select(value => ReadValue(value, readEnds, today)).OfType<MomentReading>().ToList();
            foreach (var reading in readings)
            {
                yield return reading;
            }

            if (!IsWeekdayWithOpenWeek(sameTimex.Key))
            {
                continue;
            }

            // Two values a week apart are last week's and this week's. On any day but that Friday,
            // "fri 17", Friday the 17th, has the same timex, but its values are months apart, and
            // it is left as it is.
            var dated = readings.Where(reading => reading.Day is not null).OrderBy(reading => reading.Day).ToList();
            if (dated is [{ Day: { } lastWeeks }, { Day: { } thisWeeks } latest]
                && Period.DaysBetween(lastWeeks, thisWeeks) == 7)
            {
                yield return latest with { Day = thisWeeks.PlusWeeks(1), NextWeek = true };
            }
        }
    }

    /// <summary>Whether a timex names a weekday with the week left open — alone ("wed"), with a
    /// time ("wed 6pm"), with a part of the day ("friday night") or with a span of times on that
    /// day ("friday 6pm-8pm") — and not a span that runs from one weekday to another ("mon to
    /// sat", "fri 6pm to sun 6pm").</summary>
    /// <param name="timex">The timex of a recognizer value, if it has one.</param>
    /// <returns>True for a weekday with the week left open.</returns>
    private static bool IsWeekdayWithOpenWeek(string? timex)
    {
        if (timex is null)
        {
            return false;
        }

        if (timex.StartsWith(OpenWeekTimex, StringComparison.Ordinal))
        {
            return true;
        }

        // A span is written in parentheses, its start first and its end next:
        // "(XXXX-WXX-5T18,XXXX-WXX-5T20,PT2H)" is a Friday from 6 PM to 8 PM. It is one weekday
        // when it starts and ends on the same one.
        return timex.StartsWith('(')
            && timex[1..].Split(',') is [var start, var end, ..]
            && start.Length > OpenWeekTimex.Length && start.StartsWith(OpenWeekTimex, StringComparison.Ordinal)
            && end.Length > OpenWeekTimex.Length && end.StartsWith(OpenWeekTimex, StringComparison.Ordinal)
            && start[OpenWeekTimex.Length] == end[OpenWeekTimex.Length];
    }

    /// <summary>Reads one recognizer resolution value: a full date and time, a bare date, or a
    /// bare time. A range ("tomorrow evening", "after friday 6pm") is read as its start. A range
    /// with an end and no start ("by friday 6pm", "until saturday", "before noon") is read as its
    /// end, so that the text reads as the date and time it names, except where
    /// <see cref="IsReadByItsEnd"/> says not: at the edge of a whole period ("by the weekend") and
    /// beside a length of time ("2 hours before friday 6pm").</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <param name="readEnds">Whether a range with an end and no start is read as its end.</param>
    /// <param name="today">Today's local date in the target zone.</param>
    /// <returns>The reading; null when the value carries nothing usable.</returns>
    private static MomentReading? ReadValue(Dictionary<string, string> value, bool readEnds, LocalDate today)
    {
        if (!value.TryGetValue("type", out var type))
        {
            return null;
        }

        string? raw;
        if (!type.EndsWith("range", StringComparison.Ordinal))
        {
            value.TryGetValue("value", out raw);
        }
        else if (!value.TryGetValue("start", out raw) && IsReadByItsEnd(value, readEnds))
        {
            value.TryGetValue("end", out raw);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var full = LocalPattern.Parse(raw);
        if (full.Success)
        {
            // "end of day" ("eod", "by end of day") names no day: the recognizer assumes today and
            // writes today's last second. It is read as that time with no day of its own, so that
            // beside a day ("eod friday", "by end of day friday") it is that day's.
            return value.GetValueOrDefault("timex") == EndOfDayTimex(today)
                ? new MomentReading(null, full.Value.TimeOfDay)
                : new MomentReading(full.Value.Date, full.Value.TimeOfDay);
        }

        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            return new MomentReading(LocalDate.FromDateTime(dateOnly), null);
        }

        if (DateTime.TryParseExact(raw, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOnly))
        {
            return new MomentReading(null, LocalTime.FromTicksSinceMidnight(timeOnly.TimeOfDay.Ticks));
        }

        return null;
    }

    /// <summary>The timex the recognizer writes for "end of day": today's date and the day's last
    /// second.</summary>
    /// <param name="today">Today's local date in the target zone.</param>
    /// <returns>The timex text.</returns>
    private static string EndOfDayTimex(LocalDate today) => LocalDatePattern.Iso.Format(today) + "T23:59:59";

    /// <summary>Whether a range value with no start ends at a date or a time the text names
    /// ("by friday 6pm", "until saturday", "friday before 6pm") rather than at the edge of a whole
    /// period ("by the weekend", "before next month", "by end of month"). At an edge the
    /// recognizer's end is the first day of the period or the day after its last, and read as a
    /// bare date that would be 9:00 on a day the text does not mean; such text stays unread.</summary>
    /// <param name="value">One recognizer resolution value, a range with no start.</param>
    /// <returns>True when the end is a date or a time that was named.</returns>
    private static bool IsEndOfADateOrTime(Dictionary<string, string> value) =>
        value.GetValueOrDefault("Mod") is "before" or "until"
        && value.GetValueOrDefault("sourceEntity") != "datetimerange";

    /// <summary>Whether a recognizer value is read by its end: a range with an end and no start
    /// that ends at a date or a time the text names (<see cref="IsEndOfADateOrTime"/>), in text
    /// that holds no length of time.</summary>
    /// <param name="value">One recognizer resolution value.</param>
    /// <param name="readEnds">False when the text holds a length of time, which would be lost.</param>
    /// <returns>True when the value's end is what is read.</returns>
    private static bool IsReadByItsEnd(Dictionary<string, string> value, bool readEnds) =>
        readEnds
        && value.GetValueOrDefault("type") is { } type && type.EndsWith("range", StringComparison.Ordinal)
        && !value.ContainsKey("start")
        && IsEndOfADateOrTime(value);

    /// <summary>The days of one phrase, narrowed to today where the phrase can mean today. It then
    /// means today when a time is read on it — "nov 12 (9am)" typed on November 12 is that
    /// morning, as "nov 12 9am" is, although "nov 12" alone also offers next year's — and when it
    /// is a deadline: "by jul 15" typed on July 15 is that day. Next week's weekday stays, as it
    /// does for "wed 9am". A phrase that cannot mean today keeps all its days.</summary>
    /// <param name="days">The days of one phrase, each marked when it is the next week's that
    /// <see cref="ReadPhrase"/> added.</param>
    /// <param name="today">Today's local date in the target zone.</param>
    /// <returns>The days.</returns>
    private static List<(LocalDate Day, bool NextWeek)> TodayWhereNamed(
        List<(LocalDate Day, bool NextWeek)> days, LocalDate today) =>
        days.Exists(named => named.Day == today)
            ? days.Where(named => named.Day == today || named.NextWeek).ToList()
            : days;

    /// <summary>Resolves text to a calendar day in the given zone — the day-level sibling of
    /// <see cref="TryResolve"/>, for callers that want whole days. Today stays valid until it ends
    /// ("today" at 11 PM is today, where TryResolve's 09:00 has long passed) and any time of day in
    /// the text is ignored. Text can mean today without naming it — a time with no date ("6pm"), or
    /// a range that is already under way ("this week" on a Wednesday, meaning the rest of it) —
    /// but a day the text does name always outranks that: "friday at night" is Friday. A zone
    /// abbreviation in the text does not move the day — days are always the caller's. False with a
    /// user-facing error when the text is over-long, when it repeats, when nothing is recognized,
    /// or when the only days named have passed. A deadline ("by friday") is not read here: it
    /// names a last day, and the caller asks for a first one.</summary>
    /// <param name="text">The text to parse. Text that is missing (null) is refused.</param>
    /// <param name="zone">The IANA zone whose calendar days are meant.</param>
    /// <param name="day">The resolved day — today or later — when successful.</param>
    /// <param name="error">The user-facing error text.</param>
    /// <param name="referenceInstant">The "now" that decides which day is today. Null = read the clock.</param>
    /// <returns>True when text resolved to today or a later day.</returns>
    public bool TryResolveDay(
        string? text, DateTimeZone zone, out LocalDate day, out string? error, Instant? referenceInstant = null)
    {
        day = default;
        error = null;

        // As in TryResolve: no text at all is refused in a sentence of its own.
        if (text is null)
        {
            error = MissingError;
            return false;
        }

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
        var phrases = model.Parse(SpellOutWeekendDays(text), localNow.ToDateTimeUnspecified());

        // As in TryResolve: a repeat is not one day, wherever it stands in the text.
        if (phrases.Exists(IsRepeat))
        {
            error = RepeatDayError(text);
            return false;
        }

        foreach (var recognized in phrases)
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
