using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The pure composition rules behind the action log and the CSV export: clipping,
/// summaries, details serialization, RFC 4180 quoting, and the one-row-per-RSVP model.</summary>
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

    [Fact]
    public void Csv_emits_one_row_per_rsvp_in_queue_order_and_one_row_for_rsvp_less_events()
    {
        var going = new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", IsAttending = true, Capacity = 1 };
        var busy = new Event
        {
            Id = Guid.NewGuid(), GuildId = 1, CreatorId = 9, Title = "Busy", StartsAt = Now, TimeZone = "UTC",
            DurationMinutes = 90, Location = "Hall, A", ChannelId = 5, Status = EventStatus.Scheduled,
            Options = [going],
            Rsvps =
            [
                new Rsvp { Id = Guid.NewGuid(), UserId = 2, OptionId = going.Id, Waitlisted = true, CreatedAt = Now.Plus(Duration.FromMinutes(5)) },
                new Rsvp { Id = Guid.NewGuid(), UserId = 1, OptionId = going.Id, CreatedAt = Now },
            ],
        };
        var quiet = new Event
        {
            Id = Guid.NewGuid(), GuildId = 1, CreatorId = 9, Title = "Quiet", StartsAt = Now, TimeZone = "UTC",
            ChannelId = 5, Status = EventStatus.Ended, SeriesId = Guid.NewGuid(),
        };

        var lines = CsvExport.BuildEventsCsv([busy, quiet]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(string.Join(",", CsvExport.EventColumns), lines[0]);
        Assert.Equal($"{busy.Id},Busy,2026-08-31T12:00:00Z,UTC,90,\"Hall, A\",Scheduled,,5,9,✅,Going,true,1,false,2026-08-31T12:00:00Z", lines[1]);
        Assert.Equal($"{busy.Id},Busy,2026-08-31T12:00:00Z,UTC,90,\"Hall, A\",Scheduled,,5,9,✅,Going,true,2,true,2026-08-31T12:05:00Z", lines[2]);
        Assert.Equal($"{quiet.Id},Quiet,2026-08-31T12:00:00Z,UTC,,,Ended,{quiet.SeriesId},5,9,,,,,,", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.Equal([0xEF, 0xBB, 0xBF], CsvExport.ToUtf8WithBom("").Take(3));
    }
}
