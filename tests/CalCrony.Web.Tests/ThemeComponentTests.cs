using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Auth;
using CalCrony.Web.Components;
using CalCrony.Web.Pages.App;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>The interface-theme picker and the post-login theme sync (issue #78).</summary>
public class ThemeComponentTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Picker_renders_all_five_themes_and_marks_the_stored_one_selected()
    {
        UseApi();
        await SetupAuthAsync(signedIn: false);
        JSInterop.Setup<string>("calcronyTheme.getThemeName").SetResult("ember");

        var cut = Render<InterfaceThemePicker>();

        cut.WaitForAssertion(() =>
        {
            var tiles = cut.FindAll("button.theme-tile");
            Assert.Equal(5, tiles.Count);
            var selected = tiles.Single(t => t.ClassList.Contains("selected"));
            Assert.Contains("Tavern Ember", selected.TextContent);
        });
        Assert.Contains("Candlelit Slate", cut.Markup);
        Assert.Contains("Obsidian Azure", cut.Markup);
    }

    [Fact]
    public async Task Picking_a_theme_applies_it_and_saves_it_to_the_account_without_clobbering_other_settings()
    {
        var handler = UseApi();
        await SetupAuthAsync(signedIn: true);
        JSInterop.Setup<string>("calcronyTheme.getThemeName").SetResult("slate");
        handler.JsonFor = req => req.Method == HttpMethod.Get
            ? JsonSerializer.Serialize(new UserSettingsDto("America/Chicago", false), JsonWeb)
            : JsonSerializer.Serialize(new UserSettingsDto("America/Chicago", false, "moss"), JsonWeb);

        var cut = Render<InterfaceThemePicker>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("button.theme-tile").Count));

        cut.FindAll("button.theme-tile").Single(t => t.TextContent.Contains("Feywild Moss")).Click();

        cut.WaitForAssertion(() => Assert.Contains("saved to your account", cut.Markup));
        var applied = JSInterop.VerifyInvoke("calcronyTheme.setThemeName");
        Assert.Equal("moss", applied.Arguments[0]);

        // Read-modify-write: the PUT carries the fetched timezone/DM values plus the new theme.
        var body = JsonSerializer.Deserialize<UserSettingsDto>(handler.PutBody!, JsonWeb)!;
        Assert.Equal("moss", body.Theme);
        Assert.Equal("America/Chicago", body.TimeZone);
        Assert.False(body.DmConfirmations);
    }

    [Fact]
    public async Task Picking_parchment_from_dark_mode_flips_the_face_so_the_pick_is_visible()
    {
        UseApi();
        await SetupAuthAsync(signedIn: false);
        JSInterop.Setup<string>("calcronyTheme.getThemeName").SetResult("slate");
        JSInterop.Setup<string>("calcronyTheme.getTheme").SetResult("dark");

        var cut = Render<InterfaceThemePicker>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("button.theme-tile").Count));

        cut.FindAll("button.theme-tile").Single(t => t.TextContent.Contains("Parchment")).Click();

        cut.WaitForAssertion(() => Assert.Contains("applied on this device", cut.Markup));
        Assert.Equal("parchment", JSInterop.VerifyInvoke("calcronyTheme.setThemeName").Arguments[0]);
        Assert.Equal("light", JSInterop.VerifyInvoke("calcronyTheme.setTheme").Arguments[0]);
    }

    [Fact]
    public async Task Picker_skips_the_account_save_when_settings_cannot_be_read()
    {
        var handler = UseApi();
        await SetupAuthAsync(signedIn: true);
        JSInterop.Setup<string>("calcronyTheme.getThemeName").SetResult("slate");
        // A failed settings GET must not turn into a PUT built from defaults — that would wipe
        // the stored timezone/DM preferences just to save a theme.
        handler.StatusFor = req => req.Method == HttpMethod.Get ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;

        var cut = Render<InterfaceThemePicker>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("button.theme-tile").Count));

        cut.FindAll("button.theme-tile").Single(t => t.TextContent.Contains("Feywild Moss")).Click();

        cut.WaitForAssertion(() => Assert.Contains("couldn't save to your account", cut.Markup));
        Assert.Equal("moss", JSInterop.VerifyInvoke("calcronyTheme.setThemeName").Arguments[0]);
        Assert.Null(handler.PutBody);
    }

    [Fact]
    public async Task Theme_sync_applies_the_account_theme_on_this_device_after_login()
    {
        var handler = UseApi();
        await SetupAuthAsync(signedIn: true);
        JSInterop.Setup<string>("calcronyTheme.getThemeName").SetResult("slate");
        handler.JsonFor = _ => JsonSerializer.Serialize(new UserSettingsDto(null, true, "obsidian"), JsonWeb);

        var cut = Render<ThemeSync>();

        cut.WaitForAssertion(() =>
            Assert.Equal("obsidian", JSInterop.VerifyInvoke("calcronyTheme.setThemeName").Arguments[0]));
    }

    [Fact]
    public async Task Theme_sync_leaves_the_device_alone_when_the_account_has_no_theme()
    {
        var handler = UseApi();
        await SetupAuthAsync(signedIn: true);
        JSInterop.Setup<string>("calcronyTheme.getThemeName").SetResult("slate");
        handler.JsonFor = _ => JsonSerializer.Serialize(new UserSettingsDto(null, true), JsonWeb);

        var cut = Render<ThemeSync>();

        // The settings GET resolves first; only then is "no invocation" meaningful.
        cut.WaitForAssertion(() => Assert.NotNull(handler.LastRequest));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "calcronyTheme.setThemeName");
    }

    [Fact]
    public async Task Theme_toggle_highlight_follows_mode_changes_made_elsewhere()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string>("calcronyTheme.getTheme").SetResult("dark");

        var cut = Render<ThemeToggle>();
        cut.WaitForAssertion(() =>
            Assert.Contains("active", cut.FindAll("button").First(b => b.GetAttribute("title") == "Dark").ClassName));

        // The picker's Parchment face flip calls calcronyTheme.setTheme("light"), which notifies
        // watchers — simulate that callback and expect the highlight to move.
        await cut.InvokeAsync(() => cut.Instance.OnModeChanged("light"));

        Assert.Contains("active", cut.FindAll("button").First(b => b.GetAttribute("title") == "Light").ClassName);
        Assert.DoesNotContain("active", cut.FindAll("button").First(b => b.GetAttribute("title") == "Dark").ClassName ?? "");

        // The callback is JS-invokable: an unexpected payload must be ignored, not become state.
        await cut.InvokeAsync(() => cut.Instance.OnModeChanged("banana"));
        Assert.Contains("active", cut.FindAll("button").First(b => b.GetAttribute("title") == "Light").ClassName);
    }

    [Fact]
    public async Task Dm_reminders_toggle_is_off_by_default_and_saves_only_the_opt_in()
    {
        var handler = UseApi();
        await SetupAuthAsync(signedIn: true);
        handler.JsonFor = req => req.RequestUri!.AbsolutePath switch
        {
            "/users/42/settings" => JsonSerializer.Serialize(new UserSettingsDto("UTC", true, "slate"), JsonWeb),
            var p when p.Contains("timezone") => "[]",
            _ => null,
        };

        var cut = Render<UserSettings>();
        cut.WaitForAssertion(() => cut.Find("#us-dm-reminders"));

        // Off by default — the API's false renders unchecked, and the copy says who can turn it on.
        Assert.False(cut.Find("#us-dm-reminders").HasAttribute("checked"));
        Assert.Contains("only you can turn it on", cut.Markup);

        cut.Find("#us-dm-reminders").Change(true);

        cut.WaitForAssertion(() => Assert.NotNull(handler.PutBody));
        var body = JsonSerializer.Deserialize<UserSettingsDto>(handler.PutBody!, JsonWeb)!;
        Assert.True(body.DmReminders);
        Assert.True(body.DmConfirmations); // the other settings ride along unchanged
        Assert.Equal("UTC", body.TimeZone);
    }

    [Fact]
    public async Task A_failed_settings_load_surfaces_the_error_instead_of_saving_defaults()
    {
        var handler = UseApi();
        await SetupAuthAsync(signedIn: true);
        handler.StatusFor = req => req.RequestUri!.AbsolutePath == "/users/42/settings" && req.Method == HttpMethod.Get
            ? HttpStatusCode.InternalServerError
            : HttpStatusCode.OK;
        handler.JsonFor = req => req.RequestUri!.AbsolutePath.Contains("timezone") ? "[]" : null;

        var cut = Render<UserSettings>();

        // The page says it couldn't load, and nothing can be saved from this state: the controls
        // are disabled and the handler refuses, so never-loaded defaults (timezone, confirmations,
        // the DM opt-in) can't overwrite stored values.
        cut.WaitForAssertion(() => Assert.Contains("API error 500", cut.Markup));
        var save = cut.FindAll("button").First(b => b.TextContent.Trim() == "Save");
        Assert.True(save.HasAttribute("disabled"));
        Assert.True(cut.Find("#us-dm-reminders").HasAttribute("disabled"));

        save.Click(); // a click that slips through anyway is refused
        cut.WaitForAssertion(() => Assert.Contains("haven't loaded", cut.Markup));
        Assert.Null(handler.PutBody);
    }

    private CapturingHandler UseApi()
    {
        var handler = new CapturingHandler();
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return handler;
    }

    /// <summary>Registers auth services; when signedIn, the AuthApiClient is hydrated through its
    /// real refresh path against a fake /auth/refresh so <c>Session</c> is populated.</summary>
    private async Task SetupAuthAsync(bool signedIn)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ITokenStore, InMemoryTokenStore>();
        Services.AddSingleton<JwtAuthenticationStateProvider>();
        Services.AddScoped(sp => new AuthApiClient(
            new HttpClient(new SessionHandler()) { BaseAddress = new Uri("http://localhost") },
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<JwtAuthenticationStateProvider>()));
        this.AddAuthorization();

        if (signedIn)
        {
            Assert.True(await Services.GetRequiredService<AuthApiClient>().TryRefreshAsync());
        }
    }

    /// <summary>Answers /auth/refresh with a fixed session for user 42.</summary>
    private sealed class SessionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var session = new WebSessionResponse("token", DateTimeOffset.UtcNow.AddMinutes(10), 42, "Renna", null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(session, JsonWeb), Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Records the last request and PUT body; answers with JsonFor (default empty object).</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? PutBody { get; private set; }

        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        public Func<HttpRequestMessage, HttpStatusCode>? StatusFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Method == HttpMethod.Put)
            {
                PutBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            }

            var json = JsonFor?.Invoke(request) ?? "{}";
            return new HttpResponseMessage(StatusFor?.Invoke(request) ?? HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
