using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Pages.App;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>The event form's length on an edit: sent only when it was changed. Sent unchanged with
/// a change to the whole series, one occurrence's own length would become the series' length, and
/// the API refuses a length that reaches the gap between two dates of the series.</summary>
public class EventFormLengthTests : BunitContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void An_untouched_length_is_not_sent_for_the_whole_series()
    {
        // The posted occurrence of a daily series, made three days long on its own.
        var handler = UseApi();
        var ev = SampleEvent(durationMinutes: 3 * 24 * 60, repeating: true);
        handler.JsonFor = _ => JsonSerializer.Serialize(ev, JsonWeb);

        var cut = Render<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));
        cut.WaitForAssertion(() => Assert.Equal("4320", cut.Find("#ev-duration").GetAttribute("value")));
        cut.Find("#ev-title").Change("Renamed");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Whole series")).Click();

        var body = JsonSerializer.Deserialize<UpdateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal(EditScope.Series, body.Scope);
        Assert.Equal("Renamed", body.Title);
        Assert.Null(body.DurationMinutes);
    }

    [Fact]
    public void A_changed_length_is_sent()
    {
        var handler = UseApi();
        var ev = SampleEvent(durationMinutes: 60, repeating: false);
        handler.JsonFor = _ => JsonSerializer.Serialize(ev, JsonWeb);

        var cut = Render<EventForm>(p => p.Add(x => x.EventId, (Guid?)ev.Id));
        cut.WaitForAssertion(() => Assert.Equal("60", cut.Find("#ev-duration").GetAttribute("value")));
        cut.Find("#ev-duration").Change("90");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();

        var body = JsonSerializer.Deserialize<UpdateEventRequest>(handler.LastBody!, JsonWeb)!;
        Assert.Equal(90, body.DurationMinutes);
    }

    private CapturingHandler UseApi()
    {
        var handler = new CapturingHandler();
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return handler;
    }

    /// <summary>An event with the default RSVP options; a scheduled occurrence of a daily series
    /// when <paramref name="repeating"/> is true.</summary>
    private static EventDto SampleEvent(int durationMinutes, bool repeating)
    {
        RsvpOptionDto[] options =
        [
            new(Guid.NewGuid(), "✅", "Going", 0, null, IsAttending: true),
            new(Guid.NewGuid(), "❌", "Not going", 1, null),
        ];
        return new EventDto(
            Guid.NewGuid(), 1, 2, "Length Sample", null, DateTimeOffset.UtcNow.AddHours(2), "UTC", durationMinutes,
            3, null, null, null, EventStatus.Scheduled, options, [],
            SeriesId: repeating ? Guid.NewGuid() : null,
            RecurrenceSummary: repeating ? "Repeats daily" : null);
    }

    /// <summary>Answers every request with the same JSON; records the last request's body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public Func<HttpRequestMessage, string?>? JsonFor { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonFor?.Invoke(request) ?? "{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
