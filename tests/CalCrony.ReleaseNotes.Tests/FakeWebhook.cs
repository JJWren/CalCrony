using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>One HTTP request as the stand-in webhook received it.</summary>
/// <param name="RequestLine">The request's first line: method, path and protocol.</param>
/// <param name="Headers">The request's headers, by name whatever its case.</param>
/// <param name="Body">The request's body, byte for byte.</param>
internal sealed record ReceivedRequest(string RequestLine, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>A stand-in for the Discord webhook: a listener on this machine's loopback address that
/// takes one request, keeps it, and answers. Nothing leaves the machine.</summary>
internal sealed class FakeWebhook : IDisposable
{
    private const string HeaderEnd = "\r\n\r\n";

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);

    /// <summary>Starts listening on a free port.</summary>
    public FakeWebhook()
    {
        listener.Start();
    }

    /// <summary>The address to post to, shaped like a webhook's.</summary>
    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/api/webhooks/1/not-a-real-token";

    /// <summary>Takes the next request and answers it.</summary>
    /// <param name="statusCode">The status to answer with: 204 as Discord does, or an error.</param>
    /// <param name="cancellation">Stops the wait.</param>
    /// <returns>The request received.</returns>
    public async Task<ReceivedRequest> ReceiveAsync(int statusCode, CancellationToken cancellation)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellation);
        NetworkStream stream = client.GetStream();
        using MemoryStream received = new();
        int headerLength = -1;

        while (headerLength < 0)
        {
            await ReadMoreAsync(stream, received, cancellation);
            headerLength = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length)
                .IndexOf(HeaderEnd, StringComparison.Ordinal);
        }

        string[] head = Encoding.ASCII.GetString(received.GetBuffer(), 0, headerLength).Split("\r\n");
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in head.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // curl asks before sending a body of more than a kilobyte.
        if (headers.TryGetValue("Expect", out string? expectation)
            && expectation.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 100 Continue{HeaderEnd}"), cancellation);
        }

        int bodyStart = headerLength + HeaderEnd.Length;
        int bodyLength = int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture);

        while (received.Length < bodyStart + bodyLength)
        {
            await ReadMoreAsync(stream, received, cancellation);
        }

        string reason = statusCode == 204 ? "No Content" : "Bad Request";
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {reason}\r\nContent-Length: 0\r\nConnection: close{HeaderEnd}"),
            cancellation);

        return new ReceivedRequest(head[0], headers, received.ToArray()[bodyStart..(bodyStart + bodyLength)]);
    }

    /// <summary>Stops listening.</summary>
    public void Dispose() => listener.Dispose();

    /// <summary>Reads what has arrived on a connection into a buffer.</summary>
    /// <param name="stream">The connection.</param>
    /// <param name="received">Everything received so far.</param>
    /// <param name="cancellation">Stops the wait.</param>
    /// <returns>A task that ends when more has been read.</returns>
    private static async Task ReadMoreAsync(NetworkStream stream, MemoryStream received, CancellationToken cancellation)
    {
        byte[] buffer = new byte[16 * 1024];
        int read = await stream.ReadAsync(buffer, cancellation);

        if (read == 0)
        {
            throw new IOException("The request ended before it was complete.");
        }

        received.Write(buffer, 0, read);
    }
}
