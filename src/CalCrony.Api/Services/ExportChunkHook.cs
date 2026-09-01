namespace CalCrony.Api.Services;

/// <summary>Test seam for the streaming CSV export: awaited after each chunk has been flushed to
/// the response, with the zero-based chunk index. Production registers an empty instance; a test
/// sets the delegate on that same singleton (and clears it afterwards) to mutate data between
/// chunks and prove the keyset walk is exactly-once. A seam beats relying on response
/// backpressure, which the in-memory test server does not apply deterministically.</summary>
public sealed class ExportChunkHook
{
    /// <summary>Invoked after each flushed chunk; null (the default) does nothing.</summary>
    public Func<int, CancellationToken, Task>? AfterChunkFlushed { get; set; }
}
