using System.Globalization;
using System.Text;
using CalCrony.Contracts;
using NodaTime;
using NodaTime.Text;

namespace CalCrony.Api.Services;

/// <summary>Writes the server-manager CSV export of events and their RSVPs to a TextWriter, one
/// event at a time, so the endpoint can stream a guild's whole history without holding it in
/// memory. Pure text assembly (RFC 4180: CRLF rows, quote-when-needed, doubled inner quotes),
/// unit-testable without a database; the endpoint adds the UTF-8 BOM that makes Excel read
/// emoji option emotes correctly.
/// <para>Row order: events are emitted in <b>event id</b> order, not chronologically. The id is
/// the only key that cannot change while the export streams (a start time edited mid-download
/// would otherwise duplicate or drop the event), so exactly-once wins over pre-sorted output —
/// sort by <c>starts_at_utc</c> in the spreadsheet.</para>
/// <para>Formula injection: spreadsheet apps evaluate a cell that begins with <c>=</c>, <c>+</c>,
/// <c>-</c>, <c>@</c>, tab, CR, or LF as a formula, and titles, locations, and option
/// emotes/labels are member-controlled text. Those cells are neutralized by prefixing a single
/// quote (<c>'</c>) — the OWASP-recommended mitigation Excel, LibreOffice, and Google Sheets all
/// treat as "literal text" — before quoting. A title that legitimately starts with a dash (say
/// "-5°C night hike") therefore reads as <c>'-5°C night hike</c> in a spreadsheet; ids, numbers,
/// timestamps, and enum names are server-generated and never touched.</para></summary>
public static class CsvExport
{
    /// <summary>One event's columns, projected straight from the query (no navigation loads).</summary>
    /// <param name="Id">The event id.</param>
    /// <param name="Title">The event title.</param>
    /// <param name="StartsAt">The start instant.</param>
    /// <param name="TimeZone">The event's IANA zone.</param>
    /// <param name="DurationMinutes">Duration in minutes, when set.</param>
    /// <param name="Location">Optional location text.</param>
    /// <param name="Status">The lifecycle status.</param>
    /// <param name="SeriesId">The series id for recurring occurrences.</param>
    /// <param name="ChannelId">The Discord channel id.</param>
    /// <param name="CreatorId">The creating user's Discord id.</param>
    public sealed record EventRow(
        Guid Id,
        string Title,
        Instant StartsAt,
        string TimeZone,
        int? DurationMinutes,
        string? Location,
        EventStatus Status,
        Guid? SeriesId,
        long ChannelId,
        long CreatorId);

    /// <summary>One RSVP joined with its option, projected straight from the query.</summary>
    /// <param name="EventId">The event id.</param>
    /// <param name="Emote">The option emote.</param>
    /// <param name="Label">The option label.</param>
    /// <param name="IsAttending">Whether the option counts as attending.</param>
    /// <param name="UserId">The RSVPing user's Discord id.</param>
    /// <param name="Waitlisted">True while queued past the attending option's capacity.</param>
    /// <param name="CreatedAt">When the RSVP was made (doubles as waitlist queue order).</param>
    public sealed record RsvpRow(
        Guid EventId,
        string Emote,
        string Label,
        bool IsAttending,
        long UserId,
        bool Waitlisted,
        Instant CreatedAt);

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

    /// <summary>Characters that make a spreadsheet evaluate a cell as a formula when leading.</summary>
    private const string FormulaLeaders = "=+-@\t\r\n";

    /// <summary>Writes the header row.</summary>
    /// <param name="writer">The destination (CRLF rows are written explicitly).</param>
    public static void WriteHeader(TextWriter writer) => WriteRow(writer, EventColumns);

    /// <summary>Writes one event: a row per RSVP in the order given (callers pass queue order —
    /// creation time — which doubles as waitlist position), or a single row with empty RSVP
    /// columns when the event has none.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="ev">The event's columns.</param>
    /// <param name="rsvps">The event's RSVPs in queue order.</param>
    public static void WriteEvent(TextWriter writer, EventRow ev, IReadOnlyList<RsvpRow> rsvps)
    {
        var eventCells = new[]
        {
            ev.Id.ToString(),
            Text(ev.Title),
            FormatInstant(ev.StartsAt),
            ev.TimeZone,
            ev.DurationMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
            Text(ev.Location ?? ""),
            ev.Status.ToString(),
            ev.SeriesId?.ToString() ?? "",
            ev.ChannelId.ToString(CultureInfo.InvariantCulture),
            ev.CreatorId.ToString(CultureInfo.InvariantCulture),
        };

        if (rsvps.Count == 0)
        {
            WriteRow(writer, [.. eventCells, "", "", "", "", "", ""]);
            return;
        }

        foreach (var rsvp in rsvps)
        {
            WriteRow(writer,
            [
                .. eventCells,
                Text(rsvp.Emote),
                Text(rsvp.Label),
                FormatBool(rsvp.IsAttending),
                rsvp.UserId.ToString(CultureInfo.InvariantCulture),
                FormatBool(rsvp.Waitlisted),
                FormatInstant(rsvp.CreatedAt),
            ]);
        }
    }

    /// <summary>Renders a whole export to a string — the in-memory convenience for tests and
    /// small callers; the endpoint streams via <see cref="WriteHeader"/> and <see cref="WriteEvent"/>.</summary>
    /// <param name="events">Events with their RSVPs, in output order.</param>
    /// <returns>The CSV text (header included, CRLF rows, no BOM).</returns>
    public static string BuildEventsCsv(IEnumerable<(EventRow Event, IReadOnlyList<RsvpRow> Rsvps)> events)
    {
        using var writer = new StringWriter();
        WriteHeader(writer);
        foreach (var (ev, rsvps) in events)
        {
            WriteEvent(writer, ev, rsvps);
        }

        return writer.ToString();
    }

    /// <summary>Defuses a member-controlled cell that a spreadsheet would evaluate as a formula
    /// (leading <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab, CR, or LF) by prefixing a single
    /// quote; everything else passes through untouched. See the class remarks for the trade-off.</summary>
    /// <param name="field">The raw member-controlled text.</param>
    /// <returns>The text as it is safe to place in a cell (before RFC 4180 quoting).</returns>
    public static string Neutralize(string field) =>
        field.Length > 0 && FormulaLeaders.Contains(field[0]) ? "'" + field : field;

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

    /// <summary>A member-controlled text cell: formula-neutralized, then quoted like any other.</summary>
    private static string Text(string field) => Neutralize(field);

    private static void WriteRow(TextWriter writer, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(Quote(cells[i]));
        }

        writer.Write("\r\n");
    }

    /// <summary>ISO 8601 UTC to the second — what spreadsheets and scripts parse without fuss.</summary>
    private static string FormatInstant(Instant instant) => InstantPattern.General.Format(instant);

    private static string FormatBool(bool value) => value ? "true" : "false";
}
