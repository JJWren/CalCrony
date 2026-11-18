using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CalCrony.Api.Tests;

/// <summary>Records the text of every command the API's database context sends, in order, so a
/// test can say which tables a request read and how many reads it took — things an answer does
/// not show. A test notes <see cref="Count"/> before its request and reads <see cref="Since"/>
/// after it; seeding goes through the same context, so it is recorded too and falls before the
/// mark.</summary>
public sealed class DbCommandRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> commands = new();

    /// <summary>How many commands have been recorded so far.</summary>
    public int Count => commands.Count;

    /// <summary>When set, called with the text of each command that reads rows and is sent
    /// asynchronously — as every read of the API is — after it is recorded and before it is sent,
    /// and the request waits for it: a test can change the database at a chosen point of a
    /// request. A hook that writes through the API's own context should clear itself first, or
    /// its own commands come back to it.</summary>
    public Func<string, Task>? BeforeReader { get; set; }

    /// <summary>The commands recorded after the first <paramref name="mark"/> of them.</summary>
    /// <param name="mark">A value of <see cref="Count"/> noted earlier.</param>
    /// <returns>The command texts, in the order they were sent.</returns>
    public IReadOnlyList<string> Since(int mark) => [.. commands.Skip(mark)];

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        commands.Enqueue(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        commands.Enqueue(command.CommandText);
        if (BeforeReader is { } hook)
        {
            await hook(command.CommandText);
        }

        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        commands.Enqueue(command.CommandText);
        return base.ScalarExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        commands.Enqueue(command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        commands.Enqueue(command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        commands.Enqueue(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
