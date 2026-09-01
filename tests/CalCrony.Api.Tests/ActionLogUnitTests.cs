using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The pure composition rules behind the action log and the CSV export: clipping
/// (surrogate-safe), summaries, details serialization, RFC 4180 quoting, formula
/// neutralization, and the one-row-per-RSVP model.</summary>
public class ActionLogUnitTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 31, 12, 0);

    [Fact]
    public void Quote_clips_long_titles_and_flattens_whitespace()
    {
        Assert.Equal("“Raid Night”", ActionLog.Quote("Raid Night"));
        Assert.Equal("“multi line title”", ActionLog.Quote("multi\r\n line\t title"));
        Assert.Equal("“”", ActionLog.Quote(null));

        var quoted = ActionLog.Quote(new string('x', 500));
        Assert.Equal(ActionLog.MaxQuotedLength + 2, quoted.Length);
        Assert.EndsWith("…”", quoted);
    }

    [Fact]
    public void Clip_never_splits_a_surrogate_pair()
    {
        // 98 chars, then an emoji (two UTF-16 units) straddling the cut at index 99, then more.
        var text = new string('a', 98) + "😀" + new string('b', 5);

        var clipped = ActionLog.Clip(text, 100);

        Assert.Equal(new string('a', 98) + "…", clipped); // the emoji is dropped whole, not halved
        Assert.DoesNotContain(clipped, c => char.IsSurrogate(c));

        // A pair that fits entirely inside the budget is kept.
        Assert.Equal("ab😀…", ActionLog.Clip("ab😀cdef", 5));
    }

    [Fact]
    public void Edit_summary_names_changed_fields_or_just_the_title()
    {
        var fields = ActionLog.Changed(("title", true), ("start", false), ("location", true));
        Assert.Equal(["title", "location"], fields);
        Assert.Equal("Edited “X” — title, location", ActionLog.EditSummary("Edited", "X", fields));
        Assert.Equal("Edited “X”", ActionLog.EditSummary("Edited", "X", []));
    }

    [Fact]
    public void Compose_bounds_summary_and_serializes_camel_case_details()
    {
        var actor = new ActionLog.Actor(42, ActionSource.Web);
        var entry = ActionLog.Compose(
            7, actor, ActionLogAction.EventEdited, ActionTargetType.Event, Guid.NewGuid(),
            new string('s', 1000), Now, new { Fields = new[] { "title" }, Scope = "Series" });

        Assert.Equal(FieldLimits.ActionSummary, entry.Summary.Length);
        Assert.Equal("""{"fields":["title"],"scope":"Series"}""", entry.DetailsJson);
        Assert.Equal(42, entry.ActorUserId);
        Assert.Equal(ActionSource.Web, entry.Source);
        Assert.Equal(Now, entry.CreatedAt);
        Assert.Null(ActionLog.Compose(7, actor, ActionLogAction.EventDeleted, ActionTargetType.Event, null, "x", Now).DetailsJson);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has \"quote\"", "\"has \"\"quote\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("⚔️ emoji", "⚔️ emoji")]
    public void Csv_quote_follows_rfc_4180(string input, string expected) =>
        Assert.Equal(expected, CsvExport.Quote(input));

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\t=1", "'\t=1")]
    [InlineData("\r=1", "'\r=1")]
    [InlineData("\n=1", "'\n=1")]
    [InlineData("Raid Night", "Raid Night")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    public void Csv_neutralize_defuses_formula_leading_cells(string input, string expected) =>
        Assert.Equal(expected, CsvExport.Neutralize(input));

    [Fact]
    public void Csv_neutralizes_member_text_cells_but_never_server_numbers()
    {
        // A legitimately dash-led title pays the documented price ('-5°C …); the numeric
        // duration column is server-generated and passes through untouched.
        var ev = new CsvExport.EventRow(Guid.NewGuid(), "-5°C night hike", Now, "UTC", 45, "=HYPERLINK(\"x\")", EventStatus.Scheduled, null, 5, 9);
        var rsvp = new CsvExport.RsvpRow(ev.Id, "+1", "@here", true, 2, false, Now);

        var line = CsvExport.BuildEventsCsv([(ev, [rsvp])]).Split("\r\n")[1];

        Assert.Equal($"{ev.Id},'-5°C night hike,2026-08-31T12:00:00Z,UTC,45,\"'=HYPERLINK(\"\"x\"\")\",Scheduled,,5,9,'+1,'@here,true,2,false,2026-08-31T12:00:00Z", line);
    }

    [Fact]
    public void Csv_emits_one_row_per_rsvp_in_the_order_given_and_one_row_for_rsvp_less_events()
    {
        var busy = new CsvExport.EventRow(Guid.NewGuid(), "Busy", Now, "UTC", 90, "Hall, A", EventStatus.Scheduled, null, 5, 9);
        var quiet = new CsvExport.EventRow(Guid.NewGuid(), "Quiet", Now, "UTC", null, null, EventStatus.Ended, Guid.NewGuid(), 5, 9);
        IReadOnlyList<CsvExport.RsvpRow> busyRsvps =
        [
            new(busy.Id, "✅", "Going", true, 1, false, Now),
            new(busy.Id, "✅", "Going", true, 2, true, Now.Plus(Duration.FromMinutes(5))),
        ];

        var lines = CsvExport.BuildEventsCsv([(busy, busyRsvps), (quiet, [])]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(string.Join(",", CsvExport.EventColumns), lines[0]);
        Assert.Equal($"{busy.Id},Busy,2026-08-31T12:00:00Z,UTC,90,\"Hall, A\",Scheduled,,5,9,✅,Going,true,1,false,2026-08-31T12:00:00Z", lines[1]);
        Assert.Equal($"{busy.Id},Busy,2026-08-31T12:00:00Z,UTC,90,\"Hall, A\",Scheduled,,5,9,✅,Going,true,2,true,2026-08-31T12:05:00Z", lines[2]);
        Assert.Equal($"{quiet.Id},Quiet,2026-08-31T12:00:00Z,UTC,,,Ended,{quiet.SeriesId},5,9,,,,,,", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.Equal([0xEF, 0xBB, 0xBF], CsvExport.ToUtf8WithBom("").Take(3));
    }
}
