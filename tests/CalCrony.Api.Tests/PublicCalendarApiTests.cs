using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>The opt-in public web calendar (issue #121): default off, slug lifecycle (mint /
/// regenerate / disable), manager-only changes, and the anonymous month view — what it shows,
/// what it must never show, and how running series project into it.</summary>
public class PublicCalendarApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long ChannelId = 12101;
    private const long CreatorId = 12102;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Default_is_off_and_unknown_or_malformed_slugs_are_not_found()
    {
        const long guildId = 12100;
        var settings = await ReadAsync<PublicCalendarSettingsDto>(await Client.GetAsync($"/guilds/{guildId}/public-calendar"));
        Assert.False(settings.Enabled);
        Assert.Null(settings.Slug);
        Assert.Null(settings.Path);

        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/public/calendars/{new string('0', 32)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/public/calendars/not-a-slug")).StatusCode);
    }

    [Fact]
    public async Task Enabling_mints_a_stable_slug_regenerating_revokes_it_and_disabling_removes_it()
    {
        const long guildId = 12110;
        var enabled = await PutAsync(guildId, new PublicCalendarRequest(true));
        Assert.True(enabled.Enabled);
        Assert.Matches("^[0-9a-f]{32}$", enabled.Slug);
        Assert.Equal($"/c/{enabled.Slug}", enabled.Path);

        // Enabling again keeps the link — only an explicit regenerate breaks shared URLs.
        Assert.Equal(enabled.Slug, (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug);
        Assert.Equal(enabled.Slug, (await ReadAsync<PublicCalendarSettingsDto>(
            await Client.GetAsync($"/guilds/{guildId}/public-calendar"))).Slug);

        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/public/calendars/{enabled.Slug}")).StatusCode);

        var regenerated = await PutAsync(guildId, new PublicCalendarRequest(true, Regenerate: true));
        Assert.NotEqual(enabled.Slug, regenerated.Slug);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/public/calendars/{enabled.Slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/public/calendars/{regenerated.Slug}")).StatusCode);

        var disabled = await PutAsync(guildId, new PublicCalendarRequest(false));
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.Slug);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/public/calendars/{regenerated.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Regenerating_while_off_is_rejected_and_does_not_turn_sharing_on()
    {
        const long guildId = 12180;
        var response = await Client.PutAsJsonAsync(
            $"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true, Regenerate: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await ReadAsync<PublicCalendarSettingsDto>(await Client.GetAsync($"/guilds/{guildId}/public-calendar"));
        Assert.False(after.Enabled);
        Assert.Null(after.Slug);
    }

    [Fact]
    public async Task Concurrent_first_enables_agree_on_a_single_slug()
    {
        const long guildId = 12170;
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Client.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true))));

        var stored = (await ReadAsync<PublicCalendarSettingsDto>(
            await Client.GetAsync($"/guilds/{guildId}/public-calendar"))).Slug;
        Assert.NotNull(stored);
        foreach (var response in responses)
        {
            // Every caller got the ONE link that actually exists — never a slug a racing
            // request immediately replaced.
            Assert.Equal(stored, (await ReadAsync<PublicCalendarSettingsDto>(response)).Slug);
        }
    }

    [Fact]
    public async Task Only_managers_change_the_setting_while_members_can_read_it()
    {
        const long guildId = 12120;
        // Web membership checks are scoped to guilds the bot is actually in.
        (await Client.PutAsJsonAsync($"/guilds/{guildId}/presence", new GuildPresenceRequest(true, "G"))).EnsureSuccessStatusCode();
        var (member, _) = await fixture.LoginAsync(12191, (guildId, "G", false));
        var denied = await member.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/guilds/{guildId}/public-calendar")).StatusCode);

        var (manager, _) = await fixture.LoginAsync(12192, (guildId, "G", true));
        var allowed = await manager.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", new PublicCalendarRequest(true));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True((await allowed.Content.ReadFromJsonAsync<PublicCalendarSettingsDto>())!.Enabled);
    }

    [Fact]
    public async Task Month_view_shows_titles_times_places_and_jump_links_but_nothing_private()
    {
        const long guildId = 12130;
        (await Client.PutAsJsonAsync($"/guilds/{guildId}/presence", new GuildPresenceRequest(true, "Test Guild")))
            .EnsureSuccessStatusCode();
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;

        var ev = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Council of Elrond", "in 6 hours", ChannelId,
            Description: "secret sauce recipe", Location: "The keep"));
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/message", new SetEventMessageRequest(ChannelId, 424242)))
            .EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/12150", new RsvpRequest(ev.Options.OrderBy(o => o.SortOrder).First().Id)))
            .EnsureSuccessStatusCode();

        // A cancelled event never shows.
        var cancelled = await CreateAsync(guildId, new CreateEventRequest(CreatorId, "Called off", "in 7 hours", ChannelId));
        (await Client.PatchAsJsonAsync($"/events/{cancelled.Id}", new UpdateEventRequest(CreatorId, Status: EventStatus.Cancelled)))
            .EnsureSuccessStatusCode();

        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(MonthUrl(slug, ev.StartsAtUtc));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.True(response.Headers.CacheControl!.NoStore); // the slug is revocable — never cache

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret sauce", json); // descriptions stay private
        Assert.DoesNotContain("12150", json);        // so do RSVPs and member ids
        Assert.DoesNotContain("Called off", json);

        var calendar = System.Text.Json.JsonSerializer.Deserialize<PublicCalendarDto>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("Test Guild", calendar.GuildName);
        Assert.Equal("UTC", calendar.TimeZone);
        Assert.True(calendar.EarliestMonth < calendar.LatestMonth);
        var entry = Assert.Single(calendar.Events, e => e.Title == "Council of Elrond");
        Assert.Equal("The keep", entry.Location);
        Assert.Equal($"https://discord.com/channels/{guildId}/{ChannelId}/424242", entry.DiscordUrl);
        Assert.False(entry.Projected);
        Assert.Equal(ev.StartsAtUtc, entry.StartsAtUtc);
        Assert.Equal(ev.StartsAtUtc.UtcDateTime, entry.StartsAtLocal); // server zone is UTC here
    }

    [Fact]
    public async Task Running_series_project_future_occurrences_without_doubling_the_live_one()
    {
        const long guildId = 12140;
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;
        var live = await CreateAsync(guildId, new CreateEventRequest(
            CreatorId, "Weekly Standup", "in 6 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RepeatCount: 3));

        // Sweep the months the three occurrences (and a would-be fourth) can land in.
        using var anonymous = fixture.Factory.CreateClient();
        var seen = new Dictionary<DateTimeOffset, PublicCalendarEventDto>();
        for (var week = 0; week <= 3; week++)
        {
            var month = await ReadAsync<PublicCalendarDto>(
                await anonymous.GetAsync(MonthUrl(slug, live.StartsAtUtc.AddDays(7 * week))));
            foreach (var entry in month.Events.Where(e => e.Title == "Weekly Standup"))
            {
                seen[entry.StartsAtUtc] = entry;
            }
        }

        // Exactly the count-limited three: the concrete live occurrence plus two projections.
        Assert.Equal(
            [live.StartsAtUtc, live.StartsAtUtc.AddDays(7), live.StartsAtUtc.AddDays(14)],
            seen.Keys.Order());
        Assert.False(seen[live.StartsAtUtc].Projected);
        Assert.All(seen.Values.Where(e => e.StartsAtUtc > live.StartsAtUtc), e =>
        {
            Assert.True(e.Projected);
            Assert.Null(e.DiscordUrl);
        });
    }

    [Fact]
    public async Task Months_far_from_today_are_rejected()
    {
        const long guildId = 12160;
        var slug = (await PutAsync(guildId, new PublicCalendarRequest(true))).Slug!;

        using var anonymous = fixture.Factory.CreateClient();
        var farFuture = DateTimeOffset.UtcNow.AddYears(3);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync(MonthUrl(slug, farFuture))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/public/calendars/{slug}?year=2026&month=13")).StatusCode);
        // Crafted years must be rejected outright, never wrap into range and 500 in the window math.
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/public/calendars/{slug}?year={int.MaxValue}&month=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/public/calendars/{slug}?year={int.MinValue}&month=12")).StatusCode);
    }

    // ---------- helpers ----------

    private static string MonthUrl(string slug, DateTimeOffset at) =>
        $"/public/calendars/{slug}?year={at.Year}&month={at.Month}";

    private async Task<PublicCalendarSettingsDto> PutAsync(long guildId, PublicCalendarRequest request) =>
        await ReadAsync<PublicCalendarSettingsDto>(
            await Client.PutAsJsonAsync($"/guilds/{guildId}/public-calendar", request));

    private async Task<EventDto> CreateAsync(long guildId, CreateEventRequest request)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
