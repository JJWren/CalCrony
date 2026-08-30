using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>Per-user settings with the interface theme (issue #78): the theme round-trips, an
/// omitted (null) theme on writes keeps the stored value — so the bot's timezone/DM writes never
/// clobber a web-chosen theme — invalid values are rejected, and web callers stay self-only.</summary>
public class UserSettingsApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    [Fact]
    public async Task Theme_roundtrips_and_a_null_write_keeps_it()
    {
        var put = await fixture.Client.PutAsJsonAsync(
            "/users/8101/settings", new UserSettingsDto("America/Chicago", true, "ember"));
        put.EnsureSuccessStatusCode();
        Assert.Equal("ember", (await put.Content.ReadFromJsonAsync<UserSettingsDto>())!.Theme);

        var fetched = await fixture.Client.GetFromJsonAsync<UserSettingsDto>("/users/8101/settings");
        Assert.Equal("ember", fetched!.Theme);

        // A theme-less write (the bot's /settings timezone shape) updates the other fields
        // but must not reset the theme.
        var botWrite = await fixture.Client.PutAsJsonAsync(
            "/users/8101/settings", new UserSettingsDto("UTC", false));
        botWrite.EnsureSuccessStatusCode();

        var after = await fixture.Client.GetFromJsonAsync<UserSettingsDto>("/users/8101/settings");
        Assert.Equal("ember", after!.Theme);
        Assert.Equal("UTC", after.TimeZone);
        Assert.False(after.DmConfirmations);
    }

    [Fact]
    public async Task Unknown_theme_is_rejected_and_nothing_is_stored()
    {
        var put = await fixture.Client.PutAsJsonAsync(
            "/users/8102/settings", new UserSettingsDto(null, true, "neon"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var error = await put.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("slate", error!.Error);

        var fetched = await fixture.Client.GetFromJsonAsync<UserSettingsDto>("/users/8102/settings");
        Assert.Null(fetched!.Theme);
    }

    [Fact]
    public async Task Stale_stored_theme_reads_as_null_instead_of_leaking_to_clients()
    {
        // A theme id that later gets renamed/retired must never reach clients: seed one directly
        // (PUT would reject it) and confirm GET reports the default instead.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            db.UserProfiles.Add(new UserProfile { Id = 8105, Theme = "retired-theme" });
            await db.SaveChangesAsync();
        }

        var fetched = await fixture.Client.GetFromJsonAsync<UserSettingsDto>("/users/8105/settings");
        Assert.Null(fetched!.Theme);
    }

    [Fact]
    public async Task Web_user_saves_their_own_theme_but_nobody_elses()
    {
        var (client, session) = await fixture.LoginAsync(8103);

        var put = await client.PutAsJsonAsync(
            $"/users/{session.UserId}/settings", new UserSettingsDto(null, true, "parchment"));
        put.EnsureSuccessStatusCode();

        var fetched = await client.GetFromJsonAsync<UserSettingsDto>($"/users/{session.UserId}/settings");
        Assert.Equal("parchment", fetched!.Theme);

        var denied = await client.PutAsJsonAsync(
            "/users/8104/settings", new UserSettingsDto(null, true, "moss"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
