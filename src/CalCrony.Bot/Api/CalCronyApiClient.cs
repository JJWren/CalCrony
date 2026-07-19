using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Bot.Api;

public record ApiResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Typed client for the CalCrony API. All Discord IDs cross the wire as signed 64-bit.</summary>
public sealed class CalCronyApiClient(HttpClient http)
{
    public Task<ApiResult<EventDto>> CreateEventAsync(long guildId, CreateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/guilds/{guildId}/events", request, ct), ct);

    public Task<ApiResult<List<EventDto>>> ListEventsAsync(
        long guildId, long? channelId = null, int limit = 10, bool includePast = false, CancellationToken ct = default)
    {
        var query = $"?limit={limit}"
            + (channelId is null ? "" : $"&channelId={channelId}")
            + (includePast ? "&includePast=true" : "");
        return SendAsync<List<EventDto>>(http.GetAsync($"/guilds/{guildId}/events{query}", ct), ct);
    }

    public Task<ApiResult<EventDto>> GetEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.GetAsync($"/events/{id}", ct), ct);

    public Task<ApiResult<EventDto>> UpdateEventAsync(Guid id, UpdateEventRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PatchAsJsonAsync($"/events/{id}", request, ct), ct);

    public Task<ApiResult<Unit>> DeleteEventAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/events/{id}", ct), ct);

    public Task<ApiResult<EventDto>> SetMessageAsync(Guid id, SetEventMessageRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PutAsJsonAsync($"/events/{id}/message", request, ct), ct);

    public Task<ApiResult<SkipOccurrenceResponse>> SkipOccurrenceAsync(Guid eventId, CancellationToken ct = default) =>
        SendAsync<SkipOccurrenceResponse>(http.PostAsync($"/events/{eventId}/skip", null, ct), ct);

