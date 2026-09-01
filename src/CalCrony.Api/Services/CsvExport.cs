using System.Globalization;
using System.Text;
using CalCrony.Api.Data;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Services;

/// <summary>Builds the server-manager CSV export of events and their RSVPs. Pure text assembly
/// (RFC 4180: CRLF rows, quote-when-needed, doubled inner quotes) so the row model and quoting
/// are unit-testable without a database; the endpoint adds the UTF-8 BOM that makes Excel read
/// emoji option emotes correctly.</summary>
public static class CsvExport
{
    /// <summary>The header row, in column order. One row per RSVP with the event columns
    /// repeated; an event with no RSVPs still gets one row (RSVP columns empty) so every event
    /// the server has appears in the file.</summary>
    public static readonly IReadOnlyList<string> EventColumns =
    [
        "event_id",
        "title",
        "starts_at_utc",
        "time_zone",
        "duration_minutes",
        "location",
        "status",
        "series_id",
        "channel_id",
        "creator_id",
        "rsvp_option_emote",
        "rsvp_option_label",
        "rsvp_option_is_attending",
        "rsvp_user_id",
        "rsvp_waitlisted",
        "rsvp_created_utc",
    ];

    /// <summary>The UTF-8 byte-order mark prefix: without it Excel guesses a legacy code page
    /// and mangles emoji and accented titles.</summary>
    public static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Renders the export for the given events (options and RSVPs must be loaded).
    /// Events keep the order given; RSVP rows within an event follow RSVP creation order, which
    /// doubles as the waitlist queue position.</summary>
    /// <param name="events">The events to export, with Options and Rsvps loaded.</param>
    /// <returns>The CSV text (header included, CRLF rows, no BOM).</returns>
    public static string BuildEventsCsv(IEnumerable<Event> events)
    {
        var sb = new StringBuilder();
        AppendRow(sb, EventColumns);

        foreach (var ev in events)
        {
            var eventCells = new[]
            {
                ev.Id.ToString(),
                ev.Title,
                FormatInstant(ev.StartsAt),
                ev.TimeZone,
                ev.DurationMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
                ev.Location ?? "",
                ev.Status.ToString(),
                ev.SeriesId?.ToString() ?? "",
                ev.ChannelId.ToString(CultureInfo.InvariantCulture),
                ev.CreatorId.ToString(CultureInfo.InvariantCulture),
            };

            var options = ev.Options.ToDictionary(o => o.Id);
            var rsvps = ev.Rsvps.OrderBy(r => r.CreatedAt).ThenBy(r => r.UserId).ToList();
            if (rsvps.Count == 0)
            {
                AppendRow(sb, [.. eventCells, "", "", "", "", "", ""]);
                continue;
            }

            foreach (var rsvp in rsvps)
            {
                // An RSVP whose option vanished can't exist (options cascade the RSVPs away), but
                // a defensive empty cell beats a crash on a half-migrated row.
                options.TryGetValue(rsvp.OptionId, out var option);
                AppendRow(sb,
                [
                    .. eventCells,
                    option?.Emote ?? "",
                    option?.Label ?? "",
                    option is null ? "" : FormatBool(option.IsAttending),
                    rsvp.UserId.ToString(CultureInfo.InvariantCulture),
                    FormatBool(rsvp.Waitlisted),
                    FormatInstant(rsvp.CreatedAt),
                ]);
            }
        }

        return sb.ToString();
    }

    /// <summary>RFC 4180 field quoting: fields containing a comma, a double quote, or a line
    /// break are wrapped in double quotes with inner quotes doubled; everything else passes
    /// through untouched so plain ids and numbers stay readable in a text editor.</summary>
    /// <param name="field">The raw field value.</param>
    /// <returns>The field as it should appear in the row.</returns>
    public static string Quote(string field)
    {
        if (field.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Prefixes the CSV text with the UTF-8 BOM for download.</summary>
    /// <param name="csv">The CSV text.</param>
    /// <returns>The bytes to serve.</returns>
    public static byte[] ToUtf8WithBom(string csv) => [.. Utf8Bom, .. Encoding.UTF8.GetBytes(csv)];

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Quote(cells[i]));
        }

        sb.Append("\r\n");
    }

    /// <summary>ISO 8601 UTC to the second — what spreadsheets and scripts parse without fuss.</summary>
    private static string FormatInstant(Instant instant) => InstantPattern.General.Format(instant);

    private static string FormatBool(bool value) => value ? "true" : "false";
}
