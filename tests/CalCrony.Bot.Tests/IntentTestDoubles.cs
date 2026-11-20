using System.Net;
using CalCrony.Bot;
using Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.Tests;

/// <summary>A stand-in for Discord's HTTP API: answers every request the same way and records them.</summary>
internal sealed class StubDiscord : HttpMessageHandler
{
    private readonly Func<CancellationToken, Task<HttpResponseMessage>> answer;
    private int calls;

    private StubDiscord(Func<CancellationToken, Task<HttpResponseMessage>> answer) => this.answer = answer;

    /// <summary>The requests received, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>How many requests were received.</summary>
    public int Calls => Volatile.Read(ref calls);

    public static StubDiscord Json(string body) => Status(HttpStatusCode.OK, body);

    public static StubDiscord Answering(ApplicationFlags flags) => Json($$"""{"id": "1", "flags": {{(long)flags}}}""");

    public static StubDiscord Status(HttpStatusCode status, string body) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    /// <summary>Never answers; only a cancellation ends the request.</summary>
    public static StubDiscord Silent() =>
        new(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

    public ApplicationFlagsReader Reader(TimeSpan? timeout = null) =>
        new(new HttpClient(this)) { Timeout = timeout ?? ApplicationFlagsReader.DefaultTimeout };

    /// <summary>Waits, up to a deadline, until at least this many requests have arrived.</summary>
    /// <param name="count">The number of requests to wait for.</param>
    /// <returns>A task that ends when they have; it fails after ten seconds.</returns>
    public async Task WaitForCallsAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Calls < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {Calls} of {count} requests arrived.");
            }

            await Task.Delay(20);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        Interlocked.Increment(ref calls);
        return answer(cancellationToken);
    }
}

/// <summary>A host lifetime that records stops.</summary>
internal sealed class FakeLifetime : IHostApplicationLifetime
{
    public CancellationTokenSource Stopping { get; } = new();

    public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Stops { get; private set; }

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => Stopping.Token;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
        Stops++;
        Stopped.TrySetResult();
    }
}

/// <summary>A logger that keeps the level and text of every line.</summary>
/// <typeparam name="T">The category.</typeparam>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Text)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
