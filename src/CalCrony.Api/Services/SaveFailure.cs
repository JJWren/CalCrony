using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CalCrony.Api.Services;

/// <summary>Says which rows a save could not take, for a log line: each by its entity, its key
/// and what was being done to it — never by what it holds.</summary>
public static class SaveFailure
{
    /// <summary>How many rows a description names before it counts the rest.</summary>
    public const int MaxRows = 5;

    /// <summary>The rows a failed save names, as text for a log line.</summary>
    /// <param name="failure">The failure the save threw.</param>
    /// <returns>The rows, as <see cref="Describe(IReadOnlyList{EntityEntry})"/> writes them.</returns>
    public static string Describe(DbUpdateException failure) => Describe(failure.Entries);

    /// <summary>Rows as text for a log line. Rows that were being changed or removed come before
    /// rows that were being added: the first are stored and can be looked up, and a row that was
    /// being added is stored nowhere once the save has failed.</summary>
    /// <param name="entries">The rows.</param>
    /// <returns>For example <c>Event 0b7c… (Modified), Delivery 51aa… (Added)</c>; past
    /// <see cref="MaxRows"/> the rest are counted.</returns>
    public static string Describe(IReadOnlyList<EntityEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "none named";
        }

        // OrderBy keeps the order the rows are given in within each of the two groups.
        var named = string.Join(", ", entries.OrderBy(e => e.State == EntityState.Added).Take(MaxRows).Select(Describe));
        return entries.Count > MaxRows ? $"{named} and {entries.Count - MaxRows} more" : named;
    }

    private static string Describe(EntityEntry entry)
    {
        var key = string.Join("/", entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue));
        return $"{entry.Metadata.ClrType.Name} {key} ({entry.State})";
    }
}
