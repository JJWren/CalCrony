using System.Net;
using System.Text;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The stand-in webhook the announce-step tests post to. Those tests run only where
/// bash, jq and curl exist, so the stand-in itself is checked here on every machine, with a
/// client that asks before sending its body and with one that does not.</summary>
public class FakeWebhookTests
{
    private static readonly string Body = "{\"content\": \"" + new string('a', 3000) + " — 🎉\"}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task It_keeps_the_request_it_receives_byte_for_byte(bool asksBeforeSending)
    {
        using FakeWebhook webhook = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<ReceivedRequest> receiving = webhook.ReceiveAsync(204, timeout.Token);

        using HttpResponseMessage response = await PostAsync(webhook.Url, asksBeforeSending, timeout.Token);
        ReceivedRequest received = await receiving;

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.StartsWith("POST /api/webhooks/1/not-a-real-token ", received.RequestLine, StringComparison.Ordinal);
        Assert.StartsWith("application/json", received.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Equal(asksBeforeSending, received.Headers.ContainsKey("Expect"));
        Assert.Equal(Body, Encoding.UTF8.GetString(received.Body));
    }

    [Fact]
    public async Task It_answers_with_the_error_it_is_told_to()
    {
        using FakeWebhook webhook = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<ReceivedRequest> receiving = webhook.ReceiveAsync(400, timeout.Token);

        using HttpResponseMessage response = await PostAsync(webhook.Url, false, timeout.Token);
        await receiving;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Its_address_is_on_this_machine_and_nowhere_else()
    {
        using FakeWebhook webhook = new();

        Assert.StartsWith("http://127.0.0.1:", webhook.Url, StringComparison.Ordinal);
    }

    /// <summary>Posts the test body to an address.</summary>
    /// <param name="url">The address.</param>
    /// <param name="asksBeforeSending">Whether to send <c>Expect: 100-continue</c> and wait for
    /// the go-ahead, as curl does for a large body.</param>
    /// <param name="cancellation">Stops the wait.</param>
    /// <returns>The response.</returns>
    private static async Task<HttpResponseMessage> PostAsync(string url, bool asksBeforeSending, CancellationToken cancellation)
    {
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.ExpectContinue = asksBeforeSending;

        return await client.SendAsync(request, cancellation);
    }
}
