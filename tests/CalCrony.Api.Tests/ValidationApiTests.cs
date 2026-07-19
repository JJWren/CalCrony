using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>Length/range validation must produce friendly 400s — never a Postgres truncation 500.</summary>
public class ValidationApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 9900;
    private const long ChannelId = 9901;
    private const long CreatorId = 9902;

    private HttpClient Client => fixture.Client;

    [Theory]
    [InlineData(129, null, null, null)]      // title over 128
    [InlineData(null, 4097, null, null)]     // description over 4096
    [InlineData(null, null, 257, null)]      // location over 256
    [InlineData(null, null, null, 513)]      // image URL over 512
    public async Task Create_event_rejects_over_long_fields_with_400(int? titleLen, int? descLen, int? locLen, int? imgLen)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId,
            titleLen is { } t ? new string('t', t) : "OK",
            "in 2 hours",
            ChannelId,
            Description: descLen is { } d ? new string('d', d) : null,
            Location: locLen is { } l ? new string('l', l) : null,
            ImageUrl: imgLen is { } i ? new string('i', i) : null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("must be at most", error!.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(40321)]
    public async Task Create_event_rejects_out_of_range_durations(int duration)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Duration", "in 2 hours", ChannelId, DurationMinutes: duration));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_event_accepts_exact_boundary_values()
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, new string('t', 128), "in 2 hours", ChannelId,
            Description: new string('d', 4096),
            DurationMinutes: 40320,
            Location: new string('l', 256),
            ImageUrl: new string('i', 512)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Update_event_rejects_over_long_fields_and_bad_durations()
    {
        var ev = await CreateEventAsync("Patch target");

        var longTitle = await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, Title: new string('t', 129)));
        Assert.Equal(HttpStatusCode.BadRequest, longTitle.StatusCode);

        var badDuration = await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, DurationMinutes: -5));
        Assert.Equal(HttpStatusCode.BadRequest, badDuration.StatusCode);

        var fine = await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, Title: new string('t', 128), DurationMinutes: 90));
        fine.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Convert_poll_rejects_out_of_range_duration()
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/polls", new CreatePollRequest(
            CreatorId, "When?", ChannelId, ["in 5 hours", "in 3 hours"], IsTimePoll: true));
        var poll = (await create.Content.ReadFromJsonAsync<PollDto>())!;
        (await Client.PostAsync($"/polls/{poll.Id}/close", null)).EnsureSuccessStatusCode();

        var convert = await Client.PostAsJsonAsync($"/polls/{poll.Id}/convert",
            new ConvertPollRequest(CreatorId, DurationMinutes: 0));
        Assert.Equal(HttpStatusCode.BadRequest, convert.StatusCode);

        // Poll stays convertible after the rejected attempt.
        var retry = await Client.PostAsJsonAsync($"/polls/{poll.Id}/convert",
            new ConvertPollRequest(CreatorId, DurationMinutes: 60));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Notification_create_rejects_out_of_range_and_over_long_fields()
    {
        var ev = await CreateEventAsync("Notify target");

        var farFuture = await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(40321));
        Assert.Equal(HttpStatusCode.BadRequest, farFuture.StatusCode);

        var longMessage = await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, new string('m', 1025)));
        Assert.Equal(HttpStatusCode.BadRequest, longMessage.StatusCode);

        var longMentions = await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(30, "ok", new string('@', 257)));
        Assert.Equal(HttpStatusCode.BadRequest, longMentions.StatusCode);

        var fine = await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications",
            new CreateEventNotificationRequest(40320, new string('m', 1024), new string('@', 256)));
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task Reminder_rejects_blank_and_over_long_text()
    {
        var blank = await Client.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(GuildId, CreatorId, ChannelId, "in 1 hour", "  "));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var tooLong = await Client.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(GuildId, CreatorId, ChannelId, "in 1 hour", new string('r', 1025)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var fine = await Client.PostAsJsonAsync("/reminders",
            new CreateReminderRequest(GuildId, CreatorId, ChannelId, "in 1 hour", new string('r', 1024)));
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    private async Task<EventDto> CreateEventAsync(string title)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(CreatorId, title, "in 2 hours", ChannelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }
}
