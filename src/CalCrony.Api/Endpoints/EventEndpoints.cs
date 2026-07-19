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

    /// <summary>The standard RSVP option set every event starts with (also used by poll conversion).</summary>
    /// <returns>Fresh Going/Not going/Maybe option rows.</returns>
    internal static List<RsvpOption> DefaultRsvpOptions() =>
    [
        new RsvpOption { Id = Guid.NewGuid(), Emote = "✅", Label = "Going", SortOrder = 0 },
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

        var description = request.Description ?? template?.Description;
        var durationMinutes = request.DurationMinutes ?? template?.DurationMinutes;
        var location = request.Location ?? template?.Location;
        var imageUrl = request.ImageUrl ?? template?.ImageUrl;
        // Role selection is bot-only (the web can't enumerate Discord roles); templates never carry one.
        var attendeeRoleId = isBot ? request.AttendeeRoleId : null;
        // Threads are a plain yes/no, so WantsThread is honored for BOTH caller types — the bot
        // opens the thread when it posts the embed.
        var wantsThread = request.WantsThread;
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
            Status = EventStatus.Scheduled,
            SeriesId = series?.Id,
            Series = series,
            CreatedAt = now,
            Options = DefaultRsvpOptions(),
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

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/events/{ev.Id}", ev.ToDto());
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

        return Results.Ok(ev.ToDto());
    }

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

        if (request.AttendeeRoleId is not null && request.ClearAttendeeRole)
        {
            return Results.BadRequest(new ErrorResponse("Choose an attendee role or clear it, not both."));
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

        var roleSyncNow = clock.GetCurrentInstant();
        if (isLive && !staysLive)
        {
            // Cancel/end via PATCH — the previously side-effect-free path. Revoke everything
            // under the OLD role; a same-request role change never grants on a dying event.
            if (oldRole is { } endedRole)
            {
                AttendeeRoleSync.EnqueueRoleFanOut(db, ev, DeliveryType.RevokeAttendeeRole, endedRole, roleSyncNow);
            }

            if (ev.ThreadId is not null)
            {
                EventThreadSync.EnqueueArchive(db, ev, roleSyncNow);
            }
        }
        else if (isLive && newRole != oldRole)
        {
            // Re-sync on role change: without the old-role revoke fan-out, the end-of-event
            // cleanup would only ever revoke the CURRENT role and the old grants would leak.
            if (oldRole is { } previousRole)
            {
                AttendeeRoleSync.EnqueueRoleFanOut(db, ev, DeliveryType.RevokeAttendeeRole, previousRole, roleSyncNow);
            }

            if (newRole is { } grantedRole)
            {
                AttendeeRoleSync.EnqueueRoleFanOut(db, ev, DeliveryType.GrantAttendeeRole, grantedRole, roleSyncNow);
            }
        }

        await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
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
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
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
        return Results.Ok(ev.ToDto());
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
        return Results.Ok(ev.ToDto());
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

        var existing = ev.Rsvps.FirstOrDefault(r => r.UserId == userId);
        var oldOptionId = existing?.OptionId;
        if (option.Capacity is int capacity &&
            existing?.OptionId != option.Id &&
            ev.Rsvps.Count(r => r.OptionId == option.Id) >= capacity)
        {
            return Results.Conflict(new ErrorResponse($"\"{option.Label}\" is full."));
        }

        if (existing is null)
        {
            var rsvp = new Rsvp
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                UserId = userId,
                OptionId = option.Id,
                CreatedAt = clock.GetCurrentInstant(),
            };
            // Explicit Add: with a client-set Guid key, graph fixup alone would
            // mark this entity as existing and issue an UPDATE instead of INSERT.
            // Fixup then places it into ev.Rsvps for the response DTO.
            db.Rsvps.Add(rsvp);
        }
        else
        {
            existing.OptionId = option.Id;
            existing.CreatedAt = clock.GetCurrentInstant();
        }

        // Attendee role + thread membership: crossing onto/off the Going option drives both —
        // for BOT callers too (unlike embed sync, the bot never initiates these itself;
        // everything rides the outbox). Thread adds are add-only: no removal on crossing off.
        if (AttendeeRoleSync.GoingOptionId(ev.Options) is { } goingId)
        {
            var decision = AttendeeRoleSync.Decide(oldOptionId, option.Id, goingId);
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

        await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ev.ToDto());
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
            var wasOptionId = existing.OptionId;
            db.Rsvps.Remove(existing);
            ev.Rsvps.Remove(existing);

            if (AttendeeRoleSync.IsRoleActive(ev)
                && AttendeeRoleSync.GoingOptionId(ev.Options) is { } goingId
                && AttendeeRoleSync.Decide(wasOptionId, null, goingId) == AttendeeRoleAction.Revoke)
            {
                await AttendeeRoleSync.EnqueueRoleChangeAsync(
                    db, ev, DeliveryType.RevokeAttendeeRole, userId, clock, cancellationToken);
            }

            await EnqueueEmbedSyncAsync(context, db, ev, clock, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ev.ToDto());
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
