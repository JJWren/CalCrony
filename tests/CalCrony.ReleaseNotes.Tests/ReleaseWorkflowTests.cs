using System.Text;
using System.Text.Json;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The release workflow itself. A pull request's checks never run that workflow, so
/// these tests read its file: the announce job still has no access to the repository, still pings
/// the one role and nothing else, and builds the message the tool previews. Where bash, jq and
/// curl exist — the CI runner — the announce step's own script is then run against a stand-in
/// webhook on this machine, so what it would post is seen without posting anything.</summary>
public class ReleaseWorkflowTests
{
    private const string AnnounceJob = "announce-release";
    private const string SummaryJob = "release-summary";
    private const string PreviewJob = "release-summary-preview";
    private const string AnnounceStep = "Post release to Discord";
    private const string Tag = "v0.37.0";
    private const string ReleaseUrl = "https://github.com/JJWren/CalCrony/releases/tag/v0.37.0";

    private static readonly ReleaseWorkflow Workflow = ReleaseWorkflow.Load();

    [Fact]
    public void The_announce_job_has_no_permissions_no_checkout_and_one_step()
    {
        IReadOnlyList<string> job = Workflow.Job(AnnounceJob);

        Assert.Contains("    permissions: {}", job);
        Assert.DoesNotContain(job, line => line.Contains("uses:", StringComparison.Ordinal));
        Assert.DoesNotContain(job, line => line.Contains("github.token", StringComparison.Ordinal));
        Assert.DoesNotContain(job, line => line.Contains("GITHUB_TOKEN", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'));
        Assert.Single(job, line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_announce_job_gets_the_webhook_the_release_and_the_summary_text_and_nothing_else()
    {
        Assert.Equal(
            [
                "WEBHOOK_URL: ${{ secrets.DISCORD_RELEASE_WEBHOOK }}",
                "RELEASE_TAG: ${{ needs.release-please.outputs.tag_name }}",
                "RELEASE_URL: ${{ needs.release-please.outputs.html_url }}",
                $"SUMMARY: ${{{{ needs.release-summary.outputs.{Cli.DiscordSummaryOutput} }}}}",
            ],
            StepEnvironment(Workflow.Job(AnnounceJob)));
    }

    [Fact]
    public void The_announce_step_pings_the_one_role_and_allows_no_other_mention()
    {
        string script = Workflow.StepScript(AnnounceJob, AnnounceStep);
        string commands = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.Contains($"role_id=\"{Announcement.ReleasePingRoleId}\"\n", commands, StringComparison.Ordinal);
        Assert.Contains("allowed_mentions: {roles: [$role]}}'", commands, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(commands, "allowed_mentions"));
        Assert.DoesNotContain("parse", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("users", commands, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_line_the_step_posts_is_the_one_the_tool_previews()
    {
        string template = Assert.Single(
            Workflow.StepScript(AnnounceJob, AnnounceStep).Split('\n'),
            line => line.Contains("--arg content ", StringComparison.Ordinal));
        string quoted = template[(template.IndexOf('"', StringComparison.Ordinal) + 1)..template.LastIndexOf('"')];

        string firstLine = quoted
            .Replace("${role_id}", Announcement.ReleasePingRoleId, StringComparison.Ordinal)
            .Replace("${RELEASE_TAG}", Tag, StringComparison.Ordinal)
            .Replace("${RELEASE_URL}", ReleaseUrl, StringComparison.Ordinal);

        Assert.Equal(Announcement.FirstLine(Tag, ReleaseUrl), firstLine);
    }

    [Fact]
    public void The_step_is_the_one_on_master_with_the_summary_added()
    {
        // Every line of the step as it was is still there, in order, but for the line that built
        // the body, which now also takes the summary.
        const string OldBody = "  '{content: $content, allowed_mentions: {roles: [$role]}}' \\";
        string[] before = Repository.ReadFixture("announce-step-on-master.sh").Split('\n');
        List<string> now = Workflow.StepScript(AnnounceJob, AnnounceStep).TrimEnd('\n').Split('\n').ToList();

        Assert.Contains(OldBody, before);
        int position = 0;

        foreach (string line in before.Where(line => line != OldBody))
        {
            int found = now.IndexOf(line, position);
            Assert.True(found >= 0, $"The step no longer has this line, or has it out of order: {line}");
            position = found + 1;
        }
    }

    [Fact]
    public void The_announcement_waits_for_the_summary_job_without_requiring_it()
    {
        IReadOnlyList<string> job = Workflow.Job(AnnounceJob);

        Assert.Contains("    needs: [release-please, publish-images, release-summary]", job);
        Assert.Contains(
            "    if: ${{ !cancelled() && needs.release-please.outputs.release_created == 'true' && needs.publish-images.result == 'success' }}",
            job);
    }

    [Fact]
    public void The_summary_job_runs_for_a_release_and_may_edit_it_and_nothing_else()
    {
        IReadOnlyList<string> job = Workflow.Job(SummaryJob);

        Assert.Contains("    needs: release-please", job);
        Assert.Contains("    if: needs.release-please.outputs.release_created == 'true'", job);
        Assert.Equal(["contents: write"], Workflow.Block(SummaryJob, "permissions"));
    }

    [Fact]
    public void The_summary_job_hands_the_text_on_only_when_the_release_notes_got_it()
    {
        Assert.Equal(
            [$"{Cli.DiscordSummaryOutput}: ${{{{ steps.edit.outcome == 'success' && steps.render.outputs.{Cli.DiscordSummaryOutput} || '' }}}}"],
            Workflow.Block(SummaryJob, "outputs"));
        Assert.Contains($"        if: steps.render.outputs.{Cli.HasSummaryOutput} == 'true'", Workflow.Job(SummaryJob));
    }

    [Fact]
    public void The_summary_job_reads_the_release_then_renders_then_edits_it()
    {
        string render = Workflow.StepScript(SummaryJob, "Build the release texts");

        Assert.StartsWith("gh release view \"$RELEASE_TAG\" --json body --jq .body > \"$RUNNER_TEMP/notes.md\"\n", render, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project tools/ReleaseNotes -c Release -- render \\\n", render, StringComparison.Ordinal);
        Assert.Contains("--github-output \"$GITHUB_OUTPUT\"", render, StringComparison.Ordinal);
        Assert.Contains(
            "        run: gh release edit \"$RELEASE_TAG\" --notes-file \"$RUNNER_TEMP/release-notes.md\"",
            Workflow.Job(SummaryJob));
    }

    [Fact]
    public void The_preview_job_runs_when_nothing_is_released_and_may_read_and_comment_only()
    {
        IReadOnlyList<string> job = Workflow.Job(PreviewJob);

        Assert.Contains("    needs: release-please", job);
        Assert.Contains("    if: needs.release-please.outputs.release_created != 'true'", job);
        Assert.Equal(["contents: read", "pull-requests: write"], Workflow.Block(PreviewJob, "permissions"));
    }

    [Theory]
    [InlineData(SummaryJob)]
    [InlineData(PreviewJob)]
    public void The_new_jobs_leave_no_token_behind_in_their_checkout(string jobName)
    {
        IReadOnlyList<string> job = Workflow.Job(jobName);

        Assert.Single(job, line => line.Trim() == "- uses: actions/checkout@v4");
        Assert.Single(job, line => line.Trim() == "persist-credentials: false");
    }

    [Fact]
    public void The_preview_job_finds_its_comment_by_the_marker_the_tool_writes()
    {
        string script = Workflow.StepScript(PreviewJob, "Write or update the comment");

        Assert.Contains($"startswith(\"{PreviewComment.Marker}\")", script, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project tools/ReleaseNotes -c Release -- comment \\\n", script, StringComparison.Ordinal);
        Assert.Contains("--title \"$PR_TITLE\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_job_comments_on_release_pleases_own_pull_request_only()
    {
        string script = Workflow.StepScript(PreviewJob, "Find the open release PR");

        Assert.Contains("--head release-please--branches--master", script, StringComparison.Ordinal);
        Assert.Contains(".isCrossRepository == false and .author.login == \"app/github-actions\"", script, StringComparison.Ordinal);
    }

    [AnnounceStepFact]
    public async Task With_no_summary_the_step_posts_byte_for_byte_what_it_posted_before()
    {
        string before = Repository.ReadFixture("announce-step-on-master.sh") + "\n";

        ReceivedRequest then = await PostAsync(before, null);
        ReceivedRequest now = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), string.Empty);

        Assert.Equal(then.RequestLine.Split(' ')[0], now.RequestLine.Split(' ')[0]);
        Assert.Equal(then.Headers["Content-Type"], now.Headers["Content-Type"]);
        Assert.Equal(Encoding.UTF8.GetString(then.Body), Encoding.UTF8.GetString(now.Body));
        AssertBody(now, Announcement.FirstLine(Tag, ReleaseUrl));
    }

    [AnnounceStepFact]
    public async Task With_the_0_37_0_summary_the_step_posts_the_message_kept_beside_the_tests()
    {
        string summary = Repository.Read("docs/release-notes/0.37.0.md");

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), summary);

        Assert.StartsWith("POST /api/webhooks/1/not-a-real-token ", request.RequestLine, StringComparison.Ordinal);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        AssertBody(request, Repository.ReadFixture("0.37.0/discord-message.txt"));
    }

    [AnnounceStepFact]
    public async Task A_summary_arrives_character_for_character_whatever_it_contains()
    {
        string summary = "### What's new\n\n- \"double\" and 'single' quotes, back\\slash, $HOME, $(id), `ticks`, 100%, {braces}\n- tab\there, é, — and 🎉\n\n```\n/command option:value\n```";

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), summary);

        AssertBody(request, $"{Announcement.FirstLine(Tag, ReleaseUrl)}\n\n{summary}");
    }

    [AnnounceStepFact]
    public async Task A_message_of_exactly_two_thousand_characters_is_posted_whole()
    {
        string firstLine = Announcement.FirstLine(Tag, ReleaseUrl);
        string summary = new('a', Announcement.MaxContentLength - firstLine.Length - 2);

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), summary);

        AssertBody(request, $"{firstLine}\n\n{summary}");
    }

    [AnnounceStepFact]
    public async Task A_summary_that_would_not_fit_is_left_out_and_the_usual_line_is_posted()
    {
        string firstLine = Announcement.FirstLine(Tag, ReleaseUrl);
        string summary = new('a', Announcement.MaxContentLength - firstLine.Length - 1);

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), summary);

        AssertBody(request, firstLine);
    }

    [AnnounceStepFact]
    public async Task The_step_and_the_tool_agree_on_what_fits()
    {
        // Characters that take two UTF-16 units: both count them once.
        string firstLine = Announcement.FirstLine(Tag, ReleaseUrl);
        string summary = string.Concat(Enumerable.Repeat("🎉", Announcement.MaxContentLength - firstLine.Length - 2));

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), summary);