    public Task<ApiResult<SeriesDto>> GetSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.GetAsync($"/series/{seriesId}", ct), ct);

    public Task<ApiResult<SeriesDto>> StopSeriesAsync(Guid seriesId, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PostAsync($"/series/{seriesId}/stop", null, ct), ct);

    public Task<ApiResult<SeriesDto>> UpdateSeriesAsync(Guid seriesId, UpdateSeriesRequest request, CancellationToken ct = default) =>
        SendAsync<SeriesDto>(http.PatchAsJsonAsync($"/series/{seriesId}", request, ct), ct);

    public Task<ApiResult<EventDto>> PutRsvpAsync(Guid eventId, long userId, RsvpRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PutAsJsonAsync($"/events/{eventId}/rsvps/{userId}", request, ct), ct);

    public Task<ApiResult<EventDto>> DeleteRsvpAsync(Guid eventId, long userId, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.DeleteAsync($"/events/{eventId}/rsvps/{userId}", ct), ct);

    public Task<ApiResult<GuildSettingsDto>> GetGuildSettingsAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.GetAsync($"/guilds/{guildId}/settings", ct), ct);

    public Task<ApiResult<GuildSettingsDto>> PutGuildSettingsAsync(long guildId, GuildSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<GuildSettingsDto>(http.PutAsJsonAsync($"/guilds/{guildId}/settings", settings, ct), ct);

    public Task<ApiResult<UserSettingsDto>> GetUserSettingsAsync(long userId, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.GetAsync($"/users/{userId}/settings", ct), ct);

    public Task<ApiResult<UserSettingsDto>> PutUserSettingsAsync(long userId, UserSettingsDto settings, CancellationToken ct = default) =>
        SendAsync<UserSettingsDto>(http.PutAsJsonAsync($"/users/{userId}/settings", settings, ct), ct);

    public Task<ApiResult<ParseDateTimeResponse>> ParseDateTimeAsync(ParseDateTimeRequest request, CancellationToken ct = default) =>
        SendAsync<ParseDateTimeResponse>(http.PostAsJsonAsync("/tools/parse-datetime", request, ct), ct);

    public Task<ApiResult<List<TimeZoneOptionDto>>> ListTimeZonesAsync(CancellationToken ct = default) =>
        SendAsync<List<TimeZoneOptionDto>>(http.GetAsync("/tools/timezones", ct), ct);

    public Task<ApiResult<ReminderDto>> CreateReminderAsync(CreateReminderRequest request, CancellationToken ct = default) =>
        SendAsync<ReminderDto>(http.PostAsJsonAsync("/reminders", request, ct), ct);

    public Task<ApiResult<EventNotificationDto>> CreateNotificationAsync(Guid eventId, CreateEventNotificationRequest request, CancellationToken ct = default) =>
        SendAsync<EventNotificationDto>(http.PostAsJsonAsync($"/events/{eventId}/notifications", request, ct), ct);

    public Task<ApiResult<List<DeliveryDto>>> GetPendingDeliveriesAsync(int limit = 20, CancellationToken ct = default) =>
        SendAsync<List<DeliveryDto>>(http.GetAsync($"/deliveries/pending?limit={limit}", ct), ct);

    public Task<ApiResult<Unit>> AckDeliveryAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<Unit>(http.PostAsync($"/deliveries/{id}/ack", null, ct), ct);

    public Task<ApiResult<FeedTokenDto>> GetOrCreateFeedTokenAsync(long guildId, CancellationToken ct = default) =>
        SendAsync<FeedTokenDto>(http.PostAsync($"/guilds/{guildId}/feed-token", null, ct), ct);

    public Task<ApiResult<PollDto>> CreatePollAsync(long guildId, CreatePollRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/guilds/{guildId}/polls", request, ct), ct);

    public Task<ApiResult<List<PollDto>>> ListPollsAsync(long guildId, PollStatus? status = null, int limit = 25, CancellationToken ct = default)
    {
        var query = $"?limit={limit}" + (status is null ? "" : $"&status={status}");
        return SendAsync<List<PollDto>>(http.GetAsync($"/guilds/{guildId}/polls{query}", ct), ct);
    }

    public Task<ApiResult<PollDto>> GetPollAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.GetAsync($"/polls/{id}", ct), ct);

    public Task<ApiResult<PollDto>> SetPollMessageAsync(Guid id, SetPollMessageRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PutAsJsonAsync($"/polls/{id}/message", request, ct), ct);

    public Task<ApiResult<PollDto>> PutPollVotesAsync(Guid pollId, long userId, PutPollVotesRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PutAsJsonAsync($"/polls/{pollId}/votes/{userId}", request, ct), ct);

    public Task<ApiResult<PollDto>> AddPollOptionAsync(Guid pollId, AddPollOptionRequest request, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsJsonAsync($"/polls/{pollId}/options", request, ct), ct);

    public Task<ApiResult<PollDto>> ClosePollAsync(Guid pollId, CancellationToken ct = default) =>
        SendAsync<PollDto>(http.PostAsync($"/polls/{pollId}/close", null, ct), ct);

    public Task<ApiResult<EventDto>> ConvertPollAsync(Guid pollId, ConvertPollRequest request, CancellationToken ct = default) =>
        SendAsync<EventDto>(http.PostAsJsonAsync($"/polls/{pollId}/convert", request, ct), ct);

    public Task<ApiResult<CalendarLinkTokenDto>> CreateCalendarLinkTokenAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarLinkTokenDto>(http.PostAsync($"/calendar/connections/{userId}/link-token", null, ct), ct);

    public Task<ApiResult<CalendarConnectionStatusDto>> GetCalendarStatusAsync(long userId, CancellationToken ct = default) =>
        SendAsync<CalendarConnectionStatusDto>(http.GetAsync($"/calendar/connections/{userId}", ct), ct);

    public Task<ApiResult<Unit>> DisconnectCalendarAsync(long userId, CancellationToken ct = default) =>
        SendAsync<Unit>(http.DeleteAsync($"/calendar/connections/{userId}", ct), ct);

    public Task<ApiResult<AvailabilityResponse>> CheckAvailabilityAsync(AvailabilityRequest request, CancellationToken ct = default) =>
        SendAsync<AvailabilityResponse>(http.PostAsJsonAsync("/calendar/availability", request, ct), ct);

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
