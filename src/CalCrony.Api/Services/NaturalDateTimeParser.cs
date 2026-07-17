using System.Globalization;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Services;

/// <summary>
/// Parses natural-language datetimes ("in 5 hours", "tomorrow 6pm", "6 PM on Wednesday")
/// using Microsoft.Recognizers.Text, resolved in a caller-supplied IANA time zone.
/// Always resolves to the nearest future occurrence; past-only results are rejected.
/// </summary>
public sealed class NaturalDateTimeParser(IClock clock)
{
    private static readonly LocalDateTimePattern LocalPattern =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss");

    private readonly DateTimeModel model =
        new DateTimeRecognizer(Culture.English).GetDateTimeModel();

    // Named TryResolve (not TryParse) deliberately: ASP.NET minimal APIs treat any type
    // with a TryParse method as a bindable route/query primitive, which breaks DI injection.
    public bool TryResolve(string text, DateTimeZone zone, out Instant result, out string? error)
    {
        result = default;
        error = null;

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
