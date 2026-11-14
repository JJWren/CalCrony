using System.Text;
using System.Text.Json;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The release workflow itself. A pull request's checks never run that workflow, so
/// these tests read its file. They hold the announce job to what it was — no access to the
/// repository, a ping for the one role and nothing else, the message the tool previews — and
/// they pin how the two new jobs are wired, because a slip there would not fail a release: it
/// would quietly switch the summary off. Where bash, jq and curl exist — the CI runner — the
/// announce step's own script is then run against a stand-in webhook on this machine, so what it
/// would post is seen without posting anything.</summary>
public class ReleaseWorkflowTests
{
    private const string ImagesJob = "publish-images";
    private const string AnnounceJob = "announce-release";
    private const string SummaryJob = "release-summary";
    private const string PreviewJob = "release-summary-preview";
    private const string AnnounceStep = "Post release to Discord";
    private const string Token = "GH_TOKEN: ${{ github.token }}";
    private const string Repo = "GH_REPO: ${{ github.repository }}";
    private const string PullRequestFound = "if: steps.pr.outputs.number != ''";
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
        Assert.Equal([$"- name: {AnnounceStep}"], Workflow.StepLabels(AnnounceJob));
    }

    [Fact]
    public void The_announce_step_gets_the_webhook_the_release_and_the_summary_text_and_nothing_else()
    {
        Assert.Equal(
            [
                $"- name: {AnnounceStep}",
                "shell: bash",
                "env:",
                "WEBHOOK_URL: ${{ secrets.DISCORD_RELEASE_WEBHOOK }}",
                "RELEASE_TAG: ${{ needs.release-please.outputs.tag_name }}",
                "RELEASE_URL: ${{ needs.release-please.outputs.html_url }}",
                $"SUMMARY: ${{{{ needs.{SummaryJob}.outputs.{Cli.DiscordSummaryOutput} }}}}",
                "run: |",
            ],
            Workflow.Step(AnnounceJob, $"- name: {AnnounceStep}"));
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
    public void The_step_is_the_one_before_summaries_with_the_summary_added()
    {
        // Every line of the step as it was at v0.37.0 is still there, in order, but for the line
        // that built the body, which now also takes the summary.
        const string OldBody = "  '{content: $content, allowed_mentions: {roles: [$role]}}' \\";
        string[] before = Repository.ReadFixture("announce-step-before-summaries.sh").Split('\n');
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
    public void The_announce_step_is_this_script_and_no_other()
    {
        // The step runs only when a release is cut, so a change to it is seen here first. If you
        // change the step on purpose, change this text with it, and look at what the tests below
        // post once CI has run them.
        Assert.Equal(
            """
            if [ -z "$WEBHOOK_URL" ]; then
              echo "DISCORD_RELEASE_WEBHOOK secret not set; skipping announcement."
              exit 0
            fi
            # Role id of @Release Ping in the community server; ids are public, the webhook URL
            # is the secret. allowed_mentions pins the ping to exactly this role.
            role_id="1543943300132769802"
            # The summary goes under the usual line when there is one and the whole message stays
            # inside Discord's 2,000 characters; otherwise the line stands alone.
            jq -n \
              --arg content "<@&${role_id}> **CalCrony ${RELEASE_TAG}** just shipped — release notes: ${RELEASE_URL}" \
              --arg summary "$SUMMARY" \
              --arg role "$role_id" \
              '($content + "\n\n" + $summary) as $full
               | {content: (if $summary != "" and ($full | length) <= 2000 then $full else $content end),
                  allowed_mentions: {roles: [$role]}}' \
            | curl -sf -X POST -H "Content-Type: application/json" -d @- "$WEBHOOK_URL"

            """.ReplaceLineEndings("\n"),
            Workflow.StepScript(AnnounceJob, AnnounceStep));
    }

    [Fact]
    public void The_announcement_waits_for_the_summary_job_without_requiring_it()
    {
        IReadOnlyList<string> job = Workflow.Job(AnnounceJob);

        Assert.Contains($"    needs: [release-please, {ImagesJob}, {SummaryJob}]", job);
        Assert.Contains(
            $"    if: ${{{{ !cancelled() && needs.release-please.outputs.release_created == 'true' && needs.{ImagesJob}.result == 'success' }}}}",
            job);
    }

    [Fact]
    public void Publishing_the_images_leaves_these_tests_out_so_no_summary_can_hold_a_release_back()
    {
        Assert.Equal(
            [
                "- name: Test release commit",
                $"run: dotnet test CalCrony.slnx -c Release --filter \"FullyQualifiedName!~{typeof(ReleaseWorkflowTests).Namespace}\"",
            ],
            Workflow.Step(ImagesJob, "- name: Test release commit"));
    }

    [Fact]
    public void The_summary_job_runs_for_a_release_within_a_time_limit_and_may_only_edit_it()
    {
        IReadOnlyList<string> job = Workflow.Job(SummaryJob);

        Assert.Contains("    needs: release-please", job);
        Assert.Contains("    if: needs.release-please.outputs.release_created == 'true'", job);
        Assert.Contains("    timeout-minutes: 15", job);
        Assert.Equal(["contents: write"], Workflow.Block(SummaryJob, "permissions"));
    }

    [Fact]
    public void The_summary_job_reads_the_tag_and_the_version_release_please_gave()
    {
        Assert.Equal(
            [
                "RELEASE_TAG: ${{ needs.release-please.outputs.tag_name }}",
                "RELEASE_VERSION: ${{ needs.release-please.outputs.version }}",
            ],
            Workflow.Block(SummaryJob, "env"));
    }

    [Fact]
    public void The_summary_job_hands_the_text_on_only_when_the_release_notes_got_it()
    {
        Assert.Equal(
            [$"{Cli.DiscordSummaryOutput}: ${{{{ steps.edit.outcome == 'success' && steps.render.outputs.{Cli.DiscordSummaryOutput} || '' }}}}"],
            Workflow.Block(SummaryJob, "outputs"));
    }

    [Fact]
    public void The_summary_job_checks_out_the_tag_keeps_no_token_reads_the_notes_renders_and_edits()
    {
        Assert.Equal(
            [
                "- uses: actions/checkout@v4",
                "- uses: actions/setup-dotnet@v4",
                "- name: Read the notes release-please wrote",
                "- name: Build the release texts",
                "- name: Put the summary on top of the release notes",
                "- name: Say so when the summary could not be added",
            ],
            Workflow.StepLabels(SummaryJob));

        Assert.Equal(
            [
                "- uses: actions/checkout@v4",
                "id: checkout",
                "continue-on-error: true",
                "timeout-minutes: 3",
                "with:",
                "ref: ${{ needs.release-please.outputs.tag_name }}",
                "persist-credentials: false",
            ],
            Workflow.Step(SummaryJob, "- uses: actions/checkout@v4"));

        Assert.Equal(
            [
                "- uses: actions/setup-dotnet@v4",
                "id: sdk",
                "continue-on-error: true",
                "timeout-minutes: 5",
                "with:",
                "dotnet-version: 10.0.x",
            ],
            Workflow.Step(SummaryJob, "- uses: actions/setup-dotnet@v4"));

        Assert.Equal(
            [
                "- name: Read the notes release-please wrote",
                "id: notes",
                "continue-on-error: true",
                "timeout-minutes: 2",
                "env:",
                Token,
                Repo,
                "run: gh release view \"$RELEASE_TAG\" --json body --jq .body > \"$RUNNER_TEMP/notes.md\"",
            ],
            Workflow.Step(SummaryJob, "- name: Read the notes release-please wrote"));

        Assert.Equal(
            [
                "- name: Put the summary on top of the release notes",
                "id: edit",
                $"if: steps.render.outputs.{Cli.HasSummaryOutput} == 'true'",
                "continue-on-error: true",
                "timeout-minutes: 2",
                "env:",
                Token,
                Repo,
                "run: gh release edit \"$RELEASE_TAG\" --notes-file \"$RUNNER_TEMP/release-notes.md\"",
            ],
            Workflow.Step(SummaryJob, "- name: Put the summary on top of the release notes"));
    }

    [Fact]
    public void The_summary_job_runs_the_tool_without_a_token()
    {
        Assert.Equal(
            [
                "- name: Build the release texts",
                "id: render",
                "continue-on-error: true",
                "timeout-minutes: 5",
                "run: |",
            ],
            Workflow.Step(SummaryJob, "- name: Build the release texts"));

        Assert.Equal(
            """
            dotnet run --project tools/ReleaseNotes -c Release -- render \
              --version "$RELEASE_VERSION" \
              --notes "$RUNNER_TEMP/notes.md" \
              --out "$RUNNER_TEMP/release-notes.md" \
              --github-output "$GITHUB_OUTPUT"

            """.ReplaceLineEndings("\n"),
            Workflow.StepScript(SummaryJob, "Build the release texts"));
    }

    [Fact]
    public void No_step_of_the_summary_job_can_end_it_red_and_the_last_one_says_what_failed()
    {
        IReadOnlyList<string> labels = Workflow.StepLabels(SummaryJob);

        foreach (string label in labels.SkipLast(1))
        {
            Assert.Contains("continue-on-error: true", Workflow.Step(SummaryJob, label));
            Assert.Contains(Workflow.Step(SummaryJob, label), line => line.StartsWith("timeout-minutes: ", StringComparison.Ordinal));
        }

        IReadOnlyList<string> last = Workflow.Step(SummaryJob, labels[^1]);
        Assert.Equal("if: ${{ contains(steps.*.outcome, 'failure') }}", last[1]);
        Assert.StartsWith("run: echo \"::warning title=Release summary::", last[2], StringComparison.Ordinal);
        Assert.Contains("Do not re-run this job", last[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_job_runs_from_master_when_nothing_is_released_and_may_read_and_comment_only()
    {
        IReadOnlyList<string> job = Workflow.Job(PreviewJob);

        Assert.Contains("    needs: release-please", job);
        Assert.Contains("    if: github.ref == 'refs/heads/master' && needs.release-please.outputs.release_created != 'true'", job);
        Assert.Contains("    timeout-minutes: 10", job);
        Assert.Equal(["contents: read", "pull-requests: write"], Workflow.Block(PreviewJob, "permissions"));
        Assert.DoesNotContain("    env:", job);
    }

    [Fact]
    public void The_preview_job_finds_release_pleases_own_pull_request_and_no_other()
    {
        Assert.Equal(
            ["- name: Find the open release PR", "id: pr", "env:", Token, Repo, "run: |"],
            Workflow.Step(PreviewJob, "- name: Find the open release PR"));

        Assert.Equal(
            """
            # release-please's own PR: its branch in this repository, opened by the Actions bot.
            gh pr list --state open --app github-actions --head release-please--branches--master \
              --json number,title,isCrossRepository,author \
              --jq 'map(select(.isCrossRepository == false and .author.login == "app/github-actions")) | first // empty | "number=\(.number)", "title=\(.title)"' \
              >> "$GITHUB_OUTPUT"

            """.ReplaceLineEndings("\n"),
            Workflow.StepScript(PreviewJob, "Find the open release PR"));
    }

    [Fact]
    public void The_preview_job_reads_master_as_it_is_now_and_keeps_no_token()
    {
        Assert.Equal(
            ["- uses: actions/checkout@v4", PullRequestFound, "with:", "ref: master", "persist-credentials: false"],
            Workflow.Step(PreviewJob, "- uses: actions/checkout@v4"));

        Assert.Equal(
            ["- uses: actions/setup-dotnet@v4", PullRequestFound, "with:", "dotnet-version: 10.0.x"],
            Workflow.Step(PreviewJob, "- uses: actions/setup-dotnet@v4"));
    }

    [Fact]
    public void The_preview_job_writes_the_comment_without_a_token()
    {
        Assert.Equal(
            ["- name: Write the comment", PullRequestFound, "env:", "PR_TITLE: ${{ steps.pr.outputs.title }}", "run: |"],
            Workflow.Step(PreviewJob, "- name: Write the comment"));

        Assert.Equal(
            """
            dotnet run --project tools/ReleaseNotes -c Release -- comment \
              --title "$PR_TITLE" \
              --out "$RUNNER_TEMP/comment.md"

            """.ReplaceLineEndings("\n"),
            Workflow.StepScript(PreviewJob, "Write the comment"));
    }

    [Fact]
    public void The_preview_job_updates_its_comment_in_place_or_posts_the_first_one()
    {
        Assert.Equal(
            [
                "- name: Post or update the comment",
                PullRequestFound,
                "env:",
                Token,
                Repo,
                "PR_NUMBER: ${{ steps.pr.outputs.number }}",
                "run: |",
            ],
            Workflow.Step(PreviewJob, "- name: Post or update the comment"));

        string expected =
            """
            if [ ! -s "$RUNNER_TEMP/comment.md" ]; then
              echo "The tool wrote no comment for this pull request; nothing to post."
              exit 0
            fi
            comments="repos/$GITHUB_REPOSITORY/issues/$PR_NUMBER/comments"
            ids=$(gh api "$comments" --paginate \
              --jq '.[] | select(.user.login == "github-actions[bot]" and (.body | startswith("MARKER"))) | .id')
            existing="${ids%%$'\n'*}"
            if [ -n "$existing" ]; then
              gh api --method PATCH "repos/$GITHUB_REPOSITORY/issues/comments/$existing" \
                -F body=@"$RUNNER_TEMP/comment.md" --silent
            else
              gh api --method POST "$comments" -F body=@"$RUNNER_TEMP/comment.md" --silent
            fi

            """.ReplaceLineEndings("\n").Replace("MARKER", PreviewComment.Marker, StringComparison.Ordinal);

        Assert.Equal(expected, Workflow.StepScript(PreviewJob, "Post or update the comment"));
    }

    [Fact]
    public void The_preview_job_does_nothing_more_once_no_release_pull_request_is_found()
    {
        IReadOnlyList<string> labels = Workflow.StepLabels(PreviewJob);

        Assert.Equal(5, labels.Count);
        Assert.All(labels.Skip(1), label => Assert.Equal(PullRequestFound, Workflow.Step(PreviewJob, label)[1]));
    }

    [AnnounceStepFact]
    public async Task With_no_summary_the_step_posts_byte_for_byte_what_it_posted_before()
    {
        // The step as it was at v0.37.0 named no shell; the step as it is now says shell: bash.
        string before = Repository.ReadFixture("announce-step-before-summaries.sh") + "\n";

        ReceivedRequest then = await PostAsync(before, StepShell.Unnamed, null);
        ReceivedRequest now = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), StepShell.Bash, string.Empty);

        Assert.Equal(then.RequestLine.Split(' ')[0], now.RequestLine.Split(' ')[0]);
        Assert.Equal(then.Headers["Content-Type"], now.Headers["Content-Type"]);
        Assert.Equal(Encoding.UTF8.GetString(then.Body), Encoding.UTF8.GetString(now.Body));
        AssertBody(now, Announcement.FirstLine(Tag, ReleaseUrl));
    }

    [AnnounceStepFact]
    public async Task With_the_0_37_0_summary_the_step_posts_the_message_kept_beside_the_tests()
    {
        string summary = Repository.ReadFixture("0.37.0/summary.md");

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), StepShell.Bash, summary);

        Assert.StartsWith("POST /api/webhooks/1/not-a-real-token ", request.RequestLine, StringComparison.Ordinal);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        AssertBody(request, Repository.ReadFixture("0.37.0/discord-message.txt"));
    }

    [AnnounceStepFact]
    public async Task A_summary_arrives_character_for_character_whatever_it_contains()
    {
        string summary = "### What's new\n\n- \"double\" and 'single' quotes, back\\slash, $HOME, $(id), `ticks`, 100%, {braces}\n- tab\there, é, — and 🎉\n\n--arg role 0 '. | halt_error' \"}, \"allowed_mentions\": {\"parse\": [\"everyone\"]";

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), StepShell.Bash, summary);

        AssertBody(request, $"{Announcement.FirstLine(Tag, ReleaseUrl)}\n\n{summary}");
    }

    [AnnounceStepFact]
    public async Task A_message_of_exactly_two_thousand_characters_is_posted_whole()
    {
        string firstLine = Announcement.FirstLine(Tag, ReleaseUrl);
        string summary = new('a', Announcement.MaxContentLength - firstLine.Length - 2);

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), StepShell.Bash, summary);

        AssertBody(request, $"{firstLine}\n\n{summary}");
    }

    [AnnounceStepFact]
    public async Task A_summary_that_would_not_fit_is_left_out_and_the_usual_line_is_posted()
    {
        string firstLine = Announcement.FirstLine(Tag, ReleaseUrl);
        string summary = new('a', Announcement.MaxContentLength - firstLine.Length - 1);

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), StepShell.Bash, summary);

        AssertBody(request, firstLine);
    }

    [AnnounceStepFact]
    public async Task The_step_and_the_tool_agree_on_what_fits()
    {
        // Characters that take two UTF-16 units: both count them once.
        string firstLine = Announcement.FirstLine(Tag, ReleaseUrl);
        string summary = string.Concat(Enumerable.Repeat("🎉", Announcement.MaxContentLength - firstLine.Length - 2));

        ReceivedRequest request = await PostAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), StepShell.Bash, summary);

        AssertBody(request, Announcement.Content(Tag, ReleaseUrl, summary));
    }

    [AnnounceStepFact]
    public async Task Without_a_webhook_the_step_posts_nothing_and_succeeds()
    {
        StepResult step = await StepRunner.RunAsync(
            Workflow.StepScript(AnnounceJob, AnnounceStep),
            StepShell.Bash,
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
            StepShell.Bash,
            StepVariables(webhook.Url, string.Empty));

        await receiving;
        Assert.NotEqual(0, step.ExitCode);
    }

    [AnnounceStepFact]
    public async Task When_jq_fails_the_step_fails_whatever_the_webhook_answers()
    {
        // A stand-in jq that only fails, in front of the real one. curl still sends its request,
        // with nothing in it, and here the webhook answers that all is well. The step says
        // shell: bash, so it fails all the same; a step that names no shell would have passed.
        using TemporaryCheckout folder = new();
        string brokenJq = folder.Write("jq", "#!/bin/sh\nexit 3\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(brokenJq, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string path = $"{folder.Root}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";

        (StepResult named, ReceivedRequest sentByNamed) = await RunWithPathAsync(StepShell.Bash, path);
        (StepResult unnamed, ReceivedRequest sentByUnnamed) = await RunWithPathAsync(StepShell.Unnamed, path);

        Assert.Equal(3, named.ExitCode);
        Assert.Equal(0, unnamed.ExitCode);
        Assert.Empty(sentByNamed.Body);
        Assert.Empty(sentByUnnamed.Body);
    }

    /// <summary>Runs a version of the announce step against a stand-in webhook that answers as
    /// Discord does, and gives the request it received.</summary>
    /// <param name="script">The step's script.</param>
    /// <param name="shell">The step's shell setting.</param>
    /// <param name="summary">The summary handed to the step, or null to leave the variable unset
    /// as it was before the step knew of it.</param>
    /// <returns>The request the stand-in webhook received.</returns>
    private static async Task<ReceivedRequest> PostAsync(string script, StepShell shell, string? summary)
    {
        using FakeWebhook webhook = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Task<ReceivedRequest> receiving = webhook.ReceiveAsync(204, timeout.Token);

        StepResult step = await StepRunner.RunAsync(script, shell, StepVariables(webhook.Url, summary));

        Assert.True(step.ExitCode == 0, $"The step failed with exit code {step.ExitCode}: {step.Error}");
        Task finished = await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(finished == receiving, $"The step succeeded without posting anything. Its output: {step.Output}");

        return await receiving;
    }

    /// <summary>Runs the announce step with a search path of the test's choosing, against a
    /// stand-in webhook that answers as Discord does to a message it accepts.</summary>
    /// <param name="shell">The step's shell setting.</param>
    /// <param name="path">The value of <c>PATH</c> for the step.</param>
    /// <returns>The step's result and the request the stand-in webhook received.</returns>
    private static async Task<(StepResult Step, ReceivedRequest Request)> RunWithPathAsync(StepShell shell, string path)
    {
        using FakeWebhook webhook = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Task<ReceivedRequest> receiving = webhook.ReceiveAsync(204, timeout.Token);
        Dictionary<string, string> variables = StepVariables(webhook.Url, "### What's new\n\n- One change.");
        variables["PATH"] = path;

        StepResult step = await StepRunner.RunAsync(Workflow.StepScript(AnnounceJob, AnnounceStep), shell, variables);

        return (step, await receiving);
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

    /// <summary>Counts how often a text occurs in another.</summary>
    /// <param name="text">The text to search.</param>
    /// <param name="value">The text to count.</param>
    /// <returns>The number of occurrences.</returns>
    private static int Occurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
