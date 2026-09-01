using System.Net;
using System.Text;
using System.Text.Json;
using CalCrony.Bot.Api;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>The bot client names the Discord user behind body-less mutations via the actor
/// header (issue #124), so the API's action log records the person rather than the bot; the
/// bot's own housekeeping calls send no header at all.</summary>
public class CalCronyApiClientActorTests
{
    [Fact]
    public async Task Body_less_mutations_carry_the_actor_header_when_a_user_is_named()
    {
        var handler = new RecordingHandler();
        var api = new CalCronyApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test") });
        var id = Guid.NewGuid();

        await api.DeleteEventAsync(id, actorId: 42);
        await api.SkipOccurrenceAsync(id, actorId: 43);
        await api.StopSeriesAsync(id, actorId: 44);
        await api.ClosePollAsync(id, actorId: 45);
        await api.DeleteTemplateAsync(id, actorId: 46);
        await api.DeleteLiveListAsync(id, actorId: 47);

        Assert.Equal(["42", "43", "44", "45", "46", "47"], handler.Actors);
        Assert.Equal(
            [HttpMethod.Delete, HttpMethod.Post, HttpMethod.Post, HttpMethod.Post, HttpMethod.Delete, HttpMethod.Delete],
            handler.Methods);
        Assert.All(handler.Bodies, Assert.Null);
    }

    [Fact]
    public async Task Bodied_mutations_keep_their_json_body_beside_the_header()
    {
        var handler = new RecordingHandler();
        var api = new CalCronyApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test") });

        await api.PutGuildSettingsAsync(7, new GuildSettingsDto("Europe/Berlin", 9, true), actorId: 42);
        await api.UpdateSeriesAsync(Guid.NewGuid(), new UpdateSeriesRequest(Interval: 2), actorId: 43);

        Assert.Equal(["42", "43"], handler.Actors);
        var settings = JsonSerializer.Deserialize<GuildSettingsDto>(handler.Bodies[0]!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Europe/Berlin", settings.TimeZone);
        Assert.True(settings.MirrorNativeEvents);
        Assert.Contains("\"interval\":2", handler.Bodies[1]);
    }

    [Fact]
    public async Task System_calls_send_no_actor_header()
    {
        var handler = new RecordingHandler();
        var api = new CalCronyApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test") });

        await api.DeleteLiveListAsync(Guid.NewGuid()); // the bot clearing a hand-deleted list

        Assert.Equal([null], handler.Actors);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string?> Actors { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Actors.Add(request.Headers.TryGetValues(ActionLogHeaders.ActorUserId, out var values) ? string.Join(",", values) : null);
            Methods.Add(request.Method);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("", Encoding.UTF8) };
        }
    }
}