        AssertBody(request, Announcement.Content(Tag, ReleaseUrl, summary));
    }

    [AnnounceStepFact]
    public async Task Without_a_webhook_the_step_posts_nothing_and_succeeds()
    {
        StepResult step = await StepRunner.RunAsync(
            Workflow.StepScript(AnnounceJob, AnnounceStep),
            StepVariables(string.Empty, "### What's new\n\n- One change."));

        Assert.Equal(0, step.ExitCode);
        Assert.Equal("DISCORD_RELEASE_WEBHOOK secret not set; skipping announcement.\n", step.Output);
    }

    [AnnounceStepFact]
    public async Task A_post_discord_refuses_fails_the_step_as_it_always_has()
    {
        using FakeWebhook webhook = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Task<ReceivedRequest> receiving = webhook.ReceiveAsync(400, timeout.Token);

        StepResult step = await StepRunner.RunAsync(
            Workflow.StepScript(AnnounceJob, AnnounceStep),
            StepVariables(webhook.Url, string.Empty));

        await receiving;
        Assert.NotEqual(0, step.ExitCode);
    }

    /// <summary>Runs a version of the announce step against a stand-in webhook that answers as
    /// Discord does, and gives the request it received.</summary>
    /// <param name="script">The step's script.</param>
    /// <param name="summary">The summary handed to the step, or null to leave the variable unset
    /// as it was before the step knew of it.</param>
    /// <returns>The request the stand-in webhook received.</returns>
    private static async Task<ReceivedRequest> PostAsync(string script, string? summary)
    {
        using FakeWebhook webhook = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Task<ReceivedRequest> receiving = webhook.ReceiveAsync(204, timeout.Token);

        StepResult step = await StepRunner.RunAsync(script, StepVariables(webhook.Url, summary));

        Assert.True(step.ExitCode == 0, $"The step failed with exit code {step.ExitCode}: {step.Error}");
        Task finished = await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(finished == receiving, $"The step succeeded without posting anything. Its output: {step.Output}");

        return await receiving;
    }

    /// <summary>Builds the variables the workflow gives the announce step.</summary>
    /// <param name="webhookUrl">The address to post to, empty for a repository without the secret.</param>
    /// <param name="summary">The summary, or null to leave the variable unset.</param>
    /// <returns>The step's environment.</returns>
    private static Dictionary<string, string> StepVariables(string webhookUrl, string? summary)
    {
        Dictionary<string, string> variables = new()
        {
            ["WEBHOOK_URL"] = webhookUrl,
            ["RELEASE_TAG"] = Tag,
            ["RELEASE_URL"] = ReleaseUrl,
        };

        if (summary is not null)
        {
            variables["SUMMARY"] = summary;
        }

        return variables;
    }

    /// <summary>Checks a posted body: JSON with exactly the text expected and a ping allowed for
    /// the Release Ping role alone.</summary>
    /// <param name="request">The request the stand-in webhook received.</param>
    /// <param name="content">The text the message should have.</param>
    private static void AssertBody(ReceivedRequest request, string content)
    {
        using JsonDocument body = JsonDocument.Parse(request.Body);
        JsonElement root = body.RootElement;

        Assert.Equal(["content", "allowed_mentions"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(content, root.GetProperty("content").GetString());
        Assert.Equal(
            $"{{\"roles\":[\"{Announcement.ReleasePingRoleId}\"]}}",
            JsonSerializer.Serialize(root.GetProperty("allowed_mentions")));
    }

    /// <summary>Gives the variables of a job's only step, as the workflow file writes them.</summary>
    /// <param name="job">The job's lines.</param>
    /// <returns>The lines under the step's <c>env:</c>, trimmed, without comments.</returns>
    private static List<string> StepEnvironment(IReadOnlyList<string> job)
    {
        List<string> lines = job.ToList();
        int start = lines.FindIndex(line => line.Trim() == "env:");
        Assert.True(start >= 0, "The step has no env block.");

        return lines
            .Skip(start + 1)
            .TakeWhile(line => line.Trim() != "run: |")
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    /// <summary>Counts how often a text occurs in another.</summary>
    /// <param name="text">The text to search.</param>
    /// <param name="value">The text to count.</param>
    /// <returns>The number of occurrences.</returns>
    private static int Occurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
