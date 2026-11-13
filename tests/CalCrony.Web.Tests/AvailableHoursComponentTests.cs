using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>The available-hours card on My settings: what it shows when nothing is saved and when
/// a week is, what Save sends, when Save is refused before it is sent, Reset, a failed load, the
/// zone it names, and the notice about what saving hours allows.</summary>
public class AvailableHoursComponentTests : BunitContext
{
    private const long UserId = 42;
    private const string HoursPath = "/users/42/available-hours";

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static readonly AvailableHoursDto NothingSaved = new(
        false,
        [.. MondayFirst.Select(day => new AvailableHoursDayDto(
            day, AvailableHoursLimits.DefaultStartMinute, AvailableHoursLimits.DefaultEndMinute))]);

    private static readonly AvailableHoursDto Evenings = new(
        true, [new(DayOfWeek.Monday, 1080, 1380), new(DayOfWeek.Saturday, 600, 1440)]);

    [Fact]
    public void With_nothing_saved_the_default_week_is_shown_ticked_under_a_note_saying_it_applies()
    {
        UseApi().Answer(HttpMethod.Get, NothingSaved);

        var cut = RenderCard();

        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));
        Assert.Equal(
            "You haven't saved hours, so the default applies: 8 AM–11 PM every day.", cut.Find("#ah-default-note").TextContent);
        Assert.All(MondayFirst, day =>
        {
            Assert.True(cut.Find($"#ah-on-{day}").HasAttribute("checked"));
            Assert.Equal("480", cut.Find($"#ah-from-{day}").GetAttribute("value"));
            Assert.Equal("1380", cut.Find($"#ah-to-{day}").GetAttribute("value"));
        });

        // There is nothing to take back yet; the default week itself can be saved as it stands.
        Assert.True(cut.Find("#ah-reset").HasAttribute("disabled"));
        Assert.False(cut.Find("#ah-save").HasAttribute("disabled"));
    }

    [Fact]
    public void The_rows_run_monday_first_and_offer_every_half_hour()
    {
        UseApi().Answer(HttpMethod.Get, NothingSaved);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));

        Assert.Equal(MondayFirst.Select(day => day.ToString()), cut.FindAll(".ah-row").Select(row => row.GetAttribute("data-day")));

        // A start can be any half hour of the day; an end is half an hour later each, so the last is midnight.
        var from = cut.FindAll("#ah-from-Monday option").Select(option => option.TextContent).ToList();
        var to = cut.FindAll("#ah-to-Monday option").Select(option => option.TextContent).ToList();
        Assert.Equal(48, from.Count);
        Assert.Equal(("12 AM", "12:30 AM", "11:30 PM"), (from[0], from[1], from[^1]));
        Assert.Equal(48, to.Count);
        Assert.Equal(("12:30 AM", "11:30 PM", "midnight"), (to[0], to[^2], to[^1]));
    }

    [Fact]
    public void A_saved_week_shows_its_days_ticked_and_the_others_greyed_out_at_the_default_times()
    {
        UseApi().Answer(HttpMethod.Get, Evenings);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));

        Assert.Empty(cut.FindAll("#ah-default-note"));
        Assert.True(cut.Find("#ah-on-Monday").HasAttribute("checked"));
        Assert.Equal("1080", cut.Find("#ah-from-Monday").GetAttribute("value"));
        Assert.Equal("1380", cut.Find("#ah-to-Monday").GetAttribute("value"));
        Assert.Equal("1440", cut.Find("#ah-to-Saturday").GetAttribute("value"));

        Assert.False(cut.Find("#ah-on-Tuesday").HasAttribute("checked"));
        Assert.True(cut.Find("#ah-from-Tuesday").HasAttribute("disabled"));
        Assert.True(cut.Find("#ah-to-Tuesday").HasAttribute("disabled"));
        Assert.Equal("480", cut.Find("#ah-from-Tuesday").GetAttribute("value"));

        Assert.False(cut.Find("#ah-reset").HasAttribute("disabled"));
    }

    [Fact]
    public void Save_sends_the_ticked_rows_and_nothing_else()
    {
        var api = UseApi();
        api.Answer(HttpMethod.Get, Evenings);
        api.Answer(HttpMethod.Put, new AvailableHoursDto(true, [new(DayOfWeek.Monday, 1080, 1440), new(DayOfWeek.Sunday, 480, 1380)]));

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));

        cut.Find("#ah-to-Monday").Change("1440");       // Monday now runs to midnight
        cut.Find("#ah-on-Saturday").Change(false);      // Saturday is off
        cut.Find("#ah-on-Sunday").Change(true);         // Sunday is on, at the times it showed
        cut.Find("#ah-save").Click();

        cut.WaitForAssertion(() => Assert.Equal("✓ Saved.", cut.Find("#ah-message").TextContent));
        var put = Assert.Single(api.Requests, request => request.Method == HttpMethod.Put);
        Assert.Equal(HoursPath, put.Path);
        Assert.Equal(
            [new AvailableHoursDayDto(DayOfWeek.Monday, 1080, 1440), new AvailableHoursDayDto(DayOfWeek.Sunday, 480, 1380)],
            JsonSerializer.Deserialize<SetAvailableHoursRequest>(put.Body!, JsonWeb)!.Days);

        // The rows show what the API stored, and Saturday keeps the times it had for when it is ticked again.
        Assert.False(cut.Find("#ah-on-Saturday").HasAttribute("checked"));
        Assert.Equal("600", cut.Find("#ah-from-Saturday").GetAttribute("value"));
        Assert.Equal("1440", cut.Find("#ah-to-Saturday").GetAttribute("value"));
    }

    [Fact]
    public void With_no_day_ticked_save_is_disabled_and_the_card_says_what_to_do()
    {
        var api = UseApi();
        api.Answer(HttpMethod.Get, new AvailableHoursDto(true, [new(DayOfWeek.Monday, 1080, 1380)]));

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));

        cut.Find("#ah-on-Monday").Change(false);

        Assert.True(cut.Find("#ah-save").HasAttribute("disabled"));
        Assert.Contains("Tick at least one day, or reset to the default.", cut.Markup);
        Assert.DoesNotContain(api.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public void A_ticked_row_whose_start_is_not_before_its_end_disables_save_and_says_so_beside_it()
    {
        var api = UseApi();
        api.Answer(HttpMethod.Get, Evenings);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));

        cut.Find("#ah-from-Monday").Change("1380");     // 11 PM to 11 PM

        Assert.True(cut.Find("#ah-save").HasAttribute("disabled"));
        Assert.Contains("The start has to be before the end.", cut.Find(".ah-row[data-day=Monday]").TextContent);
        Assert.DoesNotContain("The start has to be before the end.", cut.Find(".ah-row[data-day=Saturday]").TextContent);

        // An unticked row is not checked: its times are not sent.
        cut.Find("#ah-on-Monday").Change(false);
        Assert.False(cut.Find("#ah-save").HasAttribute("disabled"));
        Assert.DoesNotContain("The start has to be before the end.", cut.Markup);
    }

    [Fact]
    public void A_refusal_from_the_api_is_shown_as_it_is_and_the_form_stays()
    {
        var api = UseApi();
        api.Answer(HttpMethod.Get, Evenings);
        api.Answer(HttpMethod.Put, new ErrorResponse("Monday: times are in 30-minute steps."), HttpStatusCode.BadRequest);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));
        cut.Find("#ah-save").Click();

        cut.WaitForAssertion(() => Assert.Equal("❌ Monday: times are in 30-minute steps.", cut.Find("#ah-message").TextContent));
        Assert.True(cut.Find("#ah-on-Saturday").HasAttribute("checked"));
        Assert.False(cut.Find("#ah-reset").HasAttribute("disabled"));
    }

    [Fact]
    public void Reset_clears_the_saved_hours_and_shows_the_default_week_again()
    {
        var api = UseApi();
        api.Answer(HttpMethod.Get, Evenings);
        api.Answer(HttpMethod.Delete, null, HttpStatusCode.NoContent);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));
        cut.Find("#ah-reset").Click();

        cut.WaitForAssertion(() => Assert.Equal("✓ Back to the default.", cut.Find("#ah-message").TextContent));
        Assert.Equal(HoursPath, Assert.Single(api.Requests, request => request.Method == HttpMethod.Delete).Path);
        Assert.NotEmpty(cut.FindAll("#ah-default-note"));
        Assert.All(MondayFirst, day =>
        {
            Assert.True(cut.Find($"#ah-on-{day}").HasAttribute("checked"));
            Assert.Equal("480", cut.Find($"#ah-from-{day}").GetAttribute("value"));
            Assert.Equal("1380", cut.Find($"#ah-to-{day}").GetAttribute("value"));
        });
        Assert.True(cut.Find("#ah-reset").HasAttribute("disabled"));
    }

    [Fact]
    public void A_failed_reset_leaves_the_saved_week_on_screen()
    {
        var api = UseApi();
        api.Answer(HttpMethod.Get, Evenings);
        api.Answer(HttpMethod.Delete, new ErrorResponse("Something went wrong."), HttpStatusCode.InternalServerError);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));
        cut.Find("#ah-reset").Click();

        cut.WaitForAssertion(() => Assert.Equal("❌ Something went wrong.", cut.Find("#ah-message").TextContent));
        Assert.Empty(cut.FindAll("#ah-default-note"));
        Assert.Equal("1080", cut.Find("#ah-from-Monday").GetAttribute("value"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":\"The database is unreachable.\"}", "The database is unreachable.")]
    [InlineData(HttpStatusCode.OK, "{}", "the answer could not be read.")]      // an answer with no week in it
    public void When_the_hours_cannot_be_loaded_the_card_says_so_and_offers_nothing_to_save(
        HttpStatusCode status, string body, string reason)
    {
        var api = UseApi();
        api.AnswerRaw(HttpMethod.Get, body, status);

        var cut = RenderCard();

        cut.WaitForAssertion(() => Assert.Contains($"Couldn't load your available hours: {reason}", cut.Markup));
        Assert.Empty(cut.FindAll(".ah-row"));
        Assert.Empty(cut.FindAll("#ah-save"));
        Assert.Empty(cut.FindAll("#ah-reset"));
    }

    [Fact]
    public void The_card_names_the_members_timezone_as_the_one_its_times_are_read_in()
    {
        UseApi().Answer(HttpMethod.Get, Evenings);

        var cut = RenderCard("America/Chicago");

        cut.WaitForAssertion(() => Assert.Equal(
            "Times are read in your timezone, America/Chicago.", cut.Find("#ah-zone").TextContent.Trim()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Without_a_timezone_the_card_says_whose_applies_and_how_to_set_one(string? timeZone)
    {
        UseApi().Answer(HttpMethod.Get, Evenings);

        var cut = RenderCard(timeZone);

        cut.WaitForAssertion(() => Assert.Contains("You haven't set a timezone", cut.Find("#ah-zone").TextContent));
        var zone = string.Join(" ", cut.Find("#ah-zone").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            "You haven't set a timezone, so these times are read in the timezone of the server someone looks you up from. " +
            "Set yours above to make them mean the same everywhere.",
            zone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_card_always_says_what_saving_hours_allows(bool saved)
    {
        UseApi().Answer(HttpMethod.Get, saved ? Evenings : NothingSaved);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));

        var notice = string.Join(" ", cut.Find("#ah-notice").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            "Saved hours can be looked up by name: members of the servers you share with CalCrony can list your free slots " +
            "with /availability slots, which shows roughly the hours you keep and, if your calendar is linked, " +
            "when you're busy inside them. Reset to default takes that back.",
            notice);
    }

    [Fact]
    public void The_card_reads_and_writes_the_hours_routes_only()
    {
        var api = UseApi();
        api.Answer(HttpMethod.Get, Evenings);
        api.Answer(HttpMethod.Put, Evenings);

        var cut = RenderCard();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".ah-row").Count));
        cut.Find("#ah-save").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#ah-message")));

        Assert.All(api.Requests, request => Assert.Equal(HoursPath, request.Path));
    }

    private IRenderedComponent<AvailableHoursCard> RenderCard(string? timeZone = "America/Chicago") =>
        Render<AvailableHoursCard>(parameters => parameters
            .Add(card => card.UserId, UserId)
            .Add(card => card.TimeZone, timeZone));

    private RecordingHandler UseApi()
    {
        var handler = new RecordingHandler();
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        return handler;
    }

    /// <summary>Answers each HTTP method with what a test set for it, and keeps every request made.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<HttpMethod, (string Body, HttpStatusCode Status)> answers = [];

        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        public void Answer(HttpMethod method, object? body, HttpStatusCode status = HttpStatusCode.OK) =>
            AnswerRaw(method, body is null ? "" : JsonSerializer.Serialize(body, JsonWeb), status);

        public void AnswerRaw(HttpMethod method, string body, HttpStatusCode status) => answers[method] = (body, status);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            var (body, status) = answers.GetValueOrDefault(request.Method, ("{}", HttpStatusCode.OK));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
