using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

/// <summary>Uniform call result: Value on success, a display-ready Error otherwise.</summary>
public record ApiResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Typed client for the CalCrony API. All Discord IDs cross the wire as signed 64-bit.</summary>
/// <param name="http">The configured HTTP client.</param>
public sealed class CalCronyApiClient(HttpClient http)
{
    /// <summary>Creates an event on behalf of the invoking Discord user.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> CreateEventAsync(long guildId, CreateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/guilds/{guildId}/events", request, ct), ct);

    /// <summary>Lists a guild's upcoming events (includePast adds the last 30 days).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="channelId">The Discord channel id.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="includePast">When true, widens the window to the last 30 days.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<List<EventDto>>> ListEventsAsync(
        long guildId, long? channelId = null, int limit = 10, bool includePast = false, CancellationToken ct = default)
    {
        var query = $"?limit={limit}"
            + (channelId is null ? "" : $"&channelId={channelId}")
            + (includePast ? "&includePast=true" : "");
        return SendAsync<List<EventDto>>(http.GetAsync($"/guilds/{guildId}/events{query}", ct), ct);
    }

    /// <summary>Fetches one event with options and RSVPs.</summary>
    /// <param name="id">The entity id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> GetEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.GetAsync($"/events/{id}", ct), ct);

    /// <summary>Applies a partial event update.</summary>
    /// <param name="id">The entity id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> UpdateEventAsync(Guid id, UpdateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PatchAsJsonAsync($"/events/{id}", request, ct), ct);

    /// <summary>Deletes an event; deleting a live series occurrence stops its series.</summary>
    /// <param name="id">The entity id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<Unit>> DeleteEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/events/{id}", ct), ct);

    /// <summary>Records where the bot posted the event's embed.</summary>
    /// <param name="id">The entity id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> SetMessageAsync(Guid id, SetEventMessageRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PutAsJsonAsync($"/events/{id}/message", request, ct), ct);

    /// <summary>Records (or clears with null) the Discord scheduled event mirroring an event.</summary>
    /// <param name="id">The event id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> SetNativeEventAsync(Guid id, SetNativeEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PutAsJsonAsync($"/events/{id}/native-event", request, ct), ct);

    /// <summary>Skips the live occurrence and immediately materializes the next.</summary>
    /// <param name="eventId">The event id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<SkipOccurrenceResponse>> SkipOccurrenceAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<SkipOccurrenceResponse>(http.PostAsync($"/events/{eventId}/skip", null, ct), ct);

    /// <summary>Fetches a series' schedule, template, and progress.</summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<SeriesDto>> GetSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.GetAsync($"/series/{seriesId}", ct), ct);

    /// <summary>Stops a series from spawning future occurrences (the scheduled one survives).</summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<SeriesDto>> StopSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PostAsync($"/series/{seriesId}/stop", null, ct), ct);

    /// <summary>Edits a series' rule/end condition; editing an ended series revives it.</summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<SeriesDto>> UpdateSeriesAsync(Guid seriesId, UpdateSeriesRequest request, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PatchAsJsonAsync($"/series/{seriesId}", request, ct), ct);

    /// <summary>Sets a user's RSVP to the given option.</summary>
    /// <param name="eventId">The event id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> PutRsvpAsync(Guid eventId, long userId, RsvpRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PutAsJsonAsync($"/events/{eventId}/rsvps/{userId}", request, ct), ct);

    /// <summary>Clears a user's RSVP.</summary>
    /// <param name="eventId">The event id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> DeleteRsvpAsync(Guid eventId, long userId, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.DeleteAsync($"/events/{eventId}/rsvps/{userId}", ct), ct);

    /// <summary>Reads the guild's timezone and default channel.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<GuildSettingsDto>> GetGuildSettingsAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.GetAsync($"/guilds/{guildId}/settings", ct), ct);

    /// <summary>Updates the guild's settings.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<GuildSettingsDto>> PutGuildSettingsAsync(long guildId, GuildSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.PutAsJsonAsync($"/guilds/{guildId}/settings", settings, ct), ct);

    /// <summary>Reads a user's personal settings.</summary>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<UserSettingsDto>> GetUserSettingsAsync(long userId, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.GetAsync($"/users/{userId}/settings", ct), ct);

    /// <summary>Updates a user's personal settings.</summary>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<UserSettingsDto>> PutUserSettingsAsync(long userId, UserSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.PutAsJsonAsync($"/users/{userId}/settings", settings, ct), ct);

    /// <summary>Parses natural-language datetime text server-side.</summary>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<ParseDateTimeResponse>> ParseDateTimeAsync(ParseDateTimeRequest request, CancellationToken ct = default) =>
        SendAsync<ParseDateTimeResponse>(http.PostAsJsonAsync("/tools/parse-datetime", request, ct), ct);

    /// <summary>Lists canonical IANA timezones with current UTC-offset labels.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<List<TimeZoneOptionDto>>> ListTimeZonesAsync(CancellationToken ct = default) =>
        SendAsync<List<TimeZoneOptionDto>>(http.GetAsync("/tools/timezones", ct), ct);

    /// <summary>Creates a one-off reminder from natural-language text.</summary>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<ReminderDto>> CreateReminderAsync(CreateReminderRequest request, CancellationToken ct = default) =>
        SendAsync<ReminderDto>(http.PostAsJsonAsync("/reminders", request, ct), ct);

    /// <summary>Adds a scheduled pre-event notification.</summary>
    /// <param name="eventId">The event id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventNotificationDto>> CreateNotificationAsync(Guid eventId, CreateEventNotificationRequest request, CancellationToken ct = default) =>
        SendAsync<EventNotificationDto>(http.PostAsJsonAsync($"/events/{eventId}/notifications", request, ct), ct);

    /// <summary>Polls the outbox for pending, due deliveries.</summary>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<List<DeliveryDto>>> GetPendingDeliveriesAsync(int limit = 20, CancellationToken ct = default) =>
        SendAsync<List<DeliveryDto>>(http.GetAsync($"/deliveries/pending?limit={limit}", ct), ct);

    /// <summary>Acks a delivery after the Discord post succeeded.</summary>
    /// <param name="id">The entity id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<Unit>> AckDeliveryAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<Unit>(http.PostAsync($"/deliveries/{id}/ack", null, ct), ct);

    /// <summary>Gets (or mints) the guild's ICS feed token.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<FeedTokenDto>> GetOrCreateFeedTokenAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<FeedTokenDto>(http.PostAsync($"/guilds/{guildId}/feed-token", null, ct), ct);

    /// <summary>Creates a poll on behalf of the invoking Discord user.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<PollDto>> CreatePollAsync(long guildId, CreatePollRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/guilds/{guildId}/polls", request, ct), ct);

    /// <summary>Lists a guild's polls, optionally filtered by status.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<List<PollDto>>> ListPollsAsync(long guildId, PollStatus? status = null, int limit = 25, CancellationToken ct = default)
    {
        var query = $"?limit={limit}" + (status is null ? "" : $"&status={status}");
        return SendAsync<List<PollDto>>(http.GetAsync($"/guilds/{guildId}/polls{query}", ct), ct);
    }

    /// <summary>Fetches one poll with all votes (the bot is trusted; embeds hide names on anonymous polls).</summary>
    /// <param name="id">The entity id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<PollDto>> GetPollAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.GetAsync($"/polls/{id}", ct), ct);

    /// <summary>Records where the bot posted the poll's embed.</summary>
    /// <param name="id">The entity id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<PollDto>> SetPollMessageAsync(Guid id, SetPollMessageRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PutAsJsonAsync($"/polls/{id}/message", request, ct), ct);

    /// <summary>Replaces a user's vote set atomically; empty clears it.</summary>
    /// <param name="pollId">The poll id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<PollDto>> PutPollVotesAsync(Guid pollId, long userId, PutPollVotesRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PutAsJsonAsync($"/polls/{pollId}/votes/{userId}", request, ct), ct);

    /// <summary>Adds an option to an open poll.</summary>
    /// <param name="pollId">The poll id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<PollDto>> AddPollOptionAsync(Guid pollId, AddPollOptionRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/polls/{pollId}/options", request, ct), ct);

    /// <summary>Closes a poll; idempotent.</summary>
    /// <param name="pollId">The poll id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<PollDto>> ClosePollAsync(Guid pollId, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsync($"/polls/{pollId}/close", null, ct), ct);

    /// <summary>Converts a closed time poll's winning slot into an event.</summary>
    /// <param name="pollId">The poll id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventDto>> ConvertPollAsync(Guid pollId, ConvertPollRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/polls/{pollId}/convert", request, ct), ct);

    /// <summary>Lists the guild's event templates, name-ordered.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<List<EventTemplateDto>>> ListTemplatesAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<List<EventTemplateDto>>(http.GetAsync($"/guilds/{guildId}/templates", ct), ct);

    /// <summary>Saves a template captured from an existing event.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<EventTemplateDto>> SaveTemplateAsync(long guildId, SaveTemplateRequest request, CancellationToken ct = default) =>
        SendAsync<EventTemplateDto>(http.PostAsJsonAsync($"/guilds/{guildId}/templates", request, ct), ct);

    /// <summary>Deletes a template (creator or manager).</summary>
    /// <param name="id">The template id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<Unit>> DeleteTemplateAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/templates/{id}", ct), ct);

    /// <summary>Starts a calendar OAuth link for a user.</summary>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<CalendarLinkTokenDto>> CreateCalendarLinkTokenAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarLinkTokenDto>(http.PostAsync($"/calendar/connections/{userId}/link-token", null, ct), ct);

    /// <summary>Whether the user has a linked external calendar.</summary>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<CalendarConnectionStatusDto>> GetCalendarStatusAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarConnectionStatusDto>(http.GetAsync($"/calendar/connections/{userId}", ct), ct);

    /// <summary>Unlinks the user's external calendar.</summary>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<Unit>> DisconnectCalendarAsync(long userId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/calendar/connections/{userId}", ct), ct);

    /// <summary>Runs a live free/busy check for the given users and window.</summary>
    /// <param name="request">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The call result: the value on success, a display-ready error otherwise.</returns>
    public Task<ApiResult<AvailabilityResponse>> CheckAvailabilityAsync(AvailabilityRequest request, CancellationToken ct = default) =>
        SendAsync<AvailabilityResponse>(http.PostAsJsonAsync("/calendar/availability", request, ct), ct);

    /// <summary>Empty payload marker for calls whose success carries no body.</summary>
    public readonly record struct Unit;

    private static async Task<ApiResult<T>> SendAsync<T>(Task<HttpResponseMessage> sending, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await sending;
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult<T>(default, $"API unreachable: {ex.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(Unit) || response.Content.Headers.ContentLength == 0)
                {
                    return new ApiResult<T>(default, null);
                }

                return new ApiResult<T>(await response.Content.ReadFromJsonAsync<T>(ct), null);
            }

            string? error = null;
            try
            {
                error = (await response.Content.ReadFromJsonAsync<ErrorResponse>(ct))?.Error;
            }
            catch
            {
                // Non-JSON error body; fall through to the status-code message.
            }

            return new ApiResult<T>(default, error ?? $"API error {(int)response.StatusCode}.");
        }
    }
}
