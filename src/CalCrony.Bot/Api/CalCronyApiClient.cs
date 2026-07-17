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

    public Task<ApiResult<List<EventDto>>> ListEventsAsync(long guildId, long? channelId = null, int limit = 10, CancellationToken ct = default)
    {
        var query = $"?limit={limit}" + (channelId is null ? "" : $"&channelId={channelId}");
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
