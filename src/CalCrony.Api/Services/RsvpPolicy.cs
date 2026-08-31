using System.Text.Json;
using System.Text.RegularExpressions;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>RSVP v1 domain rules: custom option validation and replacement, the attending-option
/// flag, RSVP-close cutoff parsing/resolution, and waitlist promotion. Pure decisions live here so
/// PutRsvp/UpdateEvent stay readable and the rules are directly testable.</summary>
public static partial class RsvpPolicy
{
    /// <summary>Discord renders RSVP buttons five per row across at most five rows minus other
    /// needs; ten options keeps embeds readable and buttons to two rows.</summary>
    public const int MaxOptions = 10;

    /// <summary>Column cap for option emotes and labels (mirrors the DbContext config).</summary>
    public const int MaxOptionTextLength = 64;

    /// <summary>The attending option — the single source of "who is going" semantics. Falls back
    /// to the lowest SortOrder so pre-flag rows still resolve; null only without options.</summary>
    /// <param name="options">The event's RSVP options.</param>
    /// <returns>The attending option, or null.</returns>
    public static RsvpOption? AttendingOption(IEnumerable<RsvpOption> options)
    {
        RsvpOption? flagged = null;
        RsvpOption? first = null;
        foreach (var option in options)
        {
            flagged ??= option.IsAttending ? option : null;
            if (first is null || option.SortOrder < first.SortOrder)
            {
                first = option;
            }
        }

        return flagged ?? first;
    }

    /// <summary>Seated (non-waitlisted) RSVPs on one option — what capacity counts.</summary>
    /// <param name="ev">The event (Rsvps loaded).</param>
    /// <param name="optionId">The RSVP option id.</param>
    /// <returns>How many users hold a seat on the option.</returns>
    public static int SeatedCount(Event ev, Guid optionId) =>
        ev.Rsvps.Count(r => r.OptionId == optionId && !r.Waitlisted);

    /// <summary>The effective RSVP cutoff: the absolute instant, or the relative one resolved
    /// against the CURRENT start (so time edits move it automatically); null = never closes.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The cutoff instant, or null.</returns>
    public static Instant? EffectiveClose(Event ev) =>
        ev.RsvpClosesAt
        ?? (ev.RsvpCloseMinutesBefore is int minutes
            ? ev.StartsAt.Minus(Duration.FromMinutes(minutes))
            : null);

    /// <summary>Whether RSVPs are closed as of <paramref name="now"/>.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>True once the effective cutoff has passed.</returns>
    public static bool IsClosed(Event ev, Instant now) => EffectiveClose(ev) is { } closes && closes <= now;

    /// <summary>Validates option specs and builds fresh option rows. Exactly one attending option
    /// comes out: the flagged one, or the first when none is flagged (two flags is an error).
    /// AttendeeLimit is the "cap the attending option" shorthand and conflicts with an explicit
    /// capacity on the attending spec.</summary>
    /// <param name="specs">The creator-supplied option specs (null = default set).</param>
    /// <param name="attendeeLimit">Optional capacity for the attending option.</param>
    /// <param name="error">The user-facing problem when validation fails.</param>
    /// <returns>The built option rows, or null when validation fails.</returns>
    public static List<RsvpOption>? TryBuildOptions(
        IReadOnlyList<RsvpOptionSpec>? specs, int? attendeeLimit, out string? error)
    {
        error = null;
        if (attendeeLimit is < 1)
        {
            error = "The attendee limit must be at least 1.";
            return null;
        }

        if (specs is null)
        {
            var defaults = Endpoints.EventEndpoints.DefaultRsvpOptions();
            defaults[0].Capacity = attendeeLimit;
            return defaults;
        }

        if (specs.Count is < 1 or > MaxOptions)
        {
            error = $"RSVP options must have between 1 and {MaxOptions} entries.";
            return null;
        }

        if (specs.Count(s => s.IsAttending) > 1)
        {
            error = "Only one RSVP option can be the attending option.";
            return null;
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Label) || string.IsNullOrWhiteSpace(spec.Emote))
            {
                error = "Every RSVP option needs an emoji and a label.";
                return null;
            }

            if (spec.Label.Length > MaxOptionTextLength || spec.Emote.Length > MaxOptionTextLength)
            {
                error = $"RSVP option emojis and labels must be at most {MaxOptionTextLength} characters.";
                return null;
            }

            if (!labels.Add(spec.Label.Trim()))
            {
                error = $"Duplicate RSVP option \"{spec.Label.Trim()}\".";
                return null;
            }

