using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CalCrony.ReleaseNotes;

/// <summary>The Discord announcement of a release, as the <c>announce-release</c> job of the
/// release workflow builds it. That job has no checkout and builds the message itself, in a few
/// lines of shell; this is the same message, for the preview and for the tests, which hold the two
/// together.</summary>
public static class Announcement
{
    /// <summary>The id of the Release Ping role in the community server. Ids are public; the
    /// webhook's address is the secret.</summary>
    public const string ReleasePingRoleId = "1543943300132769802";

    /// <summary>The most characters Discord takes in a message.</summary>
    public const int MaxContentLength = 2000;

    /// <summary>The tag release-please gives a version: "v0.38.0".</summary>
    /// <param name="version">The release.</param>
    /// <returns>The tag's name.</returns>
    public static string TagFor(ReleaseVersion version) => $"v{version}";

    /// <summary>The address of a release's page on GitHub.</summary>
    /// <param name="repository">The repository as owner and name: "JJWren/CalCrony".</param>
    /// <param name="tag">The release's tag.</param>
    /// <returns>The address the announcement links to.</returns>
    public static string ReleaseUrl(string repository, string tag) =>
        $"https://github.com/{repository}/releases/tag/{tag}";

    /// <summary>The announcement's first line, which is the whole announcement when a release has
    /// no summary: the role mention, the version, and the link to the release notes.</summary>
    /// <param name="tag">The release's tag.</param>
    /// <param name="releaseUrl">The address of the release's page.</param>
    /// <returns>The first line.</returns>
    public static string FirstLine(string tag, string releaseUrl) =>
        $"<@&{ReleasePingRoleId}> **CalCrony {tag}** just shipped — release notes: {releaseUrl}";

    /// <summary>The message's text: the first line, and under it the summary when there is one
    /// and the whole stays inside <see cref="MaxContentLength"/>, counted the way the job counts
    /// it, in Unicode code points. Otherwise the first line stands alone.</summary>
    /// <param name="tag">The release's tag.</param>
    /// <param name="releaseUrl">The address of the release's page.</param>
    /// <param name="discordSummary">The summary as <see cref="ReleaseTexts.DiscordSummary"/> gives
    /// it, or an empty string when the release has none.</param>
    /// <returns>The text of the message.</returns>
    public static string Content(string tag, string releaseUrl, string discordSummary)
    {
        ArgumentNullException.ThrowIfNull(discordSummary);
        string firstLine = FirstLine(tag, releaseUrl);
        string withSummary = $"{firstLine}\n\n{discordSummary}";
        bool carriesSummary = discordSummary.Length > 0 && CountCodePoints(withSummary) <= MaxContentLength;

        return carriesSummary ? withSummary : firstLine;
    }

    /// <summary>The body posted to the webhook: the text, and an <c>allowed_mentions</c> that
    /// names the Release Ping role, so that nothing else in the text can ping anyone.</summary>
    /// <param name="tag">The release's tag.</param>
    /// <param name="releaseUrl">The address of the release's page.</param>
    /// <param name="discordSummary">The summary for Discord, or an empty string.</param>
    /// <returns>The body as indented JSON.</returns>
    public static string Payload(string tag, string releaseUrl, string discordSummary)
    {
        using MemoryStream stream = new();
        JsonWriterOptions options = new()
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        using (Utf8JsonWriter writer = new(stream, options))
        {
            writer.WriteStartObject();
            writer.WriteString("content", Content(tag, releaseUrl, discordSummary));
            writer.WriteStartObject("allowed_mentions");
            writer.WriteStartArray("roles");
            writer.WriteStringValue(ReleasePingRoleId);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Counts a text's Unicode code points, so a character outside the basic plane
    /// counts once.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The number of code points.</returns>
    public static int CountCodePoints(string text)
    {
        int count = 0;

        foreach (Rune _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}
