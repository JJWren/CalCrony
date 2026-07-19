using CalCrony.Bot.Api;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class EventFinderTests
{
    [Fact]
    public void Exact_title_match_wins_over_fragment_matches()
    {
        // Joshua's live repro: "Test" was unreachable because it's a substring of "TestTitle".
        var events = new List<EventDto> { Sample("Test"), Sample("TestTitle") };

        var (ev, problem) = EventFinder.PickSingle(events, "Test", includePast: false);

        Assert.Null(problem);
        Assert.Equal("Test", ev!.Title);
    }

    [Fact]
    public void Ambiguous_fragments_without_exact_match_still_list_candidates()
    {
        var events = new List<EventDto> { Sample("Raid Alpha"), Sample("Raid Beta") };

        var (ev, problem) = EventFinder.PickSingle(events, "Raid", includePast: false);

        Assert.Null(ev);
        Assert.Contains("Raid Alpha", problem);
        Assert.Contains("Raid Beta", problem);
    }

    [Fact]
    public void Duplicate_exact_titles_stay_ambiguous()
    {
        var events = new List<EventDto> { Sample("Test"), Sample("Test") };

        var (ev, problem) = EventFinder.PickSingle(events, "Test", includePast: false);

        Assert.Null(ev);
        Assert.Contains("Multiple events", problem);
    }

    [Fact]
    public void Suggestions_order_upcoming_first_then_recent_past_with_marker()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new List<EventDto>
        {
            Sample("Old raid", now.AddDays(-10)),
            Sample("Older raid", now.AddDays(-20)),
            Sample("Soon raid", now.AddHours(2)),
            Sample("Later raid", now.AddDays(3)),
            Sample("Unrelated", now.AddHours(1)),
        };

        var suggestions = EventNameAutocompleteHandler.BuildSuggestions(events, "raid", now);

        Assert.Equal(4, suggestions.Count);
        Assert.StartsWith("Soon raid", suggestions[0].Name);
        Assert.StartsWith("Later raid", suggestions[1].Name);
        Assert.StartsWith("Old raid", suggestions[2].Name);
        Assert.Contains("(past)", suggestions[2].Name);
        Assert.StartsWith("Older raid", suggestions[3].Name);

        // Values are event ids, which FindSingleAsync resolves directly.
        Assert.True(Guid.TryParse((string)suggestions[0].Value, out _));
    }

    [Fact]
    public void Suggestions_cap_at_discords_25()
    {
        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, 30).Select(i => Sample($"Event {i}", now.AddHours(i + 1))).ToList();

        Assert.Equal(25, EventNameAutocompleteHandler.BuildSuggestions(events, "", now).Count);
    }

    private static EventDto Sample(string title, DateTimeOffset? startsAt = null) => new(
        Guid.NewGuid(), 1, 2, title, null, startsAt ?? DateTimeOffset.UtcNow.AddHours(3), "UTC", 60,
        3, null, null, null, EventStatus.Scheduled, [], []);
}