            if (spec.Capacity is < 1)
            {
                error = "RSVP option capacities must be at least 1.";
                return null;
            }
        }

        var attendingIndex = specs.ToList().FindIndex(s => s.IsAttending);
        if (attendingIndex < 0)
        {
            attendingIndex = 0;
        }

        if (attendeeLimit is not null && specs[attendingIndex].Capacity is not null)
        {
            error = "Set the attending option's capacity in the options or via the attendee limit, not both.";
            return null;
        }

        return [.. specs.Select((spec, index) => new RsvpOption
        {
            Id = Guid.NewGuid(),
            Emote = spec.Emote.Trim(),
            Label = spec.Label.Trim(),
            SortOrder = index,
            Capacity = index == attendingIndex ? spec.Capacity ?? attendeeLimit : spec.Capacity,
            IsAttending = index == attendingIndex,
        })];
    }

    /// <summary>Clones an option row set for a new event (fresh ids, same shape) — how custom
    /// options roll forward to the next series occurrence.</summary>
    /// <param name="options">The option rows to clone.</param>
    /// <returns>Fresh rows with new ids.</returns>
    public static List<RsvpOption> CloneOptions(IEnumerable<RsvpOption> options) =>
        [.. options.OrderBy(o => o.SortOrder).Select((o, index) => new RsvpOption
        {
            Id = Guid.NewGuid(),
            Emote = o.Emote,
            Label = o.Label,
            SortOrder = index,
            Capacity = o.Capacity,
            IsAttending = o.IsAttending,
        })];

    /// <summary>Relative cutoff text: "2h before", "90 min before start", "1 day" — a bare
    /// duration counts as before-start too, since a cutoff has nothing else to be relative to.</summary>
    [GeneratedRegex(
        @"^\s*(\d{1,5})\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)\s*(before(\s+start)?)?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RelativeClose();

    /// <summary>Parses RSVP-close text: relative ("2h before") becomes minutes-before-start (it
    /// then tracks time edits); anything else parses as a natural-language absolute instant.
    /// Exactly one of the two outputs is set on success.</summary>
    /// <param name="text">The submitted cutoff text.</param>
    /// <param name="zone">The zone absolute text parses in.</param>
    /// <param name="parser">The natural-language datetime parser.</param>
    /// <param name="minutesBefore">The relative cutoff, when the text was relative.</param>
    /// <param name="closesAt">The absolute cutoff, when the text was absolute.</param>
    /// <param name="error">The user-facing problem when parsing fails.</param>
    /// <returns>True when the text parsed.</returns>
    public static bool TryParseClose(
        string text, DateTimeZone zone, NaturalDateTimeParser parser,
        out int? minutesBefore, out Instant? closesAt, out string? error)
    {
        minutesBefore = null;
        closesAt = null;
        error = null;

        var relative = RelativeClose().Match(text);
        if (relative.Success)
        {
            var amount = int.Parse(relative.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var minutes = char.ToLowerInvariant(relative.Groups[2].Value[0]) switch
            {
                'h' => amount * 60,
                'd' => amount * 60 * 24,
                _ => amount,
            };
            if (minutes is < 1 or > FieldLimits.MaxMinutes)
            {
                error = $"The RSVP cutoff must be between 1 minute and {FieldLimits.MaxMinutes} minutes (4 weeks) before start.";
                return false;
            }

            minutesBefore = minutes;
            return true;
        }

        if (!parser.TryResolve(text, zone, out var instant, out var parseError))
        {
            error = $"Couldn't read the RSVP cutoff: {parseError}";
            return false;
        }

        closesAt = instant;
        return true;
    }

    /// <summary>Replaces an event's options from specs, matching by label (case-insensitive):
    /// matches update in place and keep their RSVPs, new labels append, and an option with RSVPs
    /// cannot be removed. When the attending flag lands on a different option, the old option's
    /// waitlist is seated (waitlisting only means something on the attending option). Mutates the
    /// event only on success.</summary>
    /// <param name="db">The database context (appended rows need an explicit Add — with a
    /// client-set Guid key, graph fixup alone would issue an UPDATE instead of INSERT).</param>
    /// <param name="ev">The event (Options and Rsvps loaded).</param>
    /// <param name="specs">The replacement option specs.</param>
    /// <param name="attendeeLimit">Optional capacity for the attending option.</param>
    /// <param name="conflict">True when the error should be a 409 (option in use), not a 400.</param>
    /// <param name="error">The user-facing problem when the edit is rejected.</param>
    /// <returns>True when the replacement was applied.</returns>
    public static bool TryApplyOptionEdit(
        CalCronyDbContext db, Event ev, IReadOnlyList<RsvpOptionSpec> specs, int? attendeeLimit,
        out bool conflict, out string? error)
    {
        conflict = false;
        var built = TryBuildOptions(specs, attendeeLimit, out error);
        if (built is null)
        {
            return false;
        }

        var byLabel = built.ToDictionary(o => o.Label, StringComparer.OrdinalIgnoreCase);
        var removedInUse = ev.Options.FirstOrDefault(existing =>
            !byLabel.ContainsKey(existing.Label) && ev.Rsvps.Any(r => r.OptionId == existing.Id));
        if (removedInUse is not null)
        {
            conflict = true;
            error = $"\"{removedInUse.Label}\" has RSVPs — keep it in the list (RSVPs follow the label).";
            return false;
        }

        var oldAttendingId = AttendingOption(ev.Options)?.Id;
        ev.Options.RemoveAll(existing => !byLabel.ContainsKey(existing.Label));
        foreach (var existing in ev.Options)
        {
            var replacement = byLabel[existing.Label];
            existing.Emote = replacement.Emote;
            existing.Label = replacement.Label; // may change case
            existing.SortOrder = replacement.SortOrder;
            existing.Capacity = replacement.Capacity;
            existing.IsAttending = replacement.IsAttending;
            byLabel.Remove(existing.Label);
        }

        foreach (var appended in byLabel.Values)
        {
            appended.EventId = ev.Id;
            db.RsvpOptions.Add(appended); // fixup then places it into ev.Options
        }

        // Attending moved: the old option's queue has nothing to wait for anymore — seat it.
        // (No promotion pings; these users keep the choice they already made.)
        var newAttendingId = AttendingOption(ev.Options)?.Id;
        if (oldAttendingId is { } oldId && newAttendingId != oldAttendingId)
        {
            foreach (var rsvp in ev.Rsvps.Where(r => r.OptionId == oldId && r.Waitlisted))
            {
                rsvp.Waitlisted = false;
            }
        }

        return true;
    }

    /// <summary>Promotes waitlisted users into freed attending seats, earliest first, until the
    /// capacity (or the queue) runs out. Seats are taken in the caller's transaction; role grants,
    /// thread adds, and the promotion ping ride the outbox so they survive a crash. Pings and
    /// role/thread effects only fire on live events — a seat freed on an ended event moves
    /// quietly.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event (Options and Rsvps loaded).</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many users were promoted.</returns>
    public static async Task<int> PromoteAsync(
        CalCronyDbContext db, Event ev, IClock clock, CancellationToken cancellationToken)
    {
        if (AttendingOption(ev.Options) is not { } attending)
        {
            return 0;
        }

        var headroom = attending.Capacity is int capacity
            ? capacity - SeatedCount(ev, attending.Id)
            : int.MaxValue;
        if (headroom <= 0)
        {
            return 0;
        }

        var isLive = ev.Status is EventStatus.Scheduled or EventStatus.Started;
        var now = clock.GetCurrentInstant();
        var promoted = 0;
        foreach (var rsvp in ev.Rsvps
                     .Where(r => r.OptionId == attending.Id && r.Waitlisted)
                     .OrderBy(r => r.CreatedAt)
                     .Take(headroom))
        {
            rsvp.Waitlisted = false;
            promoted++;
            if (!isLive)
            {
                continue;
            }

            if (AttendeeRoleSync.IsRoleActive(ev))
            {
                await AttendeeRoleSync.EnqueueRoleChangeAsync(
                    db, ev, DeliveryType.GrantAttendeeRole, rsvp.UserId, clock, cancellationToken);
            }

            if (EventThreadSync.IsThreadActive(ev))
            {
                await EventThreadSync.EnqueueMemberAddAsync(db, ev, rsvp.UserId, clock, cancellationToken);
            }

            db.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(),
                Type = DeliveryType.WaitlistPromotion,
                ChannelId = ev.ChannelId,
                PayloadJson = JsonSerializer.Serialize(new WaitlistPromotionPayload(
                    ev.Id, rsvp.UserId, ev.Title, ev.StartsAt.ToUnixTimeSeconds(),
                    attending.Emote, attending.Label)),
                DueAt = now,
                Status = DeliveryStatus.Pending,
                CreatedAt = now,
            });
        }

        return promoted;
    }
}
