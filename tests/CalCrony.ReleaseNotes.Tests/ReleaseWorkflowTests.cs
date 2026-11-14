using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The release workflow itself. A pull request's checks never run that workflow, so
/// these tests read its file. They hold the announce job to what it was — no access to the
/// repository, a ping for the one role and nothing else, the message the tool previews — and
/// they pin how the two new jobs are wired, because a slip there would not fail a release: it
/// would quietly switch the summary off. Where bash, jq and curl exist — the CI runner — three
/// steps' own scripts are then run on this machine: the announce step against a stand-in
/// webhook, so what it would post is seen without posting anything, and the summary job's read
/// of the notes and its question to GitHub against a stand-in <c>gh</c>.</summary>
public class ReleaseWorkflowTests
{
    private const string ImagesJob = "publish-images";
    private const string AnnounceJob = "announce-release";
    private const string SummaryJob = "release-summary";
    private const string PreviewJob = "release-summary-preview";
    private const string AnnounceStep = "Post release to Discord";
    private const string MentionsStep = "- name: Ask GitHub whether the summary mentions anyone";
    private const string MentionWarning = "::warning title=Release summary::GitHub reads a mention";
    private const string HarmlessHtml = "<h3 dir=\"auto\">What's new</h3>\n<ul dir=\"auto\">\n<li>One change, with <code class=\"notranslate\">who:@GM</code> in code.</li>\n</ul>";
    private const string OwnerMentionHtml = "<p dir=\"auto\"><a class=\"user-mention notranslate\" data-hovercard-type=\"user\" href=\"https://github.com/JJWren\">@JJWren</a></p>";
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
        // Every command of the step as it was at v0.37.0 is still there, in order, but for the
        // line that built the body, which now also takes the summary.
        const string OldBody = "  '{content: $content, allowed_mentions: {roles: [$role]}}' \\";
        string[] before = Repository.ReadFixture("announce-step-before-summaries.sh").Split('\n');
        List<string> now = Workflow.StepCommands(AnnounceJob, AnnounceStep).TrimEnd('\n').Split('\n').ToList();

        Assert.Contains(OldBody, before);
        int position = 0;

        foreach (string line in before.Where(line => line != OldBody && !ReleaseWorkflow.IsComment(line)))
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
            role_id="1543943300132769802"
            jq -n \
              --arg content "<@&${role_id}> **CalCrony ${RELEASE_TAG}** just shipped — release notes: ${RELEASE_URL}" \
              --arg summary "$SUMMARY" \
              --arg role "$role_id" \
              '($content + "\n\n" + $summary) as $full
               | {content: (if $summary != "" and ($full | length) <= 2000 then $full else $content end),
                  allowed_mentions: {roles: [$role]}}' \
            | curl -sf -X POST -H "Content-Type: application/json" -d @- "$WEBHOOK_URL"

            """.ReplaceLineEndings("\n"),
            Workflow.StepCommands(AnnounceJob, AnnounceStep));
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
        Assert.Contains("    timeout-minutes: 25", job);
        Assert.Equal(["contents: write"], Workflow.Block(SummaryJob, "permissions"));
    }

    [Fact]
    public void The_summary_jobs_time_limit_is_above_what_its_steps_may_take_together()
    {
        // The job's limit is a backstop: were it the smaller one, it would end the job red before
        // the steps' own limits, which carry on, had their say.
        const string Limit = "timeout-minutes: ";
        const string JobLimit = "    timeout-minutes: ";

        string jobLine = Assert.Single(Workflow.Job(SummaryJob), line => line.StartsWith(JobLimit, StringComparison.Ordinal));
        int jobLimit = int.Parse(jobLine[JobLimit.Length..], CultureInfo.InvariantCulture);

        int stepLimits = Workflow.StepLabels(SummaryJob)
            .SelectMany(label => Workflow.Step(SummaryJob, label))
            .Where(line => line.StartsWith(Limit, StringComparison.Ordinal))
            .Sum(line => int.Parse(line[Limit.Length..], CultureInfo.InvariantCulture));

        Assert.True(stepLimits > 0);
        Assert.True(stepLimits < jobLimit, $"The steps may take {stepLimits} minutes together; the job has {jobLimit}.");
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
                "- name: Ask GitHub whether the summary mentions anyone",
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
                "run: |",
            ],
            Workflow.Step(SummaryJob, "- name: Read the notes release-please wrote"));

        Assert.Equal(
            """
            gh release view "$RELEASE_TAG" --json body --jq .body > "$RUNNER_TEMP/notes.partial"
            mv "$RUNNER_TEMP/notes.partial" "$RUNNER_TEMP/notes.md"

