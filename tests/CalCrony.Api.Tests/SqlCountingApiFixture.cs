using System.Data.Common;
using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>Counts the outbox SELECTs issued while recording, so a test can assert that a code
/// path's query count stays CONSTANT in the number of rows it touches instead of growing with
/// them. Recording is fixture-scoped and the class that owns the fixture runs its tests
/// sequentially, so the counter needs no cross-test coordination.</summary>
public sealed class DeliveryQueryCounter : DbCommandInterceptor
{
    private int count;

    private bool Recording { get; set; }

    /// <summary>Begins a fresh count.</summary>
    public void Start()
    {
        Interlocked.Exchange(ref count, 0);
        Recording = true;
    }

    /// <summary>Ends the count and returns the Deliveries SELECTs seen since <see cref="Start"/>.</summary>
    /// <returns>The number of outbox lookups.</returns>
    public int Stop()
    {
        Recording = false;
        return Volatile.Read(ref count);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Observe(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Observe(DbCommand command)
    {
        if (Recording
            && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
            && command.CommandText.Contains("\"Deliveries\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref count);
        }
    }
}

/// <summary>ApiFixture with a <see cref="DeliveryQueryCounter"/> wired in — EF Core picks
/// <see cref="IInterceptor"/> registrations up from the application service provider.</summary>
public sealed class SqlCountingApiFixture : ApiFixture
{
    public DeliveryQueryCounter Counter { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.ConfigureDbContext<CalCronyDbContext>(o => o.AddInterceptors(Counter));
}
