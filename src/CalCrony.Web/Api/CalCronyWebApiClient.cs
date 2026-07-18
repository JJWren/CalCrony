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
