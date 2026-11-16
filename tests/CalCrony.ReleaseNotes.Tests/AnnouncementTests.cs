using System.Text.Json;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The Discord announcement as the tool builds it for the preview. The first line is the
/// one the release workflow has always posted, and a release without a summary is that line and
/// nothing more.</summary>
public class AnnouncementTests
{
    private const string Tag = "v0.38.0";
    private const string ReleaseUrl = "https://github.com/JJWren/CalCrony/releases/tag/v0.38.0";
    private const string FirstLine =
        "<@&1543943300132769802> **CalCrony v0.38.0** just shipped — release notes: https://github.com/JJWren/CalCrony/releases/tag/v0.38.0";

    [Fact]
    public void A_version_has_its_tag_and_its_release_page()
    {
        string tag = Announcement.TagFor(new ReleaseVersion(0, 38, 0));

        Assert.Equal(Tag, tag);
        Assert.Equal(ReleaseUrl, Announcement.ReleaseUrl(Cli.DefaultRepository, tag));
    }

    [Fact]
    public void The_first_line_mentions_the_role_names_the_version_and_links_the_notes()
    {
        Assert.Equal(FirstLine, Announcement.FirstLine(Tag, ReleaseUrl));
        Assert.Equal(130, FirstLine.Length);
    }

    [Fact]
    public void Without_a_summary_the_first_line_is_the_whole_message()
    {
        Assert.Equal(FirstLine, Announcement.Content(Tag, ReleaseUrl, string.Empty));
    }

    [Fact]
    public void A_summary_goes_under_the_first_line_after_a_blank_line()
    {
        Assert.Equal(
            $"{FirstLine}\n\n### What's new\n\n- One change.",
            Announcement.Content(Tag, ReleaseUrl, "### What's new\n\n- One change."));
    }

    [Fact]
    public void A_summary_at_its_limit_leaves_room_to_spare()
    {
        string content = Announcement.Content(Tag, ReleaseUrl, new string('a', SummaryRules.MaxLength));

        Assert.Equal(FirstLine.Length + 2 + SummaryRules.MaxLength, content.Length);
        Assert.True(content.Length <= Announcement.MaxContentLength);
    }

    [Fact]
    public void A_message_of_exactly_two_thousand_characters_still_carries_the_summary()
    {
        string summary = new('a', Announcement.MaxContentLength - FirstLine.Length - 2);

        Assert.Equal(Announcement.MaxContentLength, Announcement.Content(Tag, ReleaseUrl, summary).Length);
    }

    [Fact]
    public void A_summary_that_would_not_fit_is_left_out_rather_than_sent()
    {
        string summary = new('a', Announcement.MaxContentLength - FirstLine.Length - 1);

        Assert.Equal(FirstLine, Announcement.Content(Tag, ReleaseUrl, summary));
    }

    [Fact]
    public void The_fit_is_counted_in_code_points_as_the_job_counts_it()
    {
        // 1,868 characters that take two UTF-16 units each: 2,000 code points with the first line.
        string summary = string.Concat(Enumerable.Repeat("🎉", Announcement.MaxContentLength - FirstLine.Length - 2));

        string content = Announcement.Content(Tag, ReleaseUrl, summary);

        Assert.Equal(Announcement.MaxContentLength, Announcement.CountCodePoints(content));
        Assert.EndsWith("🎉", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_body_is_the_text_and_a_ping_for_the_one_role()
    {
        string summary = "### What's new\n\n- \"Quoted\", back\\slash, <angle> & `code`.";

        using JsonDocument body = JsonDocument.Parse(Announcement.Payload(Tag, ReleaseUrl, summary));
        JsonElement root = body.RootElement;

        Assert.Equal(["content", "allowed_mentions"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal($"{FirstLine}\n\n{summary}", root.GetProperty("content").GetString());

        JsonElement allowedMentions = root.GetProperty("allowed_mentions");
        Assert.Equal(["roles"], allowedMentions.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            [Announcement.ReleasePingRoleId],
            allowedMentions.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public void The_body_is_readable_as_it_stands()
    {
        string payload = Announcement.Payload(Tag, ReleaseUrl, string.Empty);

        Assert.Contains("just shipped — release notes", payload, StringComparison.Ordinal);
        Assert.Contains("\"content\": \"<@&1543943300132769802> **CalCrony v0.38.0**", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", payload, StringComparison.Ordinal);
    }
}
