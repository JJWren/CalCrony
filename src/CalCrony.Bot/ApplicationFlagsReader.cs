using System.Net.Http.Headers;
using System.Text.Json;
using Discord;

namespace CalCrony.Bot;

/// <summary>Reads the bot's own application flags from Discord (<c>GET /applications/@me</c>) —
/// what says whether the application has the Server Members intent. One plain request with a
/// time limit and no retries, so that a Discord outage at start-up costs at most that limit
/// rather than holding the bot back; Discord.Net's own login retries such a read without end.</summary>
/// <param name="http">The HTTP client.</param>
public sealed class ApplicationFlagsReader(HttpClient http)
{
    /// <summary>How long a read may take by default.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private static readonly Uri ApplicationUri = new("https://discord.com/api/v10/applications/@me");

    private static readonly string UserAgent =
        $"DiscordBot (https://github.com/JJWren/CalCrony, {typeof(ApplicationFlagsReader).Assembly.GetName().Version})";

    /// <summary>How long a read may take before it counts as failed.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Reads the flags.</summary>
    /// <param name="token">The bot token.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The application's flags.</returns>
    /// <exception cref="TimeoutException">Discord did not answer in time.</exception>
    /// <exception cref="HttpRequestException">Discord answered with an error, or could not be reached.</exception>
    /// <exception cref="JsonException">The answer was not the application object.</exception>
    public async Task<ApplicationFlags> ReadAsync(string token, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApplicationUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // The status alone: the body and the request (which carries the token) stay out
                // of a message that is logged.
                throw new HttpRequestException(
                    $"Discord answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("flags", out var flags)
                || flags.ValueKind != JsonValueKind.Number
                || !flags.TryGetInt64(out var value))
            {
                throw new JsonException("Discord's answer has no flags.");
            }

            return (ApplicationFlags)Enum.ToObject(typeof(ApplicationFlags), value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Discord did not answer within {Timeout.TotalSeconds:0.###} seconds.");
        }
    }
}
