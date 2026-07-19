using CalCrony.Bot.Api;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class TemplateFinderTests
{
    [Fact]
    public void Resolves_by_id_exact_name_and_unambiguous_fragment()
    {
        var raid = Sample("Raid Night");
        var raidPrep = Sample("Raid Prep");
        var movies = Sample("Movies");
        var all = new List<EventTemplateDto> { raid, raidPrep, movies };

        Assert.Equal(raid.Id, TemplateFinder.PickSingle(all, raid.Id.ToString()).Template!.Id);
        Assert.Equal(raid.Id, TemplateFinder.PickSingle(all, "raid night").Template!.Id); // CI exact
        Assert.Equal(movies.Id, TemplateFinder.PickSingle(all, "mov").Template!.Id);      // fragment

        var ambiguous = TemplateFinder.PickSingle(all, "raid");
        Assert.Null(ambiguous.Template);
        Assert.Contains("Raid Night", ambiguous.Problem);
        Assert.Contains("Raid Prep", ambiguous.Problem);

        Assert.Contains("No template named", TemplateFinder.PickSingle(all, "nope").Problem);
        Assert.Contains("no longer exists", TemplateFinder.PickSingle(all, Guid.NewGuid().ToString()).Problem);
    }

    [Fact]
    public void Suggestions_filter_order_and_carry_markers()
    {
        var templates = new List<EventTemplateDto>
        {
            Sample("Zeta", recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), notificationCount: 2),
            Sample("Alpha"),
            Sample("alphabet"),
        };

        var suggestions = TemplateNameAutocompleteHandler.BuildSuggestions(templates, "alpha");

        Assert.Equal(2, suggestions.Count);
        Assert.StartsWith("Alpha", suggestions[0].Name);
        Assert.StartsWith("alphabet", suggestions[1].Name);

        var zeta = TemplateNameAutocompleteHandler.BuildSuggestions(templates, "zeta").Single();
        Assert.Contains("🔁", zeta.Name);
        Assert.Contains("🔔2", zeta.Name);
        Assert.True(Guid.TryParse((string)zeta.Value, out _));
        Assert.True(zeta.Name.Length <= 100);
    }

    private static EventTemplateDto Sample(
        string name, RecurrenceRuleDto? recurrence = null, int notificationCount = 0) => new(
        Guid.NewGuid(), 1, 2, name, $"{name} Title", null, 60, null, null, recurrence,
        [.. Enumerable.Range(0, notificationCount).Select(i => new TemplateNotificationDto(30 - i, null, null, null))],
        DateTimeOffset.UtcNow);
}
