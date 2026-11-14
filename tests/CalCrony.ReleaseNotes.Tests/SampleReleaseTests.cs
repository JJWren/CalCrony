using System.Text.Json;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>Release 0.37.0 as it would have gone out with its summary: the repository's own
/// <c>docs/release-notes/0.37.0.md</c> and the notes release-please really wrote for that release,
/// compared character for character with the texts kept beside these tests. Those files are what
/// the release notes and the Discord message look like; they were put together apart from the
/// tool, from the summary, the real notes and the announcement's first line.</summary>
public class SampleReleaseTests
{
    private const string Tag = "v0.37.0";
    private const string ReleaseUrl = "https://github.com/JJWren/CalCrony/releases/tag/v0.37.0";

    private static readonly ReleaseVersion Version = new(0, 37, 0);

    [Fact]
    public void The_summary_is_in_the_repository_and_keeps_the_rules()
    {
        ReleaseSummary? summary = ReleaseSummary.Find(Repository.Root, Version);

        Assert.NotNull(summary);
        Assert.Empty(SummaryRules.Check(summary.Text));
        Assert.Equal(706, summary.Text.Length);
    }

    [Fact]
    public void The_release_notes_are_the_summary_a_rule_and_the_real_notes()
    {
        string notes = ReleaseTexts.ReleaseNotes(Summary(), Repository.ReadFixture("0.37.0/release-please-notes.md"));

        Assert.Equal(Repository.ReadFixture("0.37.0/release-notes.md"), notes);
        Assert.StartsWith("### What's new\n\n- **Free slots.**", notes, StringComparison.Ordinal);
        Assert.Contains("\n\n---\n\n## [0.37.0](https://github.com/JJWren/CalCrony/compare/v0.36.4...v0.37.0) (2026-11-13)\n", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void The_discord_message_is_the_usual_line_then_the_summary()
    {
        string content = Announcement.Content(Tag, ReleaseUrl, ReleaseTexts.DiscordSummary(Summary()));

        Assert.Equal(Repository.ReadFixture("0.37.0/discord-message.txt"), content);
        Assert.Equal(838, content.Length);
    }

    [Fact]
    public void The_body_posted_to_the_webhook_is_the_kept_one()
    {
        string payload = Announcement.Payload(Tag, ReleaseUrl, ReleaseTexts.DiscordSummary(Summary()));

        Assert.Equal(Repository.ReadFixture("0.37.0/discord-payload.json"), payload);

        using JsonDocument body = JsonDocument.Parse(payload);
        Assert.Equal(Repository.ReadFixture("0.37.0/discord-message.txt"), body.RootElement.GetProperty("content").GetString());
        Assert.Equal(
            "{\"roles\":[\"1543943300132769802\"]}",
            JsonSerializer.Serialize(body.RootElement.GetProperty("allowed_mentions")));
    }

    /// <summary>Reads the repository's summary for 0.37.0.</summary>
    /// <returns>The summary's normalized text.</returns>
    private static string Summary() => Repository.Read(ReleaseSummary.PathFor(Version));
}