            """.ReplaceLineEndings("\n"),
            Workflow.StepCommands(SummaryJob, "Read the notes release-please wrote"));

        Assert.Equal(
            [
                "- name: Put the summary on top of the release notes",
                "id: edit",
                $"if: steps.render.outputs.{Cli.HasSummaryOutput} == 'true' && steps.mentions.outcome == 'success'",
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
    public void The_summary_job_runs_the_tool_without_a_token_and_only_on_notes_that_were_read()
    {
        Assert.Equal(
            [
                "- name: Build the release texts",
                "id: render",
                "if: steps.notes.outcome == 'success'",
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
              --summary-out "$RUNNER_TEMP/summary.md" \
              --github-output "$GITHUB_OUTPUT"

            """.ReplaceLineEndings("\n"),
            Workflow.StepCommands(SummaryJob, "Build the release texts"));
    }

    [Fact]
    public void The_summary_job_asks_github_whether_the_summary_mentions_anyone_before_it_edits()
    {
        Assert.Equal(
            [
                "- name: Ask GitHub whether the summary mentions anyone",
                "id: mentions",
                $"if: steps.render.outputs.{Cli.HasSummaryOutput} == 'true'",
                "continue-on-error: true",
                "timeout-minutes: 2",
                "env:",
                Token,
                "run: |",
            ],
            Workflow.Step(SummaryJob, MentionsStep));

        Assert.Equal(
            """
            to_html() {
              gh api markdown -f mode=gfm -f context="$GITHUB_REPOSITORY" -F text=@"$1"
            }
            mention='<a [^>]*class="[^"]*(user|team)-mention'
            printf '@%s\n' "$GITHUB_REPOSITORY_OWNER" > "$RUNNER_TEMP/control.md"
            to_html "$RUNNER_TEMP/control.md" > "$RUNNER_TEMP/control.html"
            if ! grep -Eq "$mention" "$RUNNER_TEMP/control.html"; then
              echo "GitHub did not read @$GITHUB_REPOSITORY_OWNER as a mention, so it cannot be asked about the summary."
              exit 1
            fi
            to_html "$RUNNER_TEMP/summary.md" > "$RUNNER_TEMP/summary.html"
            found=0
            grep -Eq "$mention" "$RUNNER_TEMP/summary.html" || found=$?
            if [ "$found" -eq 0 ]; then
              echo "::warning title=Release summary::GitHub reads a mention of a person or a team in the summary of this release, so the summary was left out. Take the mention out before adding the text to the release notes by hand."
            fi
            [ "$found" -eq 1 ]

            """.ReplaceLineEndings("\n"),
            Workflow.StepCommands(SummaryJob, MentionsStep["- name: ".Length..]));
    }

    [Fact]
    public void Every_working_step_of_the_summary_job_carries_on_after_a_failure_and_the_last_one_says_so()
    {
        IReadOnlyList<string> labels = Workflow.StepLabels(SummaryJob);

        foreach (string label in labels.SkipLast(1))
        {
            Assert.Contains("continue-on-error: true", Workflow.Step(SummaryJob, label));
            Assert.Contains(Workflow.Step(SummaryJob, label), line => line.StartsWith("timeout-minutes: ", StringComparison.Ordinal));
        }

        // It speaks when the texts were not built, GitHub read a mention or could not be asked, or
        // the release was not edited, and not because a step before them failed without stopping
        // them. What it says holds in each of those cases.
        IReadOnlyList<string> last = Workflow.Step(SummaryJob, labels[^1]);
        Assert.Equal(
            [
                "- name: Say so when the summary could not be added",
                "if: ${{ steps.render.outcome != 'success' || steps.mentions.outcome == 'failure' || steps.edit.outcome == 'failure' }}",
                "run: echo \"::warning title=Release summary::release-summary stopped early. If a summary was written for this release, it is not in the announcement and may be missing from the release notes. Check the release notes, and add the summary by hand if it is missing. Do not re-run this job, because that posts the announcement a second time.\"",
            ],
            last);
    }

    [AnnounceStepFact]
    public async Task The_notes_reach_their_file_only_when_the_read_succeeded()
    {
        // A stand-in gh in front of the real one: it prints what it is told to and exits as it is
        // told to. A read that fails part-way must leave no notes.md behind, because the next
        // step would put the summary on top of whatever that file held.
        using TemporaryCheckout folder = new();
        MakeExecutable(folder.Write("bin/gh", "#!/bin/sh\nprintf '%s\\n' \"$FAKE_BODY\"\nexit \"$FAKE_EXIT\"\n"));

        string script = Workflow.StepScript(SummaryJob, "Read the notes release-please wrote");
        Dictionary<string, string> variables = new()
        {
            ["PATH"] = $"{folder.PathOf("bin")}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            ["RUNNER_TEMP"] = folder.Root,
            ["RELEASE_TAG"] = Tag,
            ["FAKE_BODY"] = "## [0.37.0] the notes",
            ["FAKE_EXIT"] = "0",
        };

        StepResult read = await StepRunner.RunAsync(script, StepShell.Unnamed, variables);

        Assert.Equal(0, read.ExitCode);
        Assert.Equal("## [0.37.0] the notes\n", File.ReadAllText(folder.PathOf("notes.md")));
        Assert.False(File.Exists(folder.PathOf("notes.partial")));

        File.Delete(folder.PathOf("notes.md"));
        variables["FAKE_BODY"] = "half of the";
        variables["FAKE_EXIT"] = "1";

        StepResult failed = await StepRunner.RunAsync(script, StepShell.Unnamed, variables);

        Assert.NotEqual(0, failed.ExitCode);
        Assert.False(File.Exists(folder.PathOf("notes.md")));
    }

    [AnnounceStepFact]
    public async Task A_summary_github_reads_no_mention_in_passes_and_github_is_asked_about_that_file()
    {
        using TemporaryCheckout folder = new();

        StepResult step = await AskAboutMentionsAsync(folder, Answered(OwnerMentionHtml), Answered(HarmlessHtml));

        Assert.Equal(0, step.ExitCode);
        Assert.DoesNotContain("::warning", step.Output, StringComparison.Ordinal);

        // Two questions, each the whole file as text, rendered as this repository would: the
        // certain mention first, then the summary.
        string[] expected =
        [
            "api", "markdown", "-f", "mode=gfm", "-f", "context=JJWren/CalCrony", "-F", $"text=@{folder.PathOf("control.md")}",
            "api", "markdown", "-f", "mode=gfm", "-f", "context=JJWren/CalCrony", "-F", $"text=@{folder.PathOf("summary.md")}",
        ];
        Assert.Equal(expected, File.ReadAllLines(folder.PathOf("gh-arguments.txt")));
        Assert.Equal("@JJWren\n", File.ReadAllText(folder.PathOf("control.md")));
    }

    [AnnounceStepFact]
    public async Task A_summary_github_reads_a_mention_in_fails_the_step_and_says_why()
    {
        using TemporaryCheckout folder = new();
        string person = "<p dir=\"auto\">Thanks <a class=\"user-mention notranslate\" data-hovercard-type=\"user\" href=\"https://github.com/octocat\">@octocat</a>.</p>";
        string team = "<p dir=\"auto\">Thanks <a class=\"team-mention js-team-mention notranslate\" data-hovercard-type=\"team\" href=\"https://github.com/orgs/github/teams/docs\">@github/docs</a>.</p>";

        foreach (string html in new[] { person, team })
        {
            StepResult step = await AskAboutMentionsAsync(folder, Answered(OwnerMentionHtml), Answered(html));

            Assert.NotEqual(0, step.ExitCode);
            Assert.Contains(MentionWarning, step.Output, StringComparison.Ordinal);
        }
    }

    [AnnounceStepFact]
    public async Task Text_that_only_looks_like_a_mention_is_not_taken_for_one()
    {
        // The word, and the very class GitHub gives a mention, typed into a summary as text and
        // as code. GitHub leaves the quotes as they are, so only the link tag tells the two apart.
        using TemporaryCheckout folder = new();
        string html = "<p dir=\"auto\">It says class=\"user-mention\" where a mention is, and <code class=\"notranslate\">class=\"team-mention\"</code> for a team.</p>";

        StepResult step = await AskAboutMentionsAsync(folder, Answered(OwnerMentionHtml), Answered(html));

        Assert.Equal(0, step.ExitCode);
        Assert.DoesNotContain("::warning", step.Output, StringComparison.Ordinal);
    }

    [AnnounceStepFact]
    public async Task When_github_cannot_be_asked_the_step_fails_without_calling_it_a_mention()
    {
        using TemporaryCheckout folder = new();

        // The request about the certain mention fails, though what it printed looks right.
        StepResult noControl = await AskAboutMentionsAsync(folder, Failed(OwnerMentionHtml), Answered(HarmlessHtml));

        Assert.NotEqual(0, noControl.ExitCode);
        Assert.DoesNotContain(MentionWarning, noControl.Output, StringComparison.Ordinal);

        // The request about the summary fails, and it alone; what it printed holds no mention.
        // Without an answer the summary must not go out.
        StepResult noAnswer = await AskAboutMentionsAsync(folder, Answered(OwnerMentionHtml), Failed(HarmlessHtml));

        Assert.NotEqual(0, noAnswer.ExitCode);
        Assert.DoesNotContain(MentionWarning, noAnswer.Output, StringComparison.Ordinal);

        // GitHub answers, but not even its answer to a certain mention looks like one: whatever
        // it then says about the summary would pass, so the step stops there.
        StepResult changed = await AskAboutMentionsAsync(folder, Answered("<p dir=\"auto\">@JJWren</p>"), Answered(HarmlessHtml));

        Assert.NotEqual(0, changed.ExitCode);
        Assert.Contains("GitHub did not read @JJWren as a mention", changed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(MentionWarning, changed.Output, StringComparison.Ordinal);

        // The answer about the summary cannot be searched: a stand-in grep that finds the mention
        // in the control and gives up on the summary.
        string grep = folder.Write("bin/grep", "#!/bin/sh\ncase \"$*\" in *control.html*) exit 0 ;; *) exit 2 ;; esac\n");
        MakeExecutable(grep);

        StepResult unread = await AskAboutMentionsAsync(folder, Answered(OwnerMentionHtml), Answered(HarmlessHtml));

        Assert.NotEqual(0, unread.ExitCode);
        Assert.DoesNotContain(MentionWarning, unread.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_job_runs_from_master_when_nothing_is_released_and_may_read_and_comment_only()
    {
        IReadOnlyList<string> job = Workflow.Job(PreviewJob);

        Assert.Contains("    needs: release-please", job);
        Assert.Contains("    if: github.ref == 'refs/heads/master' && needs.release-please.outputs.release_created != 'true'", job);
        Assert.Contains("    timeout-minutes: 10", job);
        Assert.Equal(["contents: read", "pull-requests: write"], Workflow.Block(PreviewJob, "permissions"));
        Assert.Equal(["group: release-summary-preview", "cancel-in-progress: true"], Workflow.Block(PreviewJob, "concurrency"));
        Assert.DoesNotContain("    env:", job);
    }

    [Fact]
    public void The_preview_job_finds_release_pleases_own_pull_request_and_no_other()
    {
        Assert.Equal(
            ["- name: Find the open release PR", "id: pr", "env:", Token, "run: |"],
            Workflow.Step(PreviewJob, "- name: Find the open release PR"));

        // Asked for by its exact branch, not searched for: a search can be behind, and can match
        // a branch whose name only starts the same way.
        Assert.Equal(
            """
            gh api "repos/$GITHUB_REPOSITORY/pulls?state=open&head=$GITHUB_REPOSITORY_OWNER:release-please--branches--master" \
              --jq 'map(select(.user.login == "github-actions[bot]" and .head.repo.full_name == .base.repo.full_name)) | first // empty | "number=\(.number)", "title=\(.title)"' \
              >> "$GITHUB_OUTPUT"

            """.ReplaceLineEndings("\n"),
            Workflow.StepCommands(PreviewJob, "Find the open release PR"));
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
            Workflow.StepCommands(PreviewJob, "Write the comment"));
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

        Assert.Equal(expected, Workflow.StepCommands(PreviewJob, "Post or update the comment"));
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
        MakeExecutable(folder.Write("jq", "#!/bin/sh\nexit 3\n"));

        string path = $"{folder.Root}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";

        (StepResult named, ReceivedRequest sentByNamed) = await RunWithPathAsync(StepShell.Bash, path);
        (StepResult unnamed, ReceivedRequest sentByUnnamed) = await RunWithPathAsync(StepShell.Unnamed, path);

        Assert.Equal(3, named.ExitCode);
        Assert.Equal(0, unnamed.ExitCode);
        Assert.Empty(sentByNamed.Body);
        Assert.Empty(sentByUnnamed.Body);
    }

    /// <summary>What the stand-in <c>gh</c> does with one request.</summary>
    /// <param name="Html">What it prints.</param>
    /// <param name="ExitCode">What it exits with.</param>
    private sealed record GitHubAnswer(string Html, int ExitCode);

    /// <summary>A request GitHub answered.</summary>
    /// <param name="html">The answer.</param>
    /// <returns>The answer, with the exit code of success.</returns>
    private static GitHubAnswer Answered(string html) => new(html, 0);

    /// <summary>A request that failed after printing something.</summary>
    /// <param name="html">What was printed before the failure.</param>
    /// <returns>That text, with an exit code of failure.</returns>
    private static GitHubAnswer Failed(string html) => new(html, 1);

    /// <summary>Runs the step that asks GitHub about mentions, with a stand-in <c>gh</c> in front
    /// of the real one: it notes its arguments, and for the certain mention and for the summary
    /// it prints the HTML it is given and exits as it is told to, each request by itself.
    /// Nothing is asked of GitHub.</summary>
    /// <param name="folder">The folder standing in for the runner's temporary folder.</param>
    /// <param name="control">What the stand-in does with the request about the certain mention.</param>
    /// <param name="summary">What the stand-in does with the request about the summary.</param>
    /// <returns>What the step's script left behind.</returns>
    private static async Task<StepResult> AskAboutMentionsAsync(
        TemporaryCheckout folder,
        GitHubAnswer control,
        GitHubAnswer summary)
    {
        const string StandIn = """
            #!/bin/sh
            printf '%s\n' "$@" >> "$FAKE_ARGUMENTS"
            case "$*" in
              *control.md*) printf '%s\n' "$FAKE_CONTROL"; exit "$FAKE_CONTROL_EXIT" ;;
              *) printf '%s\n' "$FAKE_SUMMARY"; exit "$FAKE_SUMMARY_EXIT" ;;
            esac

            """;

        MakeExecutable(folder.Write("bin/gh", StandIn.ReplaceLineEndings("\n")));
        folder.Write("summary.md", "### What's new\n\n- One change.\n");
        File.Delete(folder.PathOf("gh-arguments.txt"));

        Dictionary<string, string> variables = new()
        {
            ["PATH"] = $"{folder.PathOf("bin")}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            ["RUNNER_TEMP"] = folder.Root,
            ["GITHUB_REPOSITORY"] = "JJWren/CalCrony",
            ["GITHUB_REPOSITORY_OWNER"] = "JJWren",
            ["FAKE_ARGUMENTS"] = folder.PathOf("gh-arguments.txt"),
            ["FAKE_CONTROL"] = control.Html,
            ["FAKE_CONTROL_EXIT"] = control.ExitCode.ToString(CultureInfo.InvariantCulture),
            ["FAKE_SUMMARY"] = summary.Html,
            ["FAKE_SUMMARY_EXIT"] = summary.ExitCode.ToString(CultureInfo.InvariantCulture),
        };

        return await StepRunner.RunAsync(
            Workflow.StepScript(SummaryJob, MentionsStep["- name: ".Length..]),
            StepShell.Unnamed,
            variables);
    }

    /// <summary>Lets a stand-in program be run, where the system asks for that.</summary>
    /// <param name="path">The program's file.</param>
    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
