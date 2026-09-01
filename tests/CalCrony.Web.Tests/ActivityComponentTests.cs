using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Pages.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CalCrony.Web.Tests;

/// <summary>The manager-only Activity page (issue #124): entries render newest first with actor
/// names or ids, filters go out as query parameters, load-more carries the cursor, the export
/// button fetches the CSV through the API client and hands it to the browser, and members see
/// the managers-only notice instead of a request.</summary>
public class ActivityComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Lists_entries_with_names_sources_and_target_links()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var eventId = Guid.NewGuid();
        var page = new ActionLogPageDto(
        [
            Entry(ActionLogAction.EventEdited, "Edited “Raid” — title", 7, "Ash", ActionSource.Web, ActionTargetType.Event, eventId),
            // A "created" entry whose event has since been deleted: the API says the target is
            // gone, so no link — the action alone never decides linkability.
            Entry(ActionLogAction.EventCreated, "Created “Old”", 8, null, ActionSource.Discord, ActionTargetType.Event, Guid.NewGuid(), targetExists: false),
        ], null);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(page, JsonWeb),
            _ => GuildsJson(canManage: true),
        };

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Edited “Raid” — title", cut.Markup);
            Assert.Contains("Ash", cut.Markup);
            Assert.Contains("user 8", cut.Markup);
            Assert.Contains($"/app/events/{eventId}", cut.Markup);
            Assert.Contains(">web<", cut.Markup);
            Assert.Contains(">Discord<", cut.Markup);
            Assert.Contains("Export events (CSV)", cut.Markup);
        });
        Assert.DoesNotContain("Load more", cut.Markup);
        // A deleted event's page would 404, so its entry carries no link.
        Assert.Single(cut.FindAll("a"), a => a.GetAttribute("href")?.StartsWith("/app/events/") == true);
    }

    [Fact]
    public void Filters_and_load_more_send_the_expected_query()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var first = new ActionLogPageDto([Entry(ActionLogAction.PollCreated, "Created poll “A”", 1, "One")], "2026-08-31T12:00:00Z|abc");
        var second = new ActionLogPageDto([Entry(ActionLogAction.PollClosed, "Closed poll “A”", 1, "One")], null);
        var calls = 0;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(++calls == 3 ? second : first, JsonWeb),
            _ => GuildsJson(canManage: true),
        };

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Created poll “A”", cut.Markup));
        Assert.Contains("limit=50", handler.LastActionsQuery);
        Assert.DoesNotContain("before=", handler.LastActionsQuery);

        cut.Find("#act-action").Change(nameof(ActionLogAction.PollCreated));
        cut.Find("#act-user").Change("42");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Apply").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("action=PollCreated", handler.LastActionsQuery);
            Assert.Contains("userId=42", handler.LastActionsQuery);
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Load more")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("before=2026-08-31T12%3A00%3A00Z%7Cabc", handler.LastActionsQuery);
            Assert.Contains("Closed poll “A”", cut.Markup); // appended below the first page
            Assert.Contains("Created poll “A”", cut.Markup);
        });
        Assert.DoesNotContain("Load more", cut.Markup);
    }

    [Fact]
    public void Non_numeric_member_filter_is_rejected_client_side()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => GuildsJson(canManage: true),
        };

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Nothing logged yet", cut.Markup));
        var before = handler.ActionsCalls;

        cut.Find("#act-user").Change("not-an-id");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Apply").Click();

        cut.WaitForAssertion(() => Assert.Contains("numeric Discord user id", cut.Markup));
        Assert.Equal(before, handler.ActionsCalls);
    }

    [Fact]
    public void Export_button_fetches_the_csv_and_hands_it_to_the_browser()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => GuildsJson(canManage: true),
        };
        handler.CsvBytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("event_id,title\r\n")];
        var download = JSInterop.Setup<string>("calcronyDownload", _ => true);
        download.SetResult("saved");

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Export events")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Downloaded calcrony-events-1-20260831.csv", cut.Markup));
        Assert.Equal("/guilds/1/export/events.csv", handler.LastCsvPath);
        var invocation = Assert.Single(download.Invocations);
        Assert.Equal("calcrony-events-1-20260831.csv", invocation.Arguments[0]);
        // What reaches JS is the response's own content stream — never a buffered copy: the
        // client must not have read the body into memory on the way.
        var streamRef = Assert.IsType<DotNetStreamReference>(invocation.Arguments[1]);
        var content = handler.LastCsvContent!;
        Assert.False(content.Buffered);
        Assert.NotNull(content.HandedOut);
        Assert.Same(content.HandedOut, streamRef.Stream);
        Assert.StartsWith("text/csv", (string)invocation.Arguments[2]!);
        // And once the interop call has returned, the response is closed behind it.
        Assert.Throws<ObjectDisposedException>(() => content.HandedOut!.ReadByte());
    }

    [Fact]
    public void Members_see_the_managers_only_notice_and_no_log_request()
    {
        var handler = UseApi();
        SetupAuth(canManage: false);
        handler.JsonFor = _ => GuildsJson(canManage: false);

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));

        cut.WaitForAssertion(() => Assert.Contains("Only server managers can see this server's activity log", cut.Markup));
        Assert.Equal(0, handler.ActionsCalls);
        Assert.DoesNotContain("Export events (CSV)", cut.Markup);
    }

    [Fact]
    public void A_failed_guild_lookup_shows_the_error_not_the_managers_only_notice()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        handler.StatusFor = req => req.RequestUri!.AbsolutePath == "/me/guilds" ? HttpStatusCode.ServiceUnavailable : null;
        handler.JsonFor = _ => """{"error":"API is down for maintenance."}""";

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));

        // An outage must never masquerade as "you're not a manager" — that would send a real
        // manager away instead of telling them to retry.
        cut.WaitForAssertion(() => Assert.Contains("API is down for maintenance.", cut.Markup));
        Assert.DoesNotContain("Only server managers can see", cut.Markup);
        Assert.Equal(0, handler.ActionsCalls);
    }

    [Fact]
    public void A_transient_list_failure_can_be_retried_from_the_error_state()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var guilds = JsonSerializer.Serialize(
            new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(1, "Mine", null, true)]), JsonWeb);
        var failList = true;
        handler.StatusFor = req =>
            failList && req.RequestUri!.AbsolutePath.EndsWith("/actions") ? HttpStatusCode.ServiceUnavailable : null;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => failList
                ? """{"error":"Temporary failure."}"""
                : JsonSerializer.Serialize(
                    new ActionLogPageDto([Entry(ActionLogAction.EventCreated, "Created “Raid night”", 7, "Ash")], null),
                    JsonWeb),
            _ => guilds,
        };

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Temporary failure.", cut.Markup));

        // Without a retry the error branch hides every control, so the page is stuck until a
        // full browser reload.
        failList = false;
        cut.FindAll("button").First(b => b.TextContent.Contains("Try again")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Raid night", cut.Markup));
        Assert.DoesNotContain("Temporary failure.", cut.Markup);
    }

    [Fact]
    public void Switching_to_an_unmanaged_guild_hides_the_export_button_before_the_lookup_lands()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var guilds = JsonSerializer.Serialize(
            new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(1, "Mine", null, true), new WebGuildDto(2, "Theirs", null, false)]), JsonWeb);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => guilds,
        };

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));

        // Hold the next guild lookup open and move the component to a guild the user doesn't
        // manage: the previous guild's "manager" must not leak into the wait.
        var gate = new TaskCompletionSource();
        handler.GateFor = req => req.RequestUri!.AbsolutePath == "/me/guilds" ? gate.Task : Task.CompletedTask;
        cut.Render(p => p.Add(x => x.GuildId, 2L));

        Assert.DoesNotContain("Export events (CSV)", cut.Markup);

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Contains("Only server managers can see this server's activity log", cut.Markup));
        Assert.DoesNotContain("Export events (CSV)", cut.Markup);
    }

    [Fact]
    public void Load_more_pages_with_the_applied_filter_not_a_pending_edit()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var page = new ActionLogPageDto([Entry(ActionLogAction.PollCreated, "Created poll “A”", 1, "One")], "2026-08-31T12:00:00Z|abc");
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(page, JsonWeb),
            _ => GuildsJson(canManage: true),
        };

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Created poll “A”", cut.Markup));

        // Apply A, then change the control to B WITHOUT applying, then load more: the next page
        // must belong to A's result set (A + the old cursor), never B's filter with A's cursor.
        cut.Find("#act-action").Change(nameof(ActionLogAction.PollCreated));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Apply").Click();
        cut.WaitForAssertion(() => Assert.Contains("action=PollCreated", handler.LastActionsQuery));

        cut.Find("#act-action").Change(nameof(ActionLogAction.PollClosed));
        cut.FindAll("button").First(b => b.TextContent.Contains("Load more")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("action=PollCreated", handler.LastActionsQuery);
            Assert.Contains("before=2026-08-31T12%3A00%3A00Z%7Cabc", handler.LastActionsQuery);
        });
        Assert.DoesNotContain("PollClosed", handler.LastActionsQuery);
    }

    [Fact]
    public void Switching_guild_mid_download_disposes_the_stale_response()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var guilds = JsonSerializer.Serialize(
            new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(1, "Mine", null, true), new WebGuildDto(2, "Also mine", null, true)]), JsonWeb);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => guilds,
        };
        handler.CsvBytes = Encoding.UTF8.GetBytes("event_id,title\r\n");
        var download = JSInterop.Setup<string>("calcronyDownload", _ => true);
        download.SetResult("saved");

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));

        // Hold guild 1's download open, move to guild 2, then let the download complete: the
        // stale response must be closed, not handed to the browser or leaked.
        var gate = new TaskCompletionSource();
        handler.GateFor = req => req.RequestUri!.AbsolutePath.EndsWith("/export/events.csv") ? gate.Task : Task.CompletedTask;
        cut.FindAll("button").First(b => b.TextContent.Contains("Export events")).Click();
        cut.Render(p => p.Add(x => x.GuildId, 2L));
        gate.SetResult();

        cut.WaitForAssertion(() => Assert.NotNull(handler.LastCsvContent?.HandedOut));
        cut.WaitForAssertion(() => Assert.Throws<ObjectDisposedException>(() => handler.LastCsvContent!.HandedOut!.ReadByte()));
        Assert.Empty(download.Invocations);
        Assert.DoesNotContain("Downloaded", cut.Markup);
    }

    [Fact]
    public async Task Switching_guild_while_js_reads_aborts_the_stream_and_leaks_nothing_into_the_new_guild()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var guilds = JsonSerializer.Serialize(
            new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(1, "Mine", null, true), new WebGuildDto(2, "Also mine", null, true)]), JsonWeb);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => guilds,
        };
        handler.CsvBytes = Encoding.UTF8.GetBytes("event_id,title\r\n");
        // No result set: the JS side is "mid-read" until the test says otherwise.
        var download = JSInterop.Setup<string>("calcronyDownload", _ => true);

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Export events")).Click();
        cut.WaitForAssertion(() => Assert.Single(download.Invocations));
        var streamBeingRead = handler.LastCsvContent!.HandedOut!;
        var actionsBefore = handler.ActionsCalls;

        // Switch guilds while JS is still reading guild 1's file.
        cut.Render(p => p.Add(x => x.GuildId, 2L));
        cut.WaitForAssertion(() => Assert.Equal(actionsBefore + 1, handler.ActionsCalls)); // guild 2's own load
        // The read was aborted: the stream JS was consuming is closed under it.
        Assert.Throws<ObjectDisposedException>(() => streamBeingRead.ReadByte());

        // Now let the (already-cancelled) interop call complete, and give any stray continuation
        // time to run: neither the old filename nor a reload may reach guild 2's page.
        try
        {
            download.SetResult("saved");
        }
        catch (InvalidOperationException)
        {
            // bUnit already completed the invocation as cancelled — the same end state.
        }

        await Task.Delay(250);
        Assert.DoesNotContain("Downloaded", cut.Markup);
        Assert.Equal(actionsBefore + 1, handler.ActionsCalls);
        Assert.Contains("Export events (CSV)", cut.Markup); // the new guild's own button is live again
    }

    [Fact]
    public async Task Disposing_the_component_mid_download_closes_the_stream_and_writes_no_state()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => GuildsJson(canManage: true),
        };
        handler.CsvBytes = Encoding.UTF8.GetBytes("event_id,title\r\n");
        var download = JSInterop.Setup<string>("calcronyDownload", _ => true); // JS stays "mid-read"

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Export events")).Click();
        cut.WaitForAssertion(() => Assert.Single(download.Invocations));
        var streamBeingRead = handler.LastCsvContent!.HandedOut!;
        var actionsBefore = handler.ActionsCalls;

        // Navigating away: Blazor disposes the component with no parameter change in between.
        ((IDisposable)cut.Instance).Dispose();

        Assert.Throws<ObjectDisposedException>(() => streamBeingRead.ReadByte());

        // Let the interop call finish and give any stray continuation time to run: a reload
        // request is the only observable state write it could make, and it must not happen.
        try
        {
            download.SetResult("saved");
        }
        catch (InvalidOperationException)
        {
            // bUnit already completed the invocation as cancelled — the same end state.
        }

        await Task.Delay(250);
        Assert.Equal(actionsBefore, handler.ActionsCalls);
    }

    [Fact]
    public async Task A_stale_export_finishing_late_does_not_re_enable_the_newer_guilds_export_button()
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        var guilds = JsonSerializer.Serialize(
            new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(1, "Mine", null, true), new WebGuildDto(2, "Also mine", null, true)]), JsonWeb);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => guilds,
        };
        handler.CsvBytes = Encoding.UTF8.GetBytes("event_id,title\r\n");
        // One pending interop per guild, told apart by the filename each download carries.
        var first = JSInterop.Setup<string>("calcronyDownload", inv => (string)inv.Arguments[0]! == "calcrony-events-1-20260831.csv");
        var second = JSInterop.Setup<string>("calcronyDownload", inv => (string)inv.Arguments[0]! == "calcrony-events-2-20260831.csv");

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Export events")).Click();
        cut.WaitForAssertion(() => Assert.Single(first.Invocations));

        // Switch guilds while guild 1's JS read is pending, then start guild 2's own export.
        cut.Render(p => p.Add(x => x.GuildId, 2L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Export events")).Click();
        cut.WaitForAssertion(() => Assert.Single(second.Invocations));
        Assert.Contains("Preparing…", cut.Markup);

        // The stale export wakes up late: it must not touch the newer export's button state.
        try
        {
            first.SetResult("saved");
        }
        catch (InvalidOperationException)
        {
            // bUnit already completed the invocation as cancelled — the same end state.
        }

        await Task.Delay(250);
        Assert.Contains("Preparing…", cut.Markup);
        Assert.DoesNotContain("Downloaded", cut.Markup);

        second.SetResult("saved");
        cut.WaitForAssertion(() => Assert.Contains("Downloaded calcrony-events-2-20260831.csv", cut.Markup));
        Assert.DoesNotContain("Preparing…", cut.Markup);
    }

    [Theory]
    [InlineData("cancelled", "Download cancelled.")]
    [InlineData("too-large", "too large to assemble in this browser")]
    [InlineData("failed", "didn't complete")]
    public void Browser_side_download_outcomes_are_reported(string outcome, string expectedText)
    {
        var handler = UseApi();
        SetupAuth(canManage: true);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/actions") => JsonSerializer.Serialize(new ActionLogPageDto([], null), JsonWeb),
            _ => GuildsJson(canManage: true),
        };
        handler.CsvBytes = Encoding.UTF8.GetBytes("event_id,title\r\n");
        var download = JSInterop.Setup<string>("calcronyDownload", _ => true);
        download.SetResult(outcome);

        var cut = Render<GuildActivity>(p => p.Add(x => x.GuildId, 1L));
        cut.WaitForAssertion(() => Assert.Contains("Export events (CSV)", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Export events")).Click();

        cut.WaitForAssertion(() => Assert.Contains(expectedText, cut.Markup));
        Assert.DoesNotContain("Downloaded", cut.Markup);
        Assert.DoesNotContain("Preparing…", cut.Markup);
    }

    private static ActionLogEntryDto Entry(
        ActionLogAction action, string summary, long actorId, string? actorName,
        ActionSource source = ActionSource.Discord, ActionTargetType targetType = ActionTargetType.Poll, Guid? targetId = null,
        bool targetExists = true) =>
        new(Guid.NewGuid(), 1, actorId, actorName, source, action, targetType, targetId ?? Guid.NewGuid(), targetExists, summary, null, DateTimeOffset.UtcNow);

    private static string GuildsJson(bool canManage) => JsonSerializer.Serialize(
        new WebGuildListResponse(DateTimeOffset.UtcNow, [new WebGuildDto(1, "G", null, canManage)]), JsonWeb);

    private CapturingHandler UseApi()
    {
        var handler = new CapturingHandler();
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return handler;
    }

    private void SetupAuth(bool canManage)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<CalCrony.Web.Auth.ITokenStore, CalCrony.Web.Auth.InMemoryTokenStore>();
        Services.AddSingleton<CalCrony.Web.Auth.JwtAuthenticationStateProvider>();
        Services.AddScoped(sp => new CalCrony.Web.Auth.AuthApiClient(
            new HttpClient { BaseAddress = new Uri("http://localhost") },
            sp.GetRequiredService<CalCrony.Web.Auth.ITokenStore>(),
            sp.GetRequiredService<CalCrony.Web.Auth.JwtAuthenticationStateProvider>()));
        this.AddAuthorization();
        _ = canManage; // the guild list JSON is what actually decides manager-ness
    }

    /// <summary>Routes JSON by request; serves the CSV bytes for the export route with the
    /// headers the API sets; records the queries and paths the page sent.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        /// <summary>Overrides the status for a request (null = 200), for failure paths.</summary>
        public Func<HttpRequestMessage, HttpStatusCode?>? StatusFor { get; set; }

        public string? LastActionsQuery { get; private set; }

        public int ActionsCalls { get; private set; }

        public string? LastCsvPath { get; private set; }

        public byte[]? CsvBytes { get; set; }

        /// <summary>The CSV content served last — tells whether the client buffered it.</summary>
        public TrackingContent? LastCsvContent { get; private set; }

        /// <summary>Awaited before a request is answered — a gate for in-flight-state tests.</summary>
        public Func<HttpRequestMessage, Task>? GateFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (GateFor is { } gate)
            {
                await gate(request);
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/export/events.csv"))
            {
                LastCsvPath = path;
                LastCsvContent = new TrackingContent(CsvBytes ?? []);
                var file = new HttpResponseMessage(HttpStatusCode.OK) { Content = LastCsvContent };
                file.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
                file.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    // Named per guild so a test can tell one guild's download from another's.
                    FileName = $"\"calcrony-events-{path.Split('/')[2]}-20260831.csv\"",
                };
                return file;
            }

            if (path.EndsWith("/actions"))
            {
                ActionsCalls++;
                LastActionsQuery = request.RequestUri.Query;
            }

            var json = JsonFor?.Invoke(request) ?? "{}";
            return new HttpResponseMessage(StatusFor?.Invoke(request) ?? HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Response content that records HOW it was consumed: buffering (ReadAsByteArray /
    /// ReadAsString / LoadIntoBuffer) goes through SerializeToStreamAsync and flips
    /// <see cref="Buffered"/>; a streaming read hands out one stream instance the test can
    /// compare against what reached JS.</summary>
    private sealed class TrackingContent(byte[] bytes) : HttpContent
    {
        public bool Buffered { get; private set; }

        public Stream? HandedOut { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            Buffered = true;
            return stream.WriteAsync(bytes).AsTask();
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            HandedOut = new MemoryStream(bytes, writable: false);
            return Task.FromResult(HandedOut);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }
}
