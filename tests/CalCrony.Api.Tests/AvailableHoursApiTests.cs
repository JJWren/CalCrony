using System.Net;
using System.Net.Http.Json;
using System.Text;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>A member's available hours on their own routes: the default week when nothing is
/// saved, a saved week replacing the last, every refusal with its reason, clearing, the web's
/// own-account rule, and that hours and the other settings never touch each other.</summary>
public class AvailableHoursApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static readonly AvailableHoursDayDto[] Evenings =
    [
        new(DayOfWeek.Monday, 1080, 1380),
        new(DayOfWeek.Saturday, 600, 1440),
    ];

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Nothing_saved_reads_as_the_default_week_and_creates_no_row()
    {
        var hours = await GetAsync(Client, 8201);

        // Not set, with the week that applies instead: both surfaces show it.
        Assert.False(hours.IsSet);
        Assert.Equal(MondayFirst, hours.Days.Select(d => d.Day));
        Assert.All(hours.Days, d => Assert.Equal(
            (AvailableHoursLimits.DefaultStartMinute, AvailableHoursLimits.DefaultEndMinute), (d.StartMinute, d.EndMinute)));

        Assert.False(await HasRowAsync(8201));
    }

    [Fact]
    public async Task A_saved_week_reads_back_marked_as_set_with_its_days_monday_first()
    {
        // Sent Saturday first; a weekday left out is not available.
        var put = await Client.PutAsJsonAsync(
            "/users/8202/available-hours", new SetAvailableHoursRequest([Evenings[1], Evenings[0]]));
        put.EnsureSuccessStatusCode();
        var saved = (await put.Content.ReadFromJsonAsync<AvailableHoursDto>())!;

        Assert.True(saved.IsSet);
        Assert.Equal(Evenings, saved.Days);

        var read = await GetAsync(Client, 8202);
        Assert.True(read.IsSet);
        Assert.Equal(Evenings, read.Days);
    }

    [Fact]
    public async Task Saving_replaces_the_whole_week()
    {
        await SaveAsync(Client, 8203, Evenings);
        await SaveAsync(Client, 8203, [new(DayOfWeek.Sunday, 0, 1440)]);

        Assert.Equal([new AvailableHoursDayDto(DayOfWeek.Sunday, 0, 1440)], (await GetAsync(Client, 8203)).Days);
    }

    [Theory]
    [InlineData("""{"days":[]}""", "Pick at least one day, or reset to go back to the default hours.")]
    [InlineData("""{}""", "Pick at least one day, or reset to go back to the default hours.")]
    [InlineData("""{"days":null}""", "Pick at least one day, or reset to go back to the default hours.")]
    [InlineData("""{"days":[{"day":9,"startMinute":600,"endMinute":840}]}""", "9 is not a day of the week.")]
    [InlineData("""{"days":[null]}""", "null is not a day of the week.")]
    [InlineData(
        """{"days":[{"day":1,"startMinute":600,"endMinute":840},{"day":1,"startMinute":900,"endMinute":960}]}""",
        "Monday is listed twice.")]
    [InlineData("""{"days":[{"day":2,"startMinute":-30,"endMinute":840}]}""", "Tuesday: a time is 0 to 1440 minutes after midnight.")]
    [InlineData("""{"days":[{"day":2,"startMinute":600,"endMinute":1470}]}""", "Tuesday: a time is 0 to 1440 minutes after midnight.")]
    [InlineData("""{"days":[{"day":5,"startMinute":615,"endMinute":840}]}""", "Friday: times are in 30-minute steps.")]
    [InlineData("""{"days":[{"day":0,"startMinute":1320,"endMinute":120}]}""", "Sunday: the start has to be before the end.")]
    [InlineData("""{"days":[{"day":0,"startMinute":600,"endMinute":600}]}""", "Sunday: the start has to be before the end.")]
    public async Task A_week_that_cannot_be_saved_is_refused_with_the_reason_and_nothing_is_stored(string body, string expected)
    {
        const long userId = 8204;

        var put = await Client.PutAsync($"/users/{userId}/available-hours", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(expected, (await put.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.False((await GetAsync(Client, userId)).IsSet);
        Assert.False(await HasRowAsync(userId));
    }

    [Fact]
    public async Task More_than_seven_days_are_refused()
    {
        var eight = new SetAvailableHoursRequest([.. MondayFirst.Append(DayOfWeek.Monday).Select(day => new AvailableHoursDayDto(day, 600, 840))]);

        var put = await Client.PutAsJsonAsync("/users/8205/available-hours", eight);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal("A week has seven days.", (await put.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task A_body_that_is_not_a_week_at_all_is_a_400_not_a_500()
    {
        var nothing = await Client.PutAsync("/users/8206/available-hours", Json("null"));
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);

        var text = await Client.PutAsync("/users/8206/available-hours", Json("\"weekdays\""));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);

        Assert.False(await HasRowAsync(8206));
    }

    [Fact]
    public async Task A_refused_week_leaves_the_saved_one_as_it_was()
    {
        await SaveAsync(Client, 8207, Evenings);

        var put = await Client.PutAsJsonAsync("/users/8207/available-hours", new SetAvailableHoursRequest([]));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(Evenings, (await GetAsync(Client, 8207)).Days);
    }

    [Fact]
    public async Task Clearing_goes_back_to_the_default_and_is_safe_to_repeat()
    {
        await SaveAsync(Client, 8208, Evenings);

        var cleared = await Client.DeleteAsync("/users/8208/available-hours");
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        var hours = await GetAsync(Client, 8208);
        Assert.False(hours.IsSet);
        Assert.Equal(7, hours.Days.Count);

        var again = await Client.DeleteAsync("/users/8208/available-hours");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task Clearing_what_was_never_saved_creates_no_row()
    {
        var cleared = await Client.DeleteAsync("/users/8209/available-hours");

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.False(await HasRowAsync(8209));
    }

    [Fact]
    public async Task A_stored_value_that_cannot_be_read_answers_as_not_set_and_can_be_saved_over()
    {
        await SeedProfileAsync(new UserProfile { Id = 8210, AvailableHoursJson = "not a week" });

        var hours = await GetAsync(Client, 8210);
        Assert.False(hours.IsSet);
        Assert.Equal(7, hours.Days.Count);

        await SaveAsync(Client, 8210, Evenings);
        Assert.Equal(Evenings, (await GetAsync(Client, 8210)).Days);
    }

    [Fact]
    public async Task A_web_member_reads_saves_and_clears_their_own_hours()
    {
        var (member, session) = await fixture.LoginAsync(8211);

        Assert.False((await GetAsync(member, session.UserId)).IsSet);

        await SaveAsync(member, session.UserId, Evenings);
        Assert.Equal(Evenings, (await GetAsync(member, session.UserId)).Days);

        var cleared = await member.DeleteAsync($"/users/{session.UserId}/available-hours");
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.False((await GetAsync(member, session.UserId)).IsSet);
    }

    [Fact]
    public async Task A_web_member_cannot_read_save_or_clear_anyone_elses_hours()
    {
        const long other = 8213;
        await SaveAsync(Client, other, Evenings);
        var (member, _) = await fixture.LoginAsync(8212);

        var read = await member.GetAsync($"/users/{other}/available-hours");
        var save = await member.PutAsJsonAsync(
            $"/users/{other}/available-hours", new SetAvailableHoursRequest([new(DayOfWeek.Sunday, 0, 1440)]));
        var clear = await member.DeleteAsync($"/users/{other}/available-hours");

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clear.StatusCode);

        // The refusal says nothing about the other member's hours, and leaves them as they were.
        Assert.Equal("You can only do that for your own account.", (await read.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Equal(Evenings, (await GetAsync(Client, other)).Days);
    }

    [Fact]
    public async Task A_settings_write_leaves_saved_hours_alone()
    {
        const long userId = 8214;
        await SaveAsync(Client, userId, Evenings);

        // The web's full write, then the bot's timezone-shaped one: neither carries hours.
        (await Client.PutAsJsonAsync(
            $"/users/{userId}/settings", new UserSettingsDto("America/Chicago", false, "ember", DmReminders: true))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto("UTC", true))).EnsureSuccessStatusCode();

        var hours = await GetAsync(Client, userId);
        Assert.True(hours.IsSet);
        Assert.Equal(Evenings, hours.Days);
    }

    [Fact]
    public async Task Saving_and_clearing_hours_leave_the_other_settings_alone()
    {
        const long userId = 8215;
        (await Client.PutAsJsonAsync(
            $"/users/{userId}/settings", new UserSettingsDto("America/Chicago", false, "ember", DmReminders: true))).EnsureSuccessStatusCode();

        await SaveAsync(Client, userId, Evenings);
        await AssertSettingsAsync();

        (await Client.DeleteAsync($"/users/{userId}/available-hours")).EnsureSuccessStatusCode();
        await AssertSettingsAsync();

        async Task AssertSettingsAsync()
        {
            var settings = (await Client.GetFromJsonAsync<UserSettingsDto>($"/users/{userId}/settings"))!;
            Assert.Equal("America/Chicago", settings.TimeZone);
            Assert.False(settings.DmConfirmations);
            Assert.Equal("ember", settings.Theme);
            Assert.True(settings.DmReminders);
        }
    }

    [Fact]
    public async Task Saving_hours_for_a_member_without_a_row_gives_them_the_defaults_a_missing_row_reads_as()
    {
        const long userId = 8216;
        var before = (await Client.GetFromJsonAsync<UserSettingsDto>($"/users/{userId}/settings"))!;

        await SaveAsync(Client, userId, Evenings);

        Assert.True(await HasRowAsync(userId));
        Assert.Equal(before, (await Client.GetFromJsonAsync<UserSettingsDto>($"/users/{userId}/settings"))!);
    }

    private static async Task<AvailableHoursDto> GetAsync(HttpClient client, long userId) =>
        (await client.GetFromJsonAsync<AvailableHoursDto>($"/users/{userId}/available-hours"))!;

    private static async Task SaveAsync(HttpClient client, long userId, AvailableHoursDayDto[] days) =>
        (await client.PutAsJsonAsync($"/users/{userId}/available-hours", new SetAvailableHoursRequest(days))).EnsureSuccessStatusCode();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<bool> HasRowAsync(long userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.UserProfiles.AnyAsync(u => u.Id == userId);
    }

    private async Task SeedProfileAsync(UserProfile profile)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();
    }
}
