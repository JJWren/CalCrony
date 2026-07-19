using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Services;

/// <summary>
/// Parses natural-language datetimes ("in 5 hours", "tomorrow 6pm", "6 PM on Wednesday")
/// using Microsoft.Recognizers.Text, resolved in a caller-supplied IANA time zone. A trailing
/// zone abbreviation ("10am CST") overrides the caller zone. Always resolves to the nearest
/// future occurrence; past-only results are rejected.
/// </summary>
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

    private readonly DateTimeModel model =
        new DateTimeRecognizer(Culture.English).GetDateTimeModel();

    // Named TryResolve (not TryParse) deliberately: ASP.NET minimal APIs treat any type
    // with a TryParse method as a bindable route/query primitive, which breaks DI injection.
    /// <summary>Resolves text to the nearest future instant in the given zone (or the zone a
    /// contained abbreviation selects). False with a user-facing error on unparseable or
    /// past-only input. Named TryResolve, not TryParse — minimal APIs treat TryParse types
    /// as bindable primitives, which breaks DI injection.</summary>
    public bool TryResolve(string text, DateTimeZone zone, out Instant result, out string? error)
    {
        result = default;
        error = null;

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

        var now = clock.GetCurrentInstant();
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
}
