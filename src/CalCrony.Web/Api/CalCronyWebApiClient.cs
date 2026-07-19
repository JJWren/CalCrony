using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Web.Api;

public record ApiResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Typed client over the CalCrony API for web pages — same ApiResult shape the bot's
/// client uses, so error surfacing is uniform across the suite. Phase A: reads + RSVPs.</summary>
public sealed class CalCronyWebApiClient(HttpClient http)
{
    public Task<ApiResult<WebGuildListResponse>> GetMyGuildsAsync(CancellationToken ct = default) =>
        SendAsync<WebGuildListResponse>(http.GetAsync("/me/guilds", ct), ct);

    public Task<ApiResult<List<EventDto>>> ListEventsAsync(long guildId, bool includePast = false, CancellationToken ct = default) =>
        SendAsync<List<EventDto>>(http.GetAsync($"/guilds/{guildId}/events?limit=25&includePast={includePast}", ct), ct);

    public Task<ApiResult<EventDto>> GetEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.GetAsync($"/events/{id}", ct), ct);

    public Task<ApiResult<EventDto>> PutRsvpAsync(Guid eventId, long userId, Guid optionId, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PutAsJsonAsync($"/events/{eventId}/rsvps/{userId}", new RsvpRequest(optionId), ct), ct);

    public Task<ApiResult<EventDto>> DeleteRsvpAsync(Guid eventId, long userId, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.DeleteAsync($"/events/{eventId}/rsvps/{userId}", ct), ct);

    public Task<ApiResult<AvailabilityResponse>> GetEventAvailabilityAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<AvailabilityResponse>(http.GetAsync($"/events/{eventId}/availability", ct), ct);

    public Task<ApiResult<List<EventNotificationDto>>> ListNotificationsAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<List<EventNotificationDto>>(http.GetAsync($"/events/{eventId}/notifications", ct), ct);

    public Task<ApiResult<GuildSettingsDto>> GetGuildSettingsAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.GetAsync($"/guilds/{guildId}/settings", ct), ct);

    public Task<ApiResult<FeedTokenDto>> GetFeedTokenAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<FeedTokenDto>(http.PostAsync($"/guilds/{guildId}/feed-token", null, ct), ct);

    public Task<ApiResult<UserSettingsDto>> GetUserSettingsAsync(long userId, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.GetAsync($"/users/{userId}/settings", ct), ct);

    public Task<ApiResult<CalendarConnectionStatusDto>> GetCalendarStatusAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarConnectionStatusDto>(http.GetAsync($"/calendar/connections/{userId}", ct), ct);

    public Task<ApiResult<CalendarLinkTokenDto>> CreateCalendarLinkTokenAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarLinkTokenDto>(http.PostAsync($"/calendar/connections/{userId}/link-token", null, ct), ct);

    public Task<ApiResult<Unit>> DisconnectCalendarAsync(long userId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/calendar/connections/{userId}", ct), ct);

    public Task<ApiResult<EventDto>> CreateEventAsync(long guildId, CreateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/guilds/{guildId}/events", request, ct), ct);

    public Task<ApiResult<EventDto>> UpdateEventAsync(Guid id, UpdateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PatchAsJsonAsync($"/events/{id}", request, ct), ct);

    public Task<ApiResult<Unit>> DeleteEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/events/{id}", ct), ct);

    public Task<ApiResult<EventNotificationDto>> CreateNotificationAsync(Guid eventId, CreateEventNotificationRequest request, CancellationToken ct = default) =>
        SendAsync<EventNotificationDto>(http.PostAsJsonAsync($"/events/{eventId}/notifications", request, ct), ct);

    public Task<ApiResult<Unit>> DeleteNotificationAsync(Guid eventId, Guid notificationId, EditScope? scope = null, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync(
            $"/events/{eventId}/notifications/{notificationId}{(scope is null ? "" : $"?scope={scope}")}", ct), ct);

    public Task<ApiResult<SeriesDto>> GetSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.GetAsync($"/series/{seriesId}", ct), ct);

    public Task<ApiResult<SeriesDto>> StopSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PostAsync($"/series/{seriesId}/stop", null, ct), ct);

    public Task<ApiResult<SeriesDto>> UpdateSeriesAsync(Guid seriesId, UpdateSeriesRequest request, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PatchAsJsonAsync($"/series/{seriesId}", request, ct), ct);

    public Task<ApiResult<SkipOccurrenceResponse>> SkipOccurrenceAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<SkipOccurrenceResponse>(http.PostAsync($"/events/{eventId}/skip", null, ct), ct);

    public Task<ApiResult<ReminderDto>> CreateReminderAsync(CreateReminderRequest request, CancellationToken ct = default) =>
        SendAsync<ReminderDto>(http.PostAsJsonAsync("/reminders", request, ct), ct);

    public Task<ApiResult<GuildSettingsDto>> PutGuildSettingsAsync(long guildId, GuildSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.PutAsJsonAsync($"/guilds/{guildId}/settings", settings, ct), ct);

    public Task<ApiResult<UserSettingsDto>> PutUserSettingsAsync(long userId, UserSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.PutAsJsonAsync($"/users/{userId}/settings", settings, ct), ct);

    public Task<ApiResult<ParseDateTimeResponse>> ParseDateTimeAsync(ParseDateTimeRequest request, CancellationToken ct = default) =>
        SendAsync<ParseDateTimeResponse>(http.PostAsJsonAsync("/tools/parse-datetime", request, ct), ct);

    public Task<ApiResult<List<TimeZoneOptionDto>>> ListTimeZonesAsync(CancellationToken ct = default) =>
        SendAsync<List<TimeZoneOptionDto>>(http.GetAsync("/tools/timezones", ct), ct);

    public Task<ApiResult<List<PollDto>>> ListPollsAsync(long guildId, PollStatus? status = null, CancellationToken ct = default)
    {
        var query = "?limit=25" + (status is null ? "" : $"&status={status}");
        return SendAsync<List<PollDto>>(http.GetAsync($"/guilds/{guildId}/polls{query}", ct), ct);
    }

    public Task<ApiResult<PollDto>> GetPollAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.GetAsync($"/polls/{id}", ct), ct);

    public Task<ApiResult<PollDto>> CreatePollAsync(long guildId, CreatePollRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/guilds/{guildId}/polls", request, ct), ct);

    public Task<ApiResult<PollDto>> PutPollVotesAsync(Guid pollId, long userId, PutPollVotesRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PutAsJsonAsync($"/polls/{pollId}/votes/{userId}", request, ct), ct);

    public Task<ApiResult<PollDto>> AddPollOptionAsync(Guid pollId, AddPollOptionRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/polls/{pollId}/options", request, ct), ct);

    public Task<ApiResult<PollDto>> ClosePollAsync(Guid pollId, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsync($"/polls/{pollId}/close", null, ct), ct);

    public Task<ApiResult<EventDto>> ConvertPollAsync(Guid pollId, ConvertPollRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/polls/{pollId}/convert", request, ct), ct);

    public Task<ApiResult<Unit>> DeletePollAsync(Guid pollId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/polls/{pollId}", ct), ct);

    public string FeedUrl(FeedTokenDto token) => $"{http.BaseAddress!.ToString().TrimEnd('/')}{token.Path}";

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
