using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>How a free-slots lookup ended: the answer, or a user-facing reason it could not be
/// given (the first day was not understood, has already passed, or is more than a year away; or
/// a member was named who has not saved their available hours).</summary>
/// <param name="Response">The answer, when the lookup succeeded.</param>
/// <param name="Error">The user-facing reason, when it did not.</param>
public sealed record FreeSlotsOutcome(FreeSlotsResponse? Response, string? Error);

/// <summary>
/// Answers "when are these members free?" over a run of whole days: resolves the caller's zone and
/// first day, reads each linked calendar once for the whole range through
/// <see cref="CalendarAvailabilityService"/>, and leaves the arithmetic to
/// <see cref="FreeSlotCalculator"/>. Nothing a calendar returns is stored. Each counted member is
/// on the hours they saved, or on the default hours (<see cref="WeeklyHours.Default"/>) when they
/// have saved none, read in that member's own zone; a member with saved hours and no linked
/// calendar is counted on the hours alone.
/// </summary>
/// <param name="db">The database context.</param>
/// <param name="availability">The availability service.</param>
/// <param name="parser">The natural-language datetime parser.</param>
/// <param name="clock">The time source.</param>
public sealed class FreeSlotsService(
    CalCronyDbContext db,
    CalendarAvailabilityService availability,
    NaturalDateTimeParser parser,
    IClock clock)
{
    /// <summary>What a caller is told when they name a member who has not saved their available
    /// hours. The same text whether or not that member has linked a calendar, or has ever used
    /// CalCrony: it is decided before anything else about them is looked at.</summary>
    public const string NamedMemberHasNoSavedHours =
        "That member hasn't saved their available hours, so their free slots can't be listed by name. " +
        "They can save them with `/settings available-hours set` or under My settings on the web. " +
        "To see when a group is free, pick a role.";

    /// <summary>Runs the lookup. The request's ids are expected distinct and its numbers in range —
    /// the route checks both.</summary>
    /// <param name="request">The request body.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The answer, or the reason it could not be given.</returns>
    public async Task<FreeSlotsOutcome> GetAsync(FreeSlotsRequest request, CancellationToken cancellationToken)
    {
        // One reading of the clock decides which day is today, where today's first slot starts and
        // what the calendars are asked for.
        var now = clock.GetCurrentInstant();

        // Read-only on purpose: a lookup must not create the guild or profile rows it finds missing.
        var guildTimeZone = await db.Guilds.AsNoTracking()
            .Where(g => g.Id == request.GuildId)
            .Select(g => g.TimeZone)
            .FirstOrDefaultAsync(cancellationToken);
        var profileIds = request.UserIds.Append(request.RequesterId).Distinct().ToList();
        var profiles = await db.UserProfiles.AsNoTracking()
            .Where(p => profileIds.Contains(p.Id))
            .Select(p => new { p.Id, p.TimeZone, p.AvailableHoursJson })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var savedHours = profiles.ToDictionary(p => p.Key, p => WeeklyHours.Parse(p.Value.AvailableHoursJson));

        // By name, a member other than the one asking is listed only once they have saved their
        // own hours: saving them is how a member makes their free slots available by name, and
        // resetting them takes that back. Decided here, from the row just read and before any
        // calendar is asked, so nothing about a member who has not saved hours is looked up.
        if (request.ByName
            && request.UserIds.Any(id => id != request.RequesterId && savedHours.GetValueOrDefault(id) is null))
        {
            return new FreeSlotsOutcome(null, NamedMemberHasNoSavedHours);
        }

        var callerZone = MemberZone.Resolve(profiles.GetValueOrDefault(request.RequesterId)?.TimeZone, guildTimeZone);

        var today = now.InZone(callerZone).Date;
        var firstDay = today;
        if (!string.IsNullOrWhiteSpace(request.When))
        {
            if (!parser.TryResolveDay(request.When, callerZone, out firstDay, out var error, now))
            {
                return new FreeSlotsOutcome(null, error);
            }

            // Natural language reaches any year ("year 5000", "in 400000 weeks"). Nobody plans
            // that far, the calendars would be asked about it all the same, and day arithmetic
            // near the end of the calendar overflows.
            if (Period.DaysBetween(today, firstDay) > FreeSlotLimits.MaxDaysAhead)
            {
                return new FreeSlotsOutcome(
                    null, $"\"{request.When}\" is more than a year away; free slots can be listed up to a year ahead.");
            }
        }

        var query = new FreeSlotQuery(
            now, callerZone, firstDay, request.Days, Duration.FromMinutes(request.MinimumMinutes));

        // One availability check — so one provider query per linked member — covers every day.
        var (windowStart, windowEnd) = FreeSlotCalculator.QueryWindow(query);
        var results = await availability.CheckAsync(request.UserIds, windowStart, windowEnd, cancellationToken);

        var members = new List<FreeSlotMemberDto>(results.Count);
        var counted = new List<MemberAvailability>();
        foreach (var result in results)
        {
            var saved = savedHours.GetValueOrDefault(result.UserId);
            var state = StateOf(result.Status, saved is not null);
            members.Add(new FreeSlotMemberDto(result.UserId, state));
            if (state is not (FreeSlotMemberState.Counted or FreeSlotMemberState.DefaultHours or FreeSlotMemberState.HoursOnly))
            {
                continue;
            }

            // A member counted on hours alone has no calendar, so no busy blocks came back.
            counted.Add(new MemberAvailability(
                MemberZone.Resolve(profiles.GetValueOrDefault(result.UserId)?.TimeZone, guildTimeZone),
                saved ?? WeeklyHours.Default,
                [.. result.BusyBlocks.Select(block =>
                    (Instant.FromDateTimeOffset(block.StartUtc), Instant.FromDateTimeOffset(block.EndUtc)))]));
        }

        // Nobody counted means there is nothing to intersect: the answer carries no days at all
        // rather than a run of days that would read as "nothing free".
        IReadOnlyList<FreeSlotDayDto> days = counted.Count == 0
            ? []
            : [.. FreeSlotCalculator.Compute(query, counted).Select(ToDto)];

        return new FreeSlotsOutcome(
            new FreeSlotsResponse(
                callerZone.Id,
                firstDay.ToDateOnly(),
                firstDay.PlusDays(request.Days - 1).ToDateOnly(),
                counted.Count,
                days,
                members,
                request.ByName),
            null);
    }

    /// <summary>How a member enters the answer, from what the availability check found and whether
    /// they have saved hours. A calendar that was read counts the member, on their own hours or
    /// the default ones. With no calendar linked, saved hours count them on the hours alone, and
    /// without those there is nothing to go on. A linked calendar that could not be read leaves
    /// the member out whether or not they have hours: the calendar exists, and a list that
    /// ignored it would say more than is known.</summary>
    /// <param name="status">The member's availability status.</param>
    /// <param name="hasSavedHours">Whether the member has saved their own hours.</param>
    /// <returns>The member's state in the answer.</returns>
    private static FreeSlotMemberState StateOf(CalendarAvailabilityStatus status, bool hasSavedHours) => status switch
    {
        CalendarAvailabilityStatus.Free or CalendarAvailabilityStatus.Busy =>
            hasSavedHours ? FreeSlotMemberState.Counted : FreeSlotMemberState.DefaultHours,
        CalendarAvailabilityStatus.ReconnectRequired => FreeSlotMemberState.ReconnectRequired,
        CalendarAvailabilityStatus.Error => FreeSlotMemberState.Error,
        _ => hasSavedHours ? FreeSlotMemberState.HoursOnly : FreeSlotMemberState.NotCounted,
    };

    /// <summary>Projects one calculated day onto the wire.</summary>
    /// <param name="day">The calculated day.</param>
    /// <returns>The projected DTO.</returns>
    private static FreeSlotDayDto ToDto(FreeSlotDay day) => new(
        day.Date.ToDateOnly(),
        [.. day.Slots.Select(slot => new FreeSlotDto(slot.Start.ToDateTimeOffset(), slot.End.ToDateTimeOffset()))],
        day.OpenUntil?.ToDateTimeOffset());
}
