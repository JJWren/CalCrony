using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Web.Api;

/// <summary>Uniform call result: Value on success, a display-ready Error otherwise.</summary>
public record ApiResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Typed client over the CalCrony API for web pages — same ApiResult shape the bot's
/// client uses, so error surfacing is uniform across the suite. Phase A: reads + RSVPs.</summary>
public sealed class CalCronyWebApiClient(HttpClient http)
{
    /// <summary>Lists the guilds the signed-in user shares with the bot.</summary>
    public Task<ApiResult<WebGuildListResponse>> GetMyGuildsAsync(CancellationToken ct = default) =>
        SendAsync<WebGuildListResponse>(http.GetAsync("/me/guilds", ct), ct);

    /// <summary>Lists upcoming events (includePast adds the last 30 days).</summary>
    public Task<ApiResult<List<EventDto>>> ListEventsAsync(long guildId, bool includePast = false, CancellationToken ct = default) =>
        SendAsync<List<EventDto>>(http.GetAsync($"/guilds/{guildId}/events?limit=25&includePast={includePast}", ct), ct);

    /// <summary>Fetches one event with options and RSVPs.</summary>
    public Task<ApiResult<EventDto>> GetEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.GetAsync($"/events/{id}", ct), ct);

    /// <summary>Sets the signed-in user's RSVP to the given option.</summary>
    public Task<ApiResult<EventDto>> PutRsvpAsync(Guid eventId, long userId, Guid optionId, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PutAsJsonAsync($"/events/{eventId}/rsvps/{userId}", new RsvpRequest(optionId), ct), ct);

    /// <summary>Clears the signed-in user's RSVP.</summary>
    public Task<ApiResult<EventDto>> DeleteRsvpAsync(Guid eventId, long userId, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.DeleteAsync($"/events/{eventId}/rsvps/{userId}", ct), ct);

    /// <summary>Free/busy grid for an event's Going members over its window.</summary>
    public Task<ApiResult<AvailabilityResponse>> GetEventAvailabilityAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<AvailabilityResponse>(http.GetAsync($"/events/{eventId}/availability", ct), ct);

    /// <summary>Lists an event's scheduled notifications.</summary>
    public Task<ApiResult<List<EventNotificationDto>>> ListNotificationsAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<List<EventNotificationDto>>(http.GetAsync($"/events/{eventId}/notifications", ct), ct);

    /// <summary>Reads the guild's timezone and default channel.</summary>
    public Task<ApiResult<GuildSettingsDto>> GetGuildSettingsAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.GetAsync($"/guilds/{guildId}/settings", ct), ct);

    /// <summary>Gets (or mints) the guild's ICS feed token.</summary>
    public Task<ApiResult<FeedTokenDto>> GetFeedTokenAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<FeedTokenDto>(http.PostAsync($"/guilds/{guildId}/feed-token", null, ct), ct);

    /// <summary>Reads the user's personal settings.</summary>
    public Task<ApiResult<UserSettingsDto>> GetUserSettingsAsync(long userId, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.GetAsync($"/users/{userId}/settings", ct), ct);

    /// <summary>Whether the user has a linked external calendar.</summary>
    public Task<ApiResult<CalendarConnectionStatusDto>> GetCalendarStatusAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarConnectionStatusDto>(http.GetAsync($"/calendar/connections/{userId}", ct), ct);

    /// <summary>Starts a calendar OAuth link; the returned StartUrl opens the provider consent page.</summary>
    public Task<ApiResult<CalendarLinkTokenDto>> CreateCalendarLinkTokenAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarLinkTokenDto>(http.PostAsync($"/calendar/connections/{userId}/link-token", null, ct), ct);

    /// <summary>Unlinks the user's external calendar.</summary>
    public Task<ApiResult<Unit>> DisconnectCalendarAsync(long userId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/calendar/connections/{userId}", ct), ct);

    /// <summary>Creates an event (identity and channel are forced server-side for web callers).</summary>
    public Task<ApiResult<EventDto>> CreateEventAsync(long guildId, CreateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/guilds/{guildId}/events", request, ct), ct);

    /// <summary>Applies a partial event update; series occurrences require a Scope.</summary>
    public Task<ApiResult<EventDto>> UpdateEventAsync(Guid id, UpdateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PatchAsJsonAsync($"/events/{id}", request, ct), ct);

    /// <summary>Deletes an event; deleting a live series occurrence stops its series.</summary>
    public Task<ApiResult<Unit>> DeleteEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/events/{id}", ct), ct);

    /// <summary>Adds a scheduled notification (max 5; series occurrences require a Scope).</summary>
    public Task<ApiResult<EventNotificationDto>> CreateNotificationAsync(Guid eventId, CreateEventNotificationRequest request, CancellationToken ct = default) =>
        SendAsync<EventNotificationDto>(http.PostAsJsonAsync($"/events/{eventId}/notifications", request, ct), ct);

    /// <summary>Removes a notification; Series scope also retires the template spec.</summary>
    public Task<ApiResult<Unit>> DeleteNotificationAsync(Guid eventId, Guid notificationId, EditScope? scope = null, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync(
            $"/events/{eventId}/notifications/{notificationId}{(scope is null ? "" : $"?scope={scope}")}", ct), ct);

    /// <summary>Fetches a series' schedule, template, and progress.</summary>
    public Task<ApiResult<SeriesDto>> GetSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.GetAsync($"/series/{seriesId}", ct), ct);

    /// <summary>Stops a series from spawning future occurrences (the scheduled one survives).</summary>
    public Task<ApiResult<SeriesDto>> StopSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PostAsync($"/series/{seriesId}/stop", null, ct), ct);

    /// <summary>Edits a series' rule/end condition; editing an ended series revives it.</summary>
    public Task<ApiResult<SeriesDto>> UpdateSeriesAsync(Guid seriesId, UpdateSeriesRequest request, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PatchAsJsonAsync($"/series/{seriesId}", request, ct), ct);

    /// <summary>Skips the live occurrence and immediately materializes the next.</summary>
    public Task<ApiResult<SkipOccurrenceResponse>> SkipOccurrenceAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<SkipOccurrenceResponse>(http.PostAsync($"/events/{eventId}/skip", null, ct), ct);

    /// <summary>Creates a one-off reminder (identity/channel forced server-side).</summary>
    public Task<ApiResult<ReminderDto>> CreateReminderAsync(CreateReminderRequest request, CancellationToken ct = default) =>
        SendAsync<ReminderDto>(http.PostAsJsonAsync("/reminders", request, ct), ct);

    /// <summary>Updates the guild's settings (managers only).</summary>
    public Task<ApiResult<GuildSettingsDto>> PutGuildSettingsAsync(long guildId, GuildSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.PutAsJsonAsync($"/guilds/{guildId}/settings", settings, ct), ct);

    /// <summary>Updates the signed-in user's settings.</summary>
    public Task<ApiResult<UserSettingsDto>> PutUserSettingsAsync(long userId, UserSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.PutAsJsonAsync($"/users/{userId}/settings", settings, ct), ct);

    /// <summary>Parses natural-language datetime text for live previews.</summary>
    public Task<ApiResult<ParseDateTimeResponse>> ParseDateTimeAsync(ParseDateTimeRequest request, CancellationToken ct = default) =>
        SendAsync<ParseDateTimeResponse>(http.PostAsJsonAsync("/tools/parse-datetime", request, ct), ct);

    /// <summary>Lists canonical IANA timezones with current UTC-offset labels.</summary>
    public Task<ApiResult<List<TimeZoneOptionDto>>> ListTimeZonesAsync(CancellationToken ct = default) =>
        SendAsync<List<TimeZoneOptionDto>>(http.GetAsync("/tools/timezones", ct), ct);

    /// <summary>Lists the guild's polls, optionally filtered by status.</summary>
    public Task<ApiResult<List<PollDto>>> ListPollsAsync(long guildId, PollStatus? status = null, CancellationToken ct = default)
    {
        var query = "?limit=25" + (status is null ? "" : $"&status={status}");
        return SendAsync<List<PollDto>>(http.GetAsync($"/guilds/{guildId}/polls{query}", ct), ct);
    }

    /// <summary>Fetches one poll (anonymous polls only include the caller's own vote rows).</summary>
    public Task<ApiResult<PollDto>> GetPollAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.GetAsync($"/polls/{id}", ct), ct);

    /// <summary>Creates a poll (identity and channel forced server-side).</summary>
    public Task<ApiResult<PollDto>> CreatePollAsync(long guildId, CreatePollRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/guilds/{guildId}/polls", request, ct), ct);

    /// <summary>Replaces the user's vote set atomically; empty clears it.</summary>
    public Task<ApiResult<PollDto>> PutPollVotesAsync(Guid pollId, long userId, PutPollVotesRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PutAsJsonAsync($"/polls/{pollId}/votes/{userId}", request, ct), ct);

    /// <summary>Adds an option to an open poll (identity forced server-side).</summary>
    public Task<ApiResult<PollDto>> AddPollOptionAsync(Guid pollId, AddPollOptionRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/polls/{pollId}/options", request, ct), ct);

    /// <summary>Closes a poll; idempotent.</summary>
    public Task<ApiResult<PollDto>> ClosePollAsync(Guid pollId, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsync($"/polls/{pollId}/close", null, ct), ct);

    /// <summary>Converts a closed time poll's winning slot into an event.</summary>
    public Task<ApiResult<EventDto>> ConvertPollAsync(Guid pollId, ConvertPollRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/polls/{pollId}/convert", request, ct), ct);

    /// <summary>Deletes a poll and its Discord embed.</summary>
    public Task<ApiResult<Unit>> DeletePollAsync(Guid pollId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/polls/{pollId}", ct), ct);

    /// <summary>Absolute subscribe URL for an ICS feed token.</summary>
    public string FeedUrl(FeedTokenDto token) => $"{http.BaseAddress!.ToString().TrimEnd('/')}{token.Path}";

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
