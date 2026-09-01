using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Tests;

/// <summary>Small readers over serialized ICS text for assertions that need more than a
/// substring match.</summary>
internal static class IcsText
{
    private static readonly LocalDateTimePattern Basic = LocalDateTimePattern.CreateWithInvariantCulture("yyyyMMdd'T'HHmmss");

    /// <summary>The DTSTART of the VEVENT carrying <paramref name="uid"/>, as a local date in
    /// <paramref name="zone"/> (handles both the UTC "…Z" form and the TZID local form).</summary>
    /// <param name="ics">The serialized calendar.</param>
    /// <param name="uid">The VEVENT UID to look up.</param>
    /// <param name="zone">The zone to express a UTC DTSTART in.</param>
    /// <returns>The DTSTART calendar date.</returns>
    public static LocalDate DtStartDate(string ics, string uid, DateTimeZone zone) =>
        DtStartDate(Events(ics).First(b => b.Contains($"UID:{uid}")), zone);

    /// <summary>The unfolded text of every VEVENT block in the calendar.</summary>
    /// <param name="ics">The serialized calendar.</param>
    /// <returns>One string per VEVENT (the text after its BEGIN line).</returns>
    public static IEnumerable<string> Events(string ics) =>
        ics.Replace("\r\n ", "").Split("BEGIN:VEVENT").Skip(1);

    /// <summary>The DTSTART of one VEVENT block as a local date in <paramref name="zone"/>.</summary>
    /// <param name="block">A VEVENT block from <see cref="Events"/>.</param>
    /// <param name="zone">The zone to express a UTC DTSTART in.</param>
    /// <returns>The DTSTART calendar date.</returns>
    public static LocalDate DtStartDate(string block, DateTimeZone zone)
    {
        var line = block.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("DTSTART", StringComparison.Ordinal));
        var value = line[(line.LastIndexOf(':') + 1)..];
        var local = Basic.Parse(value.TrimEnd('Z')).Value;
        return value.EndsWith('Z') ? local.InUtc().ToInstant().InZone(zone).Date : local.Date;
    }
}
