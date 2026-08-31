using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Event CRUD, RSVPs, datetime tooling, and the shared guild/event guards other endpoint groups reuse.</summary>
public static class EventEndpoints
{
    /// <summary>Maps event, RSVP, and datetime-tool routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        // Phase B: web (JWT) callers get bot-parity mutations — member to create, creator or
        // ManageGuild to edit/delete. SetMessage stays bot-only (only the bot knows message ids).
        app.MapPost("/guilds/{guildId:long}/events", CreateEvent);
        app.MapGet("/guilds/{guildId:long}/events", ListEvents);
        app.MapGet("/events/{id:guid}", GetEvent);
        app.MapPatch("/events/{id:guid}", UpdateEvent);
        app.MapDelete("/events/{id:guid}", DeleteEvent);
        app.MapPut("/events/{id:guid}/message", SetMessage).RequireAuthorization("BotOnly");
        app.MapPut("/events/{id:guid}/native-event", SetNativeEvent).RequireAuthorization("BotOnly");
        app.MapPut("/events/{id:guid}/thread", SetThread).RequireAuthorization("BotOnly");
        app.MapPut("/events/{id:guid}/rsvps/{userId:long}", PutRsvp);
        app.MapDelete("/events/{id:guid}/rsvps/{userId:long}", DeleteRsvp);
        app.MapPost("/tools/parse-datetime", ParseDateTime);
        app.MapGet("/tools/timezones", ListTimeZones);
    }

    /// <summary>Canonical IANA zones (city zones + UTC) with their current UTC offset, for
    /// timezone pickers — nobody should have to type an IANA id from memory.</summary>
    /// <param name="clock">The time source.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static IResult ListTimeZones(IClock clock)
    {
        var now = clock.GetCurrentInstant();
        var source = NodaTime.TimeZones.TzdbDateTimeZoneSource.Default;
        // "UTC" itself is an alias of Etc/UTC in the canonical map, so it's prepended explicitly.
        var options = source.CanonicalIdMap
            .Where(pair => pair.Key == pair.Value) // canonical only — no aliases
            .Select(pair => pair.Key)
            .Where(id => id.Contains('/') && !id.StartsWith("Etc/", StringComparison.Ordinal))
            .Prepend("UTC")
            .Select(id =>
            {
                var minutes = DateTimeZoneProviders.Tzdb[id].GetUtcOffset(now).Seconds / 60;
                var formatted = minutes == 0
                    ? "UTC±00:00"
                    : $"UTC{(minutes < 0 ? "-" : "+")}{Math.Abs(minutes) / 60:00}:{Math.Abs(minutes) % 60:00}";
                return new TimeZoneOptionDto(id, $"{id} — {formatted}");
            })
            .OrderBy(o => o.Id, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(options);
    }

    /// <summary>Mutation guard for JWT callers: event's guild member AND (creator or manager).
    /// Bot passes. Non-members get 404 (same anti-probing rule as reads).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="ev">The event.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    internal static Task<IResult?> GuardEventMutateAsync(
        HttpContext context, GuildAccessService access, Event ev, CancellationToken cancellationToken) =>
        GuardMutateAsync(context, access, ev.GuildId, ev.CreatorId,
            "Only the event creator or a server manager can change this event.", cancellationToken);

    /// <summary>Shared creator-or-manager mutate guard (events + series).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="creatorId">The creating user's Discord id.</param>
    /// <param name="forbiddenMessage">The 403 body when a plain member is not the creator.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    internal static async Task<IResult?> GuardMutateAsync(
        HttpContext context, GuildAccessService access, long guildId, long creatorId,
        string forbiddenMessage, CancellationToken cancellationToken)
    {
        if (context.User.IsBot())
        {
            return null;
        }

        var userId = context.User.WebUserId();
        if (userId is null)
        {
            return Results.NotFound();
        }

        return await access.CheckAsync(userId.Value, guildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.Manager => null,
            GuildAccess.Member when creatorId == userId.Value => null,
            GuildAccess.Member => Results.Json(
                new ErrorResponse(forbiddenMessage),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

    /// <summary>The standard RSVP option set events start with when no custom options are given
    /// (also used by poll conversion). Going carries the attending flag.</summary>
    /// <returns>Fresh Going/Not going/Maybe option rows.</returns>
    internal static List<RsvpOption> DefaultRsvpOptions() =>
    [
        new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", SortOrder = 0, IsAttending = true },
        new RsvpOption { Id = Guid.NewGuid(), Emote = "❌", Label = "Not going", SortOrder = 1 },
        new RsvpOption { Id = Guid.NewGuid(), Emote = "🤔", Label = "Maybe", SortOrder = 2 },
    ];

    /// <summary>Guild-read guard for web callers: bot passes, members pass, others get 403/stale.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    internal static async Task<IResult?> GuardGuildReadAsync(
        HttpContext context, GuildAccessService access, long guildId, CancellationToken cancellationToken)
    {
        if (context.User.IsBot())
        {
            return null;
        }

        var userId = context.User.WebUserId();
        if (userId is null)
        {
            return GuildAccessService.Forbidden();
        }

        return await access.CheckAsync(userId.Value, guildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.None => GuildAccessService.Forbidden(),
            _ => null,
        };
    }

    /// <summary>Event-read guard: like GuardGuildReadAsync but non-members get 404 so event ids
    /// can't be probed for existence.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="ev">The event.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    internal static async Task<IResult?> GuardEventReadAsync(
        HttpContext context, GuildAccessService access, Event ev, CancellationToken cancellationToken)
    {
        if (context.User.IsBot())
        {
            return null;
        }

        var userId = context.User.WebUserId();
        if (userId is null)
        {
            return Results.NotFound();
        }

        return await access.CheckAsync(userId.Value, ev.GuildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.Member or GuildAccess.Manager => null,
            _ => Results.NotFound(),
        };
    }

    /// <summary>Enqueue a Discord-embed re-render for web-initiated changes. Bot callers skip
    /// this (the bot edits the message itself); coalesces with an identical pending sync.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    internal static async Task EnqueueEmbedSyncAsync(
        HttpContext context, CalCronyDbContext db, Event ev, IClock clock, CancellationToken cancellationToken)
    {
        if (context.User.IsBot() || ev.MessageId is null)
        {
            return;
        }

        var payloadJson = JsonSerializer.Serialize(new SyncEventMessagePayload(ev.Id));
        var alreadyQueued = await db.Deliveries.AnyAsync(
            d => d.Type == DeliveryType.SyncEventMessage
                 && d.Status == DeliveryStatus.Pending
                 && d.PayloadJson == payloadJson,
            cancellationToken);
        if (alreadyQueued)
        {
            return;
        }

        var now = clock.GetCurrentInstant();
        db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.SyncEventMessage,
            ChannelId = ev.ChannelId,
            PayloadJson = payloadJson,
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
    }

    /// <summary>Creates an event (and its series when a recurrence rule is supplied); web callers get identity and channel forced server-side.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="parser">The natural-language datetime parser.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> CreateEvent(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CreateEventRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await GetOrCreateGuildAsync(db, guildId, cancellationToken);

        // Web callers can't spoof identity or pick channels: creator is always the JWT subject,
        // and the embed goes to the guild's default channel — creation is blocked without one
        // (a channel-less event would be invisible in Discord with no RSVP buttons).
        var isBot = context.User.IsBot();
        var creatorId = isBot ? request.CreatorId : context.User.WebUserId()!.Value;
        long channelId;
        if (isBot)
        {
            channelId = request.ChannelId;
        }
        else if (guild.DefaultChannelId is long defaultChannel)
        {
            channelId = defaultChannel;
        }
        else
        {
            return Results.BadRequest(new ErrorResponse(
                "This server has no default events channel yet — a manager must run /settings default-channel in Discord."));
        }

        var zone = await ResolveZoneAsync(db, creatorId, guild, cancellationToken);
        if (!parser.TryResolve(request.WhenText, zone, out var startsAt, out var error))
        {
            return Results.BadRequest(new ErrorResponse(error!));
        }

        // Template application: explicit request fields win, the template fills gaps, and its
        // notification specs are always copied. NoRecurrence explicitly suppresses a template
        // rule (unset means "take it" when no explicit rule was sent).
        EventTemplate? template = null;
        if (request.TemplateId is { } templateId)
        {
            template = await db.EventTemplates
                .Include(t => t.Notifications)
                .FirstOrDefaultAsync(t => t.Id == templateId && t.GuildId == guildId, cancellationToken);
            if (template is null)
            {
                return Results.BadRequest(new ErrorResponse("That template no longer exists."));
            }
        }

        if (request.NoRecurrence && request.Recurrence is not null)
        {
            return Results.BadRequest(new ErrorResponse("Choose a repeat rule or no repeat, not both."));
        }

        var title = string.IsNullOrWhiteSpace(request.Title) && template is not null
            ? template.Title
            : request.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return Results.BadRequest(new ErrorResponse("The title is required."));
        }

        // Friendly 400s instead of Postgres truncation 500s; template TEXT values are already
        // capped at save time (the duration is range-checked below on the effective value,
        // because templates saved from pre-validation events could carry an out-of-range one).
        if ((Validation.TooLong("title", request.Title, FieldLimits.EventTitle)
            ?? Validation.TooLong("description", request.Description, FieldLimits.EventDescription)
            ?? Validation.TooLong("location", request.Location, FieldLimits.EventLocation)
            ?? Validation.TooLong("image URL", request.ImageUrl, FieldLimits.EventImageUrl)) is { } invalid)
        {
            return invalid;
        }

        var description = request.Description ?? template?.Description;
        var durationMinutes = request.DurationMinutes ?? template?.DurationMinutes;
        var location = request.Location ?? template?.Location;
        var imageUrl = request.ImageUrl ?? template?.ImageUrl;

        // Range-check the EFFECTIVE duration so a template gap-fill can't smuggle one in.
        if (Validation.BadDuration(durationMinutes) is { } invalidDuration)
        {
            return invalidDuration;
        }
        // Role selection is bot-only (the web can't enumerate Discord roles); templates never carry one.
        var attendeeRoleId = isBot ? request.AttendeeRoleId : null;
        // Threads are a plain yes/no, so WantsThread is honored for BOTH caller types — the bot
        // opens the thread when it posts the embed.
        var wantsThread = request.WantsThread;

        // Custom RSVP options + attendee limit (defaults apply when unspecified) and the optional
        // RSVP cutoff — relative text becomes minutes-before (tracks time edits), absolute text
        // parses in the same zone as the start time.
        var options = RsvpPolicy.TryBuildOptions(request.RsvpOptions, request.AttendeeLimit, out var optionsError);
        if (options is null)
        {
            return Results.BadRequest(new ErrorResponse(optionsError!));
        }

        int? rsvpCloseMinutesBefore = null;
        Instant? rsvpClosesAt = null;
        if (request.RsvpCloseText is not null)
        {
            if (!RsvpPolicy.TryParseClose(
                    request.RsvpCloseText, zone, parser, out rsvpCloseMinutesBefore, out rsvpClosesAt, out var closeError))
            {
                return Results.BadRequest(new ErrorResponse(closeError!));
            }

            // A relative cutoff that lands before "now" would create the event already closed
            // (the parser guarantees a future instant for absolute text; relative needs the
            // start applied) — and an absolute cutoff at/after start would never close early.
            if (rsvpCloseMinutesBefore is int closeMinutes
                && startsAt.Minus(Duration.FromMinutes(closeMinutes)) <= clock.GetCurrentInstant())
            {
                return Results.BadRequest(new ErrorResponse(
                    "That RSVP cutoff is already in the past — the event starts too soon."));
            }

            if (rsvpClosesAt is { } absoluteClose && absoluteClose >= startsAt)
            {
                return Results.BadRequest(new ErrorResponse("The RSVP cutoff must be before the event starts."));
            }
        }
        var recurrence = request.Recurrence
            ?? (request.NoRecurrence || template?.RecurrenceUnit is null
                ? null
                : new RecurrenceRuleDto(
                    template.RecurrenceUnit.Value,
                    template.RecurrenceInterval!.Value,
                    template.RecurrenceMonthlyMode!.Value));

        if (recurrence is null && (request.RepeatUntilText is not null || request.RepeatCount is not null))
        {
            return Results.BadRequest(new ErrorResponse("Set a repeat rule to use the repeat end options."));
        }

        var now = clock.GetCurrentInstant();
        EventSeries? series = null;
        if (recurrence is { } rule)
        {
            if (rule.Interval is < 1 or > 12)
            {
                return Results.BadRequest(new ErrorResponse("Repeat interval must be between 1 and 12."));
            }

            if (request.RepeatUntilText is not null && request.RepeatCount is not null)
            {
                return Results.BadRequest(new ErrorResponse("Choose either an end date or a number of times, not both."));
            }

            if (request.RepeatCount is < 2 or > 500)
            {
                return Results.BadRequest(new ErrorResponse("Repeat count must be between 2 and 500."));
            }

            var firstLocal = startsAt.InZone(zone).LocalDateTime;
            LocalDate? untilDate = null;
            if (request.RepeatUntilText is not null)
            {
                if (!parser.TryResolve(request.RepeatUntilText, zone, out var untilInstant, out var untilError))
                {
                    return Results.BadRequest(new ErrorResponse(untilError!));
                }

                untilDate = untilInstant.InZone(zone).Date;
                if (untilDate < firstLocal.Date)
                {
                    return Results.BadRequest(new ErrorResponse("The repeat end date is before the first occurrence."));
                }
            }

            series = new EventSeries
            {
                Id = Guid.NewGuid(),
                GuildId = guildId,
                CreatorId = creatorId,
                Unit = rule.Unit,
                Interval = rule.Interval,
                MonthlyMode = rule.MonthlyMode,
                AnchorDate = firstLocal.Date,
                StartTime = firstLocal.TimeOfDay,
                TimeZone = zone.Id,
                UntilDate = untilDate,
                MaxOccurrences = request.RepeatCount,
                CurrentOccurrenceDate = firstLocal.Date,
                OccurrenceCount = 1,
                Title = title,
                Description = description,
                DurationMinutes = durationMinutes,
                ChannelId = channelId,
                Location = location,
                ImageUrl = imageUrl,
                AttendeeRoleId = attendeeRoleId,
                WantsThread = wantsThread,
                // Only the relative cutoff is a template field — a fixed instant makes no sense
                // across a schedule. The option set (with any merged attendee limit) is captured
                // as a template too, so spawned occurrences start from it.
                RsvpCloseMinutesBefore = rsvpCloseMinutesBefore,
                RsvpOptionsJson = request.RsvpOptions is null && request.AttendeeLimit is null
                    ? null
                    : RsvpPolicy.SerializeSpecs(options),
                CreatedAt = now,
            };
            db.EventSeries.Add(series);
        }

        var ev = new Event
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatorId = creatorId,
            Title = title,
            Description = description,
            StartsAt = startsAt,
            TimeZone = zone.Id,
            DurationMinutes = durationMinutes,
            ChannelId = channelId,
            Location = location,
            ImageUrl = imageUrl,
            AttendeeRoleId = attendeeRoleId,
            WantsThread = wantsThread,
            RsvpCloseMinutesBefore = rsvpCloseMinutesBefore,
            RsvpClosesAt = rsvpClosesAt,
            Status = EventStatus.Scheduled,
            SeriesId = series?.Id,
            Series = series,
            CreatedAt = now,
            Options = options,
        };
        db.Events.Add(ev);

        // Template notification specs go onto the event and — when a series was created — onto
        // its spec list with lineage, exactly like hand-added Series-scope notifications, so
        // future occurrences inherit them and Series-scope deletes retire them.
        if (template is not null)
        {
            foreach (var spec in template.Notifications)
            {
                SeriesNotification? seriesSpec = null;
                if (series is not null)
                {
                    seriesSpec = new SeriesNotification
                    {
                        Id = Guid.NewGuid(),
                        SeriesId = series.Id,
                        MinutesBefore = spec.MinutesBefore,
                        Message = spec.Message,
                        Mentions = spec.Mentions,
                        ChannelId = spec.ChannelId,
                    };
                    db.SeriesNotifications.Add(seriesSpec);
                }

                db.EventNotifications.Add(new EventNotification
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    MinutesBefore = spec.MinutesBefore,
                    Message = spec.Message,
                    Mentions = spec.Mentions,
                    ChannelId = spec.ChannelId,
                    SeriesNotificationId = seriesSpec?.Id,
                });
            }
        }

        if (!isBot)
        {
            // The bot posts the embed itself on /create; web creates hand that job to the outbox.
            db.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(),
                Type = DeliveryType.PostEventMessage,
                ChannelId = channelId,
                PayloadJson = JsonSerializer.Serialize(new PostEventMessagePayload(ev.Id)),
                DueAt = now,
                Status = DeliveryStatus.Pending,
                CreatedAt = now,
            });
        }

        // Live lists rewrite on every event change, both caller types — the outbox is the only
        // path that knows which channels host one.
        await LiveListSync.EnqueueSyncForGuildAsync(db, guildId, now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/events/{ev.Id}", await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Lists a guild's events ascending; includePast widens the window to the last 30 days.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="channelId">The Discord channel id.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="includePast">When true, widens the window to the last 30 days.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ListEvents(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken,
        long? channelId = null,
        int limit = 10,
        bool includePast = false)
    {
        if (await GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        limit = Math.Clamp(limit, 1, 25);
        var query = db.Events
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .Include(e => e.Series)
            .Where(e => e.GuildId == guildId && e.Status != EventStatus.Cancelled);

        // includePast means the last 30 days (the ICS feed's window), not all history — otherwise
        // ancient events crowd upcoming ones out of the ascending 25-cap, which broke the bot's
        // name autocomplete and left /series edit unable to see recent occurrences.
        var now = clock.GetCurrentInstant();
        var startFloor = includePast ? now.Minus(Duration.FromDays(30)) : now;
        query = query.Where(e => e.StartsAt >= startFloor);

        if (channelId is not null)
        {
            query = query.Where(e => e.ChannelId == channelId);
        }

        var events = await query.OrderBy(e => e.StartsAt).Take(limit).ToListAsync(cancellationToken);
        return Results.Ok(events.Select(e => e.ToDto()));
    }

    /// <summary>Fetches one event (non-members get 404, not 403 — ids must not be probeable).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The event id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetEvent(
        HttpContext context, GuildAccessService access, Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Maps a single event to its DTO with the channel-name snapshot attached. Every
    /// single-event response carries it (issue #80) — the web page overwrites its in-memory
    /// event with mutation responses (RSVP, edit), so a GET-only snapshot would make the
    /// channel chip vanish after user actions. List views deliberately skip the lookup.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="ev">The event to map.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The projected DTO.</returns>
    private static async Task<EventDto> ToDtoWithChannelAsync(
        CalCronyDbContext db, Event ev, CancellationToken cancellationToken) =>
        ev.ToDto(await db.Channels
            .Where(c => c.Id == ev.ChannelId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken));

    /// <summary>Applies a partial update; live series occurrences require a Scope (occurrence-only vs template + re-anchor).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The event id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="parser">The natural-language datetime parser.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> UpdateEvent(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        UpdateEventRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // Option/capacity edits promote off the waitlist, so they serialize behind the same
        // event-row lock PUT/DELETE RSVP take (before the aggregate loads) — two stale
        // aggregates must not both promote the same queue head or double-seat a freed spot.
        // Early returns roll back via the transaction's dispose.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockEventRowAsync(db, id, cancellationToken);

        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventMutateAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        // Ask-per-edit: a live occurrence of an active series must say whether the change is
        // one-off (diverges; the next spawn reverts to the template) or series-wide.
        var series = ev.Series is { Ended: false } ? ev.Series : null;
        var isLive = ev.Status is EventStatus.Scheduled or EventStatus.Started;
        if (series is not null && isLive && request.Scope is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "This event repeats — specify whether to change this occurrence or the whole series."));
        }

        if (series is not null && request.Scope == EditScope.Series && !isLive)
        {
            return Results.Conflict(new ErrorResponse("Only the upcoming occurrence can edit the whole series."));
        }

        var applyToSeries = series is not null && isLive && request.Scope == EditScope.Series;

        // Friendly 400s instead of Postgres truncation 500s. One check covers both the event
        // write and the applyToSeries template write — they use the same request fields.
        if ((Validation.TooLong("title", request.Title, FieldLimits.EventTitle)
            ?? Validation.TooLong("description", request.Description, FieldLimits.EventDescription)
            ?? Validation.TooLong("location", request.Location, FieldLimits.EventLocation)
            ?? Validation.TooLong("image URL", request.ImageUrl, FieldLimits.EventImageUrl)
            ?? Validation.BadDuration(request.DurationMinutes)) is { } invalid)
        {
            return invalid;
        }

        if (request.AttendeeRoleId is not null && request.ClearAttendeeRole)
        {
            return Results.BadRequest(new ErrorResponse("Choose an attendee role or clear it, not both."));
        }

        if (request.AttendeeLimit is not null && request.ClearAttendeeLimit)
        {
            return Results.BadRequest(new ErrorResponse("Choose an attendee limit or clear it, not both."));
        }

        if (request.RsvpCloseText is not null && request.ClearRsvpClose)
        {
            return Results.BadRequest(new ErrorResponse("Choose an RSVP cutoff or clear it, not both."));
        }

        if (request.AttendeeLimit is < 1)
        {
            return Results.BadRequest(new ErrorResponse("The attendee limit must be at least 1."));
        }

        if (!context.User.IsBot() && request.AttendeeRoleId is not null)
        {
            // The web can't enumerate Discord roles, so selection is bot-only; clearing is fine.
            return Results.BadRequest(new ErrorResponse("Attendee roles are picked in Discord — use /create or /edit."));
        }

        var oldRole = ev.AttendeeRoleId;
        var newRole = request.ClearAttendeeRole ? null : request.AttendeeRoleId ?? oldRole;
        var staysLive = (request.Status ?? ev.Status) is EventStatus.Scheduled or EventStatus.Started;

        if (request.WhenText is not null)
        {
            // Series-scope time changes parse in the series zone and re-anchor the schedule;
            // occurrence-scope ones parse in the event zone and leave the schedule untouched.
            var zone = Mapping.FindZone(applyToSeries ? series!.TimeZone : ev.TimeZone) ?? DateTimeZone.Utc;
            if (!parser.TryResolve(request.WhenText, zone, out var startsAt, out var error))
            {
                return Results.BadRequest(new ErrorResponse(error!));
            }

            ev.StartsAt = startsAt;
            // A relative cutoff moved with the start — let the closed-state sync re-arm.
            ev.RsvpCloseSynced = false;
            if (applyToSeries)
            {
                var local = startsAt.InZone(zone).LocalDateTime;
                series!.AnchorDate = local.Date;
                series.StartTime = local.TimeOfDay;
                series.CurrentOccurrenceDate = local.Date;
            }
        }

        ev.Title = request.Title ?? ev.Title;
        ev.Description = request.Description ?? ev.Description;
        ev.DurationMinutes = request.DurationMinutes ?? ev.DurationMinutes;
        ev.Location = request.Location ?? ev.Location;
        ev.ImageUrl = request.ImageUrl ?? ev.ImageUrl;
        ev.Status = request.Status ?? ev.Status; // occurrence state — never a template field

        if (applyToSeries)
        {
            series!.Title = request.Title ?? series.Title;
            series.Description = request.Description ?? series.Description;
            series.DurationMinutes = request.DurationMinutes ?? series.DurationMinutes;
            series.Location = request.Location ?? series.Location;
            series.ImageUrl = request.ImageUrl ?? series.ImageUrl;
        }

        ev.AttendeeRoleId = newRole;
        if (applyToSeries && (request.AttendeeRoleId is not null || request.ClearAttendeeRole))
        {
            series!.AttendeeRoleId = newRole;
        }

        // RSVP v1 edits — options, attendee limit, cutoff — apply BEFORE the role fan-outs so
        // one unified transition (old role/option → new role/option) can be computed afterwards.
        var oldAttendingId = RsvpPolicy.AttendingOption(ev.Options)?.Id;
        if (request.RsvpOptions is not null)
        {
            if (!RsvpPolicy.TryApplyOptionEdit(
                    db, ev, request.RsvpOptions, request.AttendeeLimit, out var optionsConflict, out var optionsError))
            {
                return optionsConflict
                    ? Results.Conflict(new ErrorResponse(optionsError!))
                    : Results.BadRequest(new ErrorResponse(optionsError!));
            }
        }
        else if ((request.AttendeeLimit is not null || request.ClearAttendeeLimit)
                 && RsvpPolicy.AttendingOption(ev.Options) is { } cappedOption)
        {
            // Limit-only shorthand: set or clear the attending option's capacity in place.
            cappedOption.Capacity = request.ClearAttendeeLimit ? null : request.AttendeeLimit;
        }

        if (applyToSeries && request.RsvpOptions is not null)
        {
            // Series scope with an explicit option set: it becomes the template future
            // occurrences spawn from; Occurrence scope leaves the template alone (the next
            // spawn reverts to it).
            series!.RsvpOptionsJson = RsvpPolicy.SerializeSpecs(ev.Options);
        }
        else if (applyToSeries && (request.AttendeeLimit is not null || request.ClearAttendeeLimit))
        {
            // Limit-only: cap the TEMPLATE's attending option rather than copying this
            // occurrence's rows — an earlier occurrence-scoped option divergence must not ride
            // a limit change into every future occurrence.
            series!.RsvpOptionsJson = RsvpPolicy.WithAttendingCapacity(
                series.RsvpOptionsJson, request.ClearAttendeeLimit ? null : request.AttendeeLimit);
        }

        if (request.ClearRsvpClose)
        {
            ev.RsvpCloseMinutesBefore = null;
            ev.RsvpClosesAt = null;
            ev.RsvpCloseSynced = false;
            if (applyToSeries)
            {
                series!.RsvpCloseMinutesBefore = null;
            }
        }
        else if (request.RsvpCloseText is not null)
        {
            var closeZone = Mapping.FindZone(ev.TimeZone) ?? DateTimeZone.Utc;
            if (!RsvpPolicy.TryParseClose(
                    request.RsvpCloseText, closeZone, parser, out var closeMinutes, out var closesAt, out var closeError))
            {
                return Results.BadRequest(new ErrorResponse(closeError!));
            }

            ev.RsvpCloseMinutesBefore = closeMinutes;
            ev.RsvpClosesAt = closesAt;
            ev.RsvpCloseSynced = false;
            if (applyToSeries && closeMinutes is not null)
            {
                // Only the relative form is a template field — an absolute instant is pinned to
                // THIS occurrence, so a series-scoped absolute edit leaves the template cutoff
                // alone (clearing it stays the explicit ClearRsvpClose operation).
                series!.RsvpCloseMinutesBefore = closeMinutes;
            }
        }

        // The cutoff must stay coherent with the (possibly just-edited) start. The relative form
        // tracks the start, so it can only go wrong by resolving into the past; the absolute form
        // is future-guaranteed at parse, so it can only go wrong by landing at/after start. A
        // stale absolute cutoff on a start-only postpone stays legal — RSVPs simply stay closed.
        if (staysLive
            && (request.WhenText is not null || request.RsvpCloseText is not null)
            && !request.ClearRsvpClose
            && RsvpPolicy.EffectiveClose(ev) is { } editedClose)
        {
            if (ev.RsvpCloseMinutesBefore is not null && editedClose <= clock.GetCurrentInstant())
            {
                return Results.BadRequest(new ErrorResponse(
                    "That RSVP cutoff is already in the past — the event starts too soon."));
            }

            if (editedClose >= ev.StartsAt)
            {
                return Results.BadRequest(new ErrorResponse("The RSVP cutoff must be before the event starts."));
            }
        }

        var newAttendingId = RsvpPolicy.AttendingOption(ev.Options)?.Id;
        var roleSyncNow = clock.GetCurrentInstant();
        if (isLive && !staysLive)
        {
            // Cancel/end via PATCH — the previously side-effect-free path. Revoke everything
            // under the OLD role from the PRE-EDIT attending option (its members are the ones
            // holding it); a same-request role change never grants on a dying event.
            if (oldRole is { } endedRole && oldAttendingId is { } endedAttending)
            {
                AttendeeRoleSync.EnqueueRoleFanOutForOption(
                    db, ev, DeliveryType.RevokeAttendeeRole, endedRole, endedAttending, roleSyncNow);
            }

            if (ev.ThreadId is not null)
            {
                EventThreadSync.EnqueueArchive(db, ev, roleSyncNow);
            }
        }
        else if (isLive && (newRole != oldRole || newAttendingId != oldAttendingId))
        {
            // One unified re-sync for role and/or attending-option changes: revoke the old role
            // from the old attending members (who actually hold it) and grant the new role to
            // the new attending members — never contradictory rows for the same user at the same
            // due time. The old option's just-seated waitlist gets harmless no-op revokes.
            if (oldRole is { } previousRole && oldAttendingId is { } previousAttending)
            {
                AttendeeRoleSync.EnqueueRoleFanOutForOption(
                    db, ev, DeliveryType.RevokeAttendeeRole, previousRole, previousAttending, roleSyncNow);
            }

            if (newRole is { } grantedRole && newAttendingId is { } currentAttending)
            {
                AttendeeRoleSync.EnqueueRoleFanOutForOption(
                    db, ev, DeliveryType.GrantAttendeeRole, grantedRole, currentAttending, roleSyncNow);
            }

            // The RSVP and promotion paths add thread members one at a time as users land on the
            // CURRENT attending option — so when the flag moves, users already seated on the new
            // option are backfilled here (add-only; the enqueue dedups repeats).
            if (newAttendingId != oldAttendingId
                && newAttendingId is { } seatedAttending
                && EventThreadSync.IsThreadActive(ev))
            {
                foreach (var rsvp in ev.Rsvps.Where(r => r.OptionId == seatedAttending && !r.Waitlisted))
                {
                    await EventThreadSync.EnqueueMemberAddAsync(db, ev, rsvp.UserId, clock, cancellationToken);
                }
            }
        }

        // Raised/cleared capacity frees seats — promote in queue order (no-op when nothing waits).
        await RsvpPolicy.PromoteAsync(db, ev, clock, cancellationToken);

        await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
        await LiveListSync.EnqueueSyncForGuildAsync(db, ev.GuildId, roleSyncNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Deletes an event; deleting a live series occurrence stops its series (skip is the just-this-one verb).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The event id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> DeleteEvent(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventMutateAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        // Deleting the live occurrence of a series means "make it gone" — stop the series too
        // (skip is the explicit just-this-one verb). Past occurrences delete as plain history.
        if (ev.Series is { Ended: false } series && ev.Status is EventStatus.Scheduled or EventStatus.Started)
        {
            series.Ended = true;
        }

        // Attendee-role revokes must be captured before the delete cascades the RSVP rows away —
        // and unlike the embed cleanup, they apply to BOTH caller types (roles always ride the outbox).
        if (AttendeeRoleSync.IsRoleActive(ev))
        {
            AttendeeRoleSync.EnqueueRoleFanOut(
                db, ev, DeliveryType.RevokeAttendeeRole, ev.AttendeeRoleId!.Value, clock.GetCurrentInstant());
        }

        // Deleting the embed message does NOT delete its attached thread (it survives orphaned),
        // so the discussion thread is archived explicitly — both caller types, payload survives
        // the event row.
        if (EventThreadSync.IsThreadActive(ev))
        {
            EventThreadSync.EnqueueArchive(db, ev, clock.GetCurrentInstant());
        }

        // Web deletes: capture the posted message's and mirrored native event's ids before the
        // row dies so the bot can remove both. The bot handles both itself, so bot callers
        // enqueue nothing.
        if (!context.User.IsBot() && (ev.MessageId is not null || ev.NativeEventId is not null))
        {
            var now = clock.GetCurrentInstant();
            db.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(),
                Type = DeliveryType.DeleteEventMessage,
                ChannelId = ev.ChannelId,
                PayloadJson = JsonSerializer.Serialize(
                    new DeleteEventMessagePayload(ev.ChannelId, ev.MessageId, ev.GuildId, ev.NativeEventId)),
                DueAt = now,
                Status = DeliveryStatus.Pending,
                CreatedAt = now,
            });
        }

        db.Events.Remove(ev);
        await LiveListSync.EnqueueSyncForGuildAsync(db, ev.GuildId, clock.GetCurrentInstant(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Records where the bot posted the event's embed (BotOnly).</summary>
    /// <param name="id">The event id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SetMessage(
        Guid id, SetEventMessageRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        ev.ChannelId = request.ChannelId;
        ev.MessageId = request.MessageId;
        await ChannelEndpoints.UpsertSnapshotAsync(
            db, request.ChannelId, ev.GuildId, request.ChannelName, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Records (or clears) the Discord scheduled event mirroring this event (BotOnly).</summary>
    /// <param name="id">The event id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SetNativeEvent(
        Guid id, SetNativeEventRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        ev.NativeEventId = request.NativeEventId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Records (or clears) the Discord thread channel opened on this event's embed (BotOnly).</summary>
    /// <param name="id">The event id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SetThread(
        Guid id, SetThreadRequest request, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        ev.ThreadId = request.ThreadId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Sets a user's RSVP (self-only for web callers) and syncs the embed for web-side changes.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The event id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutRsvp(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        long userId,
        RsvpRequest request,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // Serialize RSVP mutations per event: the row lock (taken BEFORE the aggregate loads)
        // makes concurrent capacity checks and waitlist promotions queue behind each other
        // instead of double-seating past the cap or double-promoting one freed seat. Early
        // returns roll back via the transaction's dispose.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockEventRowAsync(db, id, cancellationToken);

        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        var option = ev.Options.FirstOrDefault(o => o.Id == request.OptionId);
        if (option is null)
        {
            return Results.BadRequest(new ErrorResponse("Unknown RSVP option for this event."));
        }

        var now = clock.GetCurrentInstant();
        if (RsvpPolicy.IsClosed(ev, now))
        {
            return Results.Conflict(new ErrorResponse("RSVPs for this event are closed."));
        }

        var attendingId = AttendeeRoleSync.AttendingOptionId(ev.Options);
        var existing = ev.Rsvps.FirstOrDefault(r => r.UserId == userId);
        var oldOptionId = existing?.OptionId;
        var oldWaitlisted = existing?.Waitlisted ?? false;

        // Re-clicking the current choice changes nothing — and must not move a queue position
        // (CreatedAt doubles as the waitlist order).
        if (existing is not null && existing.OptionId == option.Id)
        {
            return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
        }

        // Capacity: the attending option queues past its cap (waitlist); any other full option
        // still rejects outright — there is nothing to wait for on a decline/maybe.
        var waitlisted = false;
        if (option.Capacity is int capacity && RsvpPolicy.SeatedCount(ev, option.Id) >= capacity)
        {
            if (option.Id != attendingId)
            {
                return Results.Conflict(new ErrorResponse($"\"{option.Label}\" is full."));
            }

            waitlisted = true;
        }

        if (existing is null)
        {
            var rsvp = new Rsvp
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                UserId = userId,
                OptionId = option.Id,
                Waitlisted = waitlisted,
                CreatedAt = now,
            };
            // Explicit Add: with a client-set Guid key, graph fixup alone would
            // mark this entity as existing and issue an UPDATE instead of INSERT.
            // Fixup then places it into ev.Rsvps for the response DTO.
            db.Rsvps.Add(rsvp);
        }
        else
        {
            existing.OptionId = option.Id;
            existing.Waitlisted = waitlisted;
            existing.CreatedAt = now;
        }

        // Attendee role + thread membership: crossing onto/off an attending SEAT drives both —
        // for BOT callers too (unlike embed sync, the bot never initiates these itself;
        // everything rides the outbox). Waitlisted states pass null: a queued RSVP earns the
        // role and thread on promotion, not on joining. Thread adds are add-only.
        if (attendingId is { } goingId)
        {
            var decision = AttendeeRoleSync.Decide(
                oldWaitlisted ? null : oldOptionId,
                waitlisted ? null : option.Id,
                goingId);
            if (AttendeeRoleSync.IsRoleActive(ev))
            {
                switch (decision)
                {
                    case AttendeeRoleAction.Grant:
                        await AttendeeRoleSync.EnqueueRoleChangeAsync(
                            db, ev, DeliveryType.GrantAttendeeRole, userId, clock, cancellationToken);
                        break;
                    case AttendeeRoleAction.Revoke:
                        await AttendeeRoleSync.EnqueueRoleChangeAsync(
                            db, ev, DeliveryType.RevokeAttendeeRole, userId, clock, cancellationToken);
                        break;
                }
            }

            if (decision == AttendeeRoleAction.Grant && EventThreadSync.IsThreadActive(ev))
            {
                await EventThreadSync.EnqueueMemberAddAsync(db, ev, userId, clock, cancellationToken);
            }
        }

        // Switching off an attending seat frees it — promote the first waitlisted user (the
        // seat move and the promotion commit together; the ping rides the outbox).
        if (!oldWaitlisted && oldOptionId == attendingId)
        {
            await RsvpPolicy.PromoteAsync(db, ev, clock, cancellationToken);
        }

        await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
        await LiveListSync.EnqueueSyncForGuildAsync(db, ev.GuildId, clock.GetCurrentInstant(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Clears a user's RSVP (self-only for web callers).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The event id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> DeleteRsvp(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        long userId,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // Same per-event serialization as PutRsvp — a withdrawal promotes off the waitlist, and
        // two concurrent withdrawals must not both promote the same queue head.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockEventRowAsync(db, id, cancellationToken);

        var ev = await LoadEventAsync(db, id, cancellationToken);
        if (ev is null)
        {
            return Results.NotFound();
        }

        if (await GuardEventReadAsync(context, access, ev, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        var existing = ev.Rsvps.FirstOrDefault(r => r.UserId == userId);
        if (existing is not null)
        {
            // The cutoff freezes withdrawals too — "reject changes" cuts both ways, and a frozen
            // attendee list is what a close-early creator is counting on.
            if (RsvpPolicy.IsClosed(ev, clock.GetCurrentInstant()))
            {
                return Results.Conflict(new ErrorResponse("RSVPs for this event are closed."));
            }

            var wasOptionId = existing.OptionId;
            var wasSeated = !existing.Waitlisted;
            db.Rsvps.Remove(existing);
            ev.Rsvps.Remove(existing);

            if (wasSeated
                && AttendeeRoleSync.IsRoleActive(ev)
                && AttendeeRoleSync.AttendingOptionId(ev.Options) is { } goingId
                && AttendeeRoleSync.Decide(wasOptionId, null, goingId) == AttendeeRoleAction.Revoke)
            {
                await AttendeeRoleSync.EnqueueRoleChangeAsync(
                    db, ev, DeliveryType.RevokeAttendeeRole, userId, clock, cancellationToken);
            }

            // A vacated attending seat promotes the first waitlisted user; a waitlisted
            // withdrawal just shortens the queue.
            if (wasSeated && wasOptionId == AttendeeRoleSync.AttendingOptionId(ev.Options))
            {
                await RsvpPolicy.PromoteAsync(db, ev, clock, cancellationToken);
            }

            await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
            await LiveListSync.EnqueueSyncForGuildAsync(db, ev.GuildId, clock.GetCurrentInstant(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await ToDtoWithChannelAsync(db, ev, cancellationToken));
    }

    /// <summary>Parses natural-language datetime text: explicit TimeZone override, else user zone, else guild zone, else UTC.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="parser">The natural-language datetime parser.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ParseDateTime(
        HttpContext context,
        GuildAccessService access,
        ParseDateTimeRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        CancellationToken cancellationToken)
    {
        // JWT callers always parse as themselves, and may only reference guilds they're in.
        var effectiveUserId = context.User.IsBot() ? request.UserId : context.User.WebUserId();
        if (!context.User.IsBot() && request.GuildId is long requestedGuild &&
            await GuardGuildReadAsync(context, access, requestedGuild, cancellationToken) is { } denied)
        {
            return denied;
        }

        DateTimeZone zone = DateTimeZone.Utc;
        if (request.TimeZone is not null)
        {
            // Explicit zone wins outright — previews for series edits must match the zone the
            // server will actually parse in (the series' stored zone), not the viewer's.
            var explicitZone = Mapping.FindZone(request.TimeZone);
            if (explicitZone is null)
            {
                return Results.BadRequest(new ErrorResponse(
                    $"Unknown time zone \"{request.TimeZone}\". Use an IANA id like America/Chicago."));
            }

            zone = explicitZone;
        }
        else
        {
            if (request.GuildId is long guildId)
            {
                var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
                zone = Mapping.FindZone(guild?.TimeZone) ?? zone;
            }

            if (effectiveUserId is long userId)
            {
                var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
                zone = Mapping.FindZone(user?.TimeZone) ?? zone;
            }
        }

        if (!parser.TryResolve(request.Text, zone, out var instant, out var error))
        {
            return Results.BadRequest(new ErrorResponse(error!));
        }

        var utc = instant.ToDateTimeOffset();
        return Results.Ok(new ParseDateTimeResponse(utc, utc.ToUnixTimeSeconds(), zone.Id));
    }

    /// <summary>Takes a FOR UPDATE lock on the event row inside the ambient transaction, so
    /// competing RSVP mutations for one event serialize (capacity checks and waitlist promotions
    /// stay race-free). A missing event locks nothing and falls through to the 404.</summary>
    /// <param name="db">The database context (a transaction must be open).</param>
    /// <param name="id">The event id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private static Task LockEventRowAsync(CalCronyDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"""SELECT "Id" FROM "Events" WHERE "Id" = {id} FOR UPDATE""", cancellationToken);

    /// <summary>Loads an event with options, RSVPs, and series for DTO mapping.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="id">The event id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The event with options, RSVPs, and series, or null.</returns>
    private static Task<Event?> LoadEventAsync(CalCronyDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Events
            .Include(e => e.Options)
            .Include(e => e.Rsvps)
            .Include(e => e.Series)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    /// <summary>Fetches or lazily creates the guild row (guilds appear on first use).</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The tracked guild row.</returns>
    internal static async Task<Guild> GetOrCreateGuildAsync(
        CalCronyDbContext db, long guildId, CancellationToken cancellationToken)
    {
        var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
        if (guild is null)
        {
            guild = new Guild { Id = guildId };
            db.Guilds.Add(guild);
        }

        return guild;
    }

    /// <summary>The zone events parse in: user's personal zone, else the guild's, else UTC.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="guild">The guild row.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The effective zone for parsing.</returns>
    internal static async Task<DateTimeZone> ResolveZoneAsync(
        CalCronyDbContext db, long userId, Guild guild, CancellationToken cancellationToken)
    {
        var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
        return Mapping.FindZone(user?.TimeZone) ?? Mapping.FindZone(guild.TimeZone) ?? DateTimeZone.Utc;
    }
}
