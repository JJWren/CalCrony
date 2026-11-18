using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CalCrony.Api.Services;

/// <summary>Saves what one sweep of <see cref="DeliveryScheduler"/> has gathered, inside the
/// sweep's transaction. Rows can have changed while the sweep ran, and two kinds of failure are
/// taken up here, each by letting go of what can no longer be saved and saving the rest as the
/// sweep worked it out from the rows it read: a row the sweep changed that has been deleted, and
/// a write of the sweep that would give a series a second live occurrence. Any other failure is
/// logged with the rows it names and goes on to the caller.</summary>
/// <param name="db">The sweep's database context, holding what the sweep has gathered.</param>
/// <param name="logger">The scheduler's logger.</param>
/// <param name="endedSeries">The series the sweep ended, which it names in the log once it is
/// saved. A series that is left as the sweep found it is taken off the list.</param>
public sealed class SweepSave(CalCronyDbContext db, ILogger logger, List<EventSeries> endedSeries)
{
    private bool lookedForGone;
    private int secondLiveFailures;
    private int nextLetGo;

    /// <summary>Saves, and saves again without what had to be let go of.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many next occurrences were let go of, each with the post queued for it.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return nextLetGo;
            }
            catch (DbUpdateConcurrencyException ex) when (ex.Entries.Count > 0)
            {
                // The save runs inside the sweep's transaction, so EF has put the transaction
                // back to where this save began, here and in the next clause.
                await LetGoOfRowsThatWentAsync(ex.Entries, cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSecondLiveOccurrence(ex))
            {
                if (!await LetGoOfSecondLiveOccurrencesAsync(cancellationToken))
                {
                    LogCouldNotBeSaved(ex);
                    throw;
                }
            }
            catch (DbUpdateException ex)
            {
                LogCouldNotBeSaved(ex);
                throw;
            }
        }
    }

    /// <summary>Nothing of the sweep is saved. The rows the failure names go into the log by
    /// entity and key; the failure itself goes on to the caller, which logs it.</summary>
    /// <param name="failure">The failed save.</param>
    private void LogCouldNotBeSaved(DbUpdateException failure) =>
        logger.LogError("The sweep could not be saved. Rows named by the failure: {Rows}.", SaveFailure.Describe(failure));

    /// <summary>A row the sweep changed has been deleted while the sweep ran: a notification
    /// removed, an event or a poll deleted. No entity here has a concurrency token, so a change
    /// that finds no row to apply to can only mean that the row is gone. The failure names one
    /// row. The first time, every notification, event and poll the sweep changed is looked for,
    /// so that all that are gone are let go of together, however many they are, and the ones
    /// still there are held until the sweep is saved. Should a later save fail in the same way,
    /// the rows it names are let go of, so the loop ends.</summary>
    /// <param name="named">The rows the failed save names.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private async Task LetGoOfRowsThatWentAsync(IReadOnlyList<EntityEntry> named, CancellationToken cancellationToken)
    {
        var gone = lookedForGone ? [.. named] : await GoneAmongChangedAsync(named, cancellationToken);
        lookedForGone = true;
        logger.LogWarning(
            "Rows the sweep changed were deleted while it ran; they are let go of and the save is tried again without them: {Rows}.",
            SaveFailure.Describe(gone));
        foreach (var entry in gone)
        {
            entry.State = EntityState.Detached;
            if (entry.Entity is Event { SeriesId: Guid seriesId })
            {
                // Whoever deleted the occurrence decided what becomes of its series, and the
                // next sweep takes the series as it then stands.
                LeaveAsFound(seriesId);
            }
        }
    }

    /// <summary>The rows the sweep changed that are no longer there: the ones a failed save
    /// names, and every other changed notification, event and poll that has gone as well. The
    /// rows that are still there are locked, so none of them can go before the sweep is saved.</summary>
    /// <param name="named">The rows the failed save names.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The rows to let go of.</returns>
    private async Task<List<EntityEntry>> GoneAmongChangedAsync(
        IReadOnlyList<EntityEntry> named, CancellationToken cancellationToken)
    {
        var gone = new List<EntityEntry>(named);

        // Events before their notifications: the order in which a delete takes them.
        await AddGoneAsync<Event>(ids =>
            $"""SELECT "Id" AS "Value" FROM "Events" WHERE "Id" = ANY({ids}) FOR NO KEY UPDATE""");
        await AddGoneAsync<EventNotification>(ids =>
            $"""SELECT "Id" AS "Value" FROM "EventNotifications" WHERE "Id" = ANY({ids}) FOR NO KEY UPDATE""");
        await AddGoneAsync<Poll>(ids =>
            $"""SELECT "Id" AS "Value" FROM "Polls" WHERE "Id" = ANY({ids}) FOR NO KEY UPDATE""");
        return gone;

        async Task AddGoneAsync<T>(Func<Guid[], FormattableString> stillThere)
            where T : class
        {
            var changed = db.ChangeTracker.Entries<T>()
                .Where(e => e.State == EntityState.Modified && !gone.Any(g => ReferenceEquals(g.Entity, e.Entity)))
                .ToDictionary(e => (Guid)e.Property("Id").CurrentValue!, e => (EntityEntry)e);
            if (changed.Count == 0)
            {
                return;
            }

            var there = (await db.Database.SqlQuery<Guid>(stillThere([.. changed.Keys])).ToListAsync(cancellationToken))
                .ToHashSet();
            gone.AddRange(changed.Where(c => !there.Contains(c.Key)).Select(c => c.Value));
        }
    }

    /// <summary>Whether a save failed on the rule that a series has one live occurrence, which
    /// the database holds it to with a partial unique index.</summary>
    /// <param name="failure">The failed save.</param>
    /// <returns>True when it did.</returns>
    private static bool IsSecondLiveOccurrence(DbUpdateException failure) =>
        failure.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
            ConstraintName: CalCronyDbContext.LiveOccurrenceIndex,
        };

    /// <summary>Something this sweep writes would give a series a second live occurrence: the
    /// next occurrence it made, or its own change of an occurrence to a live status. The first
    /// time, the series concerned are looked at, so that only what stands against a stored
    /// occurrence is let go of, and their stored occurrences are held until the sweep is saved;
    /// where nothing stands against one by then, the save is tried again as it is. Should the
    /// save fail in this way a second time, every next occurrence the sweep still has is let go
    /// of. Where it has none left, and at a third time, the failure is not taken up, so the loop
    /// ends.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>False when the failure is not taken up, and goes on to the caller.</returns>
    private async Task<bool> LetGoOfSecondLiveOccurrencesAsync(CancellationToken cancellationToken)
    {
        List<Guid> next;
        switch (secondLiveFailures++)
        {
            case 0:
                (next, var changes) = await SecondLiveOccurrencesAsync(cancellationToken);
                if (next.Count == 0 && changes.Count == 0)
                {
                    logger.LogWarning(
                        "The save met a second live occurrence of a series, and nothing the sweep writes stands against one now. The save is tried again as it is.");
                }
                else
                {
                    var letGo = new List<string>();
                    if (next.Count > 0)
                    {
                        letGo.Add($"the next occurrence made for series {string.Join(", ", next)}");
                    }

                    if (changes.Count > 0)
                    {
                        letGo.Add($"the sweep's change to {SaveFailure.Describe(changes)}");
                    }

                    logger.LogWarning(
                        "The save met a second live occurrence of a series. Let go of, and the save is tried again without them: {LetGo}.",
                        string.Join("; ", letGo));
                }

                foreach (var change in changes)
                {
                    // What is stored stands. What the sweep queued for the occurrence stays
                    // queued, as it does for a row that went.
                    change.State = EntityState.Detached;
                }

                break;
            case 1:
                next = SeriesGivenANextOccurrence();
                if (next.Count == 0)
                {
                    return false;
                }

                logger.LogWarning(
                    "The save met a second live occurrence of a series once more. Every next occurrence the sweep made is let go of, and the save is tried again without them: series {Series}.",
                    string.Join(", ", next));
                break;
            default:
                return false;
        }

        foreach (var seriesId in next)
        {
            LeaveAsFound(seriesId);
        }

        return true;
    }

    private static bool IsLive(EventStatus status) => status is EventStatus.Scheduled or EventStatus.Started;

    /// <summary>The series this sweep made a next occurrence for and still has it.</summary>
    /// <returns>Their ids.</returns>
    private List<Guid> SeriesGivenANextOccurrence() =>
        [.. db.ChangeTracker.Entries<Event>()
            .Where(e => e.State == EntityState.Added && e.Entity.SeriesId is not null)
            .Select(e => e.Entity.SeriesId!.Value)
            .Distinct()];

    /// <summary>What of this sweep's work would give a series a second live occurrence, the stored
    /// occurrences being as they are: the next occurrence it made for a series, and its own
    /// change of an occurrence to a live status. Every stored occurrence of the series concerned
    /// is locked first, so none of them can change before the sweep is saved.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Two lists: the series whose next occurrence is to be let go of, and the
    /// occurrences whose change by the sweep is to be let go of.</returns>
    private async Task<(List<Guid> Next, List<EntityEntry> Changes)> SecondLiveOccurrencesAsync(
        CancellationToken cancellationToken)
    {
        // The occurrences the sweep is changing. The save that failed is undone, so for these
        // the status the sweep writes decides, not the one that is stored.
        var writing = db.ChangeTracker.Entries<Event>()
            .Where(e => e.State == EntityState.Modified)
            .ToDictionary(e => e.Entity.Id);
        var made = SeriesGivenANextOccurrence();
        Guid[] ids = [.. made.Union(writing.Values
            .Where(e => IsLive(e.Entity.Status) && e.Entity.SeriesId is not null)
            .Select(e => e.Entity.SeriesId!.Value))];
        await db.Database.ExecuteSqlAsync(
            $"""SELECT "Id" FROM "Events" WHERE "SeriesId" = ANY({ids}) FOR NO KEY UPDATE""", cancellationToken);
        var stored = await db.Events.AsNoTracking()
            .Where(e => e.SeriesId != null && ids.Contains(e.SeriesId.Value))
            .Select(e => new { e.Id, SeriesId = e.SeriesId!.Value, e.Status })
            .ToListAsync(cancellationToken);

        var next = new List<Guid>();
        var changes = new List<EntityEntry>();
        foreach (var series in stored.GroupBy(e => e.SeriesId))
        {
            // Live as it is stored, and not being changed by the sweep.
            var standing = series.Any(e => !writing.ContainsKey(e.Id) && IsLive(e.Status));

            // Live by the sweep's own change.
            var setLive = series
                .Where(e => writing.TryGetValue(e.Id, out var entry) && IsLive(entry.Entity.Status))
                .Select(e => (EntityEntry)writing[e.Id])
                .ToList();
            if (standing)
            {
                // A stored occurrence that stands comes first. The sweep worked its change to
                // the other one out from a status that one no longer has.
                changes.AddRange(setLive);
            }

            if ((standing || setLive.Count > 0) && made.Contains(series.Key))
            {
                next.Add(series.Key);
            }
        }

        return (next, changes);
    }

    /// <summary>Leaves a series as the sweep found it: the next occurrence the sweep made for it
    /// is let go of, with the options, the notifications and the post made with it, and what
    /// the sweep worked out for the series itself is dropped.</summary>
    /// <param name="seriesId">The series.</param>
    private void LeaveAsFound(Guid seriesId)
    {
        var spawned = db.ChangeTracker.Entries<Event>()
            .Where(e => e.State == EntityState.Added && e.Entity.SeriesId == seriesId)
            .ToList();
        foreach (var occurrence in spawned)
        {
            var eventId = occurrence.Entity.Id;
            var post = JsonSerializer.Serialize(new PostEventMessagePayload(eventId));
            var withIt = db.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added && e.Entity switch
                {
                    RsvpOption option => option.EventId == eventId,
                    EventNotification notification => notification.EventId == eventId,
                    Delivery delivery => delivery.Type == DeliveryType.PostEventMessage && delivery.PayloadJson == post,
                    _ => false,
                })
                .ToList();
            foreach (var row in withIt)
            {
                row.State = EntityState.Detached;
            }

            occurrence.State = EntityState.Detached;
        }

        var series = db.ChangeTracker.Entries<EventSeries>().FirstOrDefault(e => e.Entity.Id == seriesId);
        if (series is { State: EntityState.Modified })
        {
            series.CurrentValues.SetValues(series.OriginalValues);
            series.State = EntityState.Unchanged;
        }

        endedSeries.RemoveAll(ended => ended.Id == seriesId);
        nextLetGo += spawned.Count;
    }
}
