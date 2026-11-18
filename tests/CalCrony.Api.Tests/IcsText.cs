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
        var value = Value(block, "DTSTART");
        var local = Basic.Parse(value.TrimEnd('Z')).Value;
        return value.EndsWith('Z') ? local.InUtc().ToInstant().InZone(zone).Date : local.Date;
    }

    /// <summary>How long one VEVENT runs, in minutes: its DTEND less its DTSTART, read as written.
    /// The feed writes both in the same form (UTC "…Z", or local time with a TZID).</summary>
    /// <param name="block">A VEVENT block from <see cref="Events"/>.</param>
    /// <returns>The minutes between DTSTART and DTEND.</returns>
    public static long LengthMinutes(string block)
    {
        var start = Basic.Parse(Value(block, "DTSTART").TrimEnd('Z')).Value;
        var end = Basic.Parse(Value(block, "DTEND").TrimEnd('Z')).Value;
        return Period.Between(start, end, PeriodUnits.Minutes).Minutes;
    }

    /// <summary>The value of the first line of a VEVENT block that begins with a property name:
    /// what follows its last colon.</summary>
    /// <param name="block">A VEVENT block from <see cref="Events"/>.</param>
    /// <param name="property">The property name the line begins with.</param>
    /// <returns>The value as written.</returns>
    private static string Value(string block, string property)
    {
        var line = block.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith(property, StringComparison.Ordinal));
        return line[(line.LastIndexOf(':') + 1)..];
    }
}
