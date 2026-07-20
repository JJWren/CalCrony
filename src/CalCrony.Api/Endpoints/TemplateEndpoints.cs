using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Event templates: reusable event shapes saved from existing events. Any member saves
/// and uses; editing and deleting require the creator or a manager. Names are unique per guild —
/// the API rejects case-insensitive duplicates, backed by a functional unique index on
/// (GuildId, lower(Name)) so races lose regardless of casing.</summary>
public static class TemplateEndpoints
{
    private const int MaxPerGuild = 25;
    private const int MaxNameLength = 64;

    /// <summary>Maps template routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/guilds/{guildId:long}/templates", SaveTemplate);
        app.MapGet("/guilds/{guildId:long}/templates", ListTemplates);
        app.MapPatch("/templates/{id:guid}", UpdateTemplate);
        app.MapDelete("/templates/{id:guid}", DeleteTemplate);
    }

    private const int MaxNotifications = 5;

    /// <summary>Applies a partial update (creator or manager; non-members get 404 so ids can't be
    /// probed). Null fields stay unchanged; a non-null Notifications list replaces the whole spec
    /// set. Editing never touches events already created from the template — denormalization
    /// runs in both directions.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The template id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> UpdateTemplate(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        UpdateTemplateRequest request,
        CalCronyDbContext db,
        CancellationToken cancellationToken)
    {
        var template = await db.EventTemplates
            .Include(t => t.Notifications)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return Results.NotFound();
        }

        if (await EventEndpoints.GuardMutateAsync(
                context, access, template.GuildId, template.CreatorId,
                "Only the template creator or a server manager can edit this template.", cancellationToken) is { } denied)
        {
            return denied;
        }

        if (request.Recurrence is not null && request.ClearRecurrence)
        {
            return Results.BadRequest(new ErrorResponse("Choose a repeat rule or clear it, not both."));
        }

        if (request.Recurrence is { } rule && rule.Interval is < 1 or > 12)
        {
            return Results.BadRequest(new ErrorResponse("Repeat interval must be between 1 and 12."));
        }

        if ((Validation.TooLong("title", request.Title, FieldLimits.EventTitle)
            ?? Validation.TooLong("description", request.Description, FieldLimits.EventDescription)
            ?? Validation.TooLong("location", request.Location, FieldLimits.EventLocation)
            ?? Validation.TooLong("image URL", request.ImageUrl, FieldLimits.EventImageUrl)
            ?? Validation.BadDuration(request.DurationMinutes)) is { } invalid)
        {
            return invalid;
        }

        if (request.Name is { } rawName)
        {
            var name = rawName.Trim();
            if (name.Length is 0 or > MaxNameLength)
            {
                return Results.BadRequest(new ErrorResponse("The template name must be 1-64 characters."));
            }

            var lowered = name.ToLowerInvariant();
            if (await db.EventTemplates.AnyAsync(
                    t => t.GuildId == template.GuildId && t.Id != template.Id && t.Name.ToLower() == lowered,
                    cancellationToken))
            {
                return Results.Conflict(new ErrorResponse($"A template named \"{name}\" already exists."));
            }

            template.Name = name;
        }

        if (request.Notifications is { } specs)
        {
            if (specs.Count > MaxNotifications)
            {
                return Results.BadRequest(new ErrorResponse($"A template can carry at most {MaxNotifications} notifications."));
            }

            foreach (var spec in specs)
            {
                if (spec.MinutesBefore is < 0 or > FieldLimits.MaxMinutes)
                {
                    return Results.BadRequest(new ErrorResponse(
                        $"minutesBefore must be between 0 and {FieldLimits.MaxMinutes} (4 weeks)."));
                }

                if ((Validation.TooLong("message", spec.Message, FieldLimits.NotificationMessage)
                    ?? Validation.TooLong("mentions", spec.Mentions, FieldLimits.NotificationMentions)) is { } badSpec)
                {
                    return badSpec;
                }
            }

            db.EventTemplateNotifications.RemoveRange(template.Notifications);
            template.Notifications = [];
            foreach (var spec in specs.OrderByDescending(n => n.MinutesBefore))
            {
                var row = new EventTemplateNotification
                {
                    Id = Guid.NewGuid(),
                    TemplateId = template.Id,
                    MinutesBefore = spec.MinutesBefore,
                    Message = spec.Message,
                    Mentions = spec.Mentions,
                    ChannelId = spec.ChannelId,
                };
                // Explicit Add: with a client-set Guid key, graph fixup alone would mark the
                // row as existing and issue an UPDATE instead of INSERT. Fixup then places the
                // row into template.Notifications for the response DTO — adding it manually too
                // would double it up.
                db.EventTemplateNotifications.Add(row);
            }
        }

        template.Title = request.Title ?? template.Title;
        template.Description = request.Description ?? template.Description;
        template.DurationMinutes = request.DurationMinutes ?? template.DurationMinutes;
        template.Location = request.Location ?? template.Location;
        template.ImageUrl = request.ImageUrl ?? template.ImageUrl;

        if (request.ClearRecurrence)
        {
            template.RecurrenceUnit = null;
            template.RecurrenceInterval = null;
            template.RecurrenceMonthlyMode = null;
        }
        else if (request.Recurrence is { } newRule)
        {
            template.RecurrenceUnit = newRule.Unit;
            template.RecurrenceInterval = newRule.Interval;
            template.RecurrenceMonthlyMode = newRule.MonthlyMode;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
        })
        {
            return Results.Conflict(new ErrorResponse($"A template named \"{template.Name}\" already exists."));
        }

        return Results.Ok(ToDto(template));
    }

    /// <summary>Saves a template from an existing event's current content, notifications, and —
    /// when the event is the live occurrence of a running series — its repeat rule. Fully
    /// denormalized: the source event can be deleted afterward without affecting the template.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SaveTemplate(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        SaveTemplateRequest request,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var creatorId = context.User.IsBot() ? request.CreatorId : context.User.WebUserId()!.Value;
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength)
        {
            return Results.BadRequest(new ErrorResponse("The template name must be 1-64 characters."));
        }

        var ev = await db.Events
            .Include(e => e.Notifications)
            .Include(e => e.Series)
            .FirstOrDefaultAsync(e => e.Id == request.EventId, cancellationToken);
        if (ev is null || ev.GuildId != guildId)
        {
            // Cross-guild is indistinguishable from missing — event ids must not be probeable.
            return Results.NotFound();
        }

        if (await db.EventTemplates.CountAsync(t => t.GuildId == guildId, cancellationToken) >= MaxPerGuild)
        {
            return Results.Conflict(new ErrorResponse($"A server can have at most {MaxPerGuild} templates."));
        }

        var lowered = name.ToLowerInvariant();
        if (await db.EventTemplates.AnyAsync(
                t => t.GuildId == guildId && t.Name.ToLower() == lowered, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse($"A template named \"{name}\" already exists."));
        }

        // Capture the rule only when the source is the live occurrence of a running series —
        // the same predicate scoped notification edits use.
        var captureRule = ev.Series is { Ended: false }
            && ev.Status is EventStatus.Scheduled or EventStatus.Started;
        var template = new EventTemplate
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatorId = creatorId,
            Name = name,
            Title = ev.Title,
            Description = ev.Description,
            DurationMinutes = ev.DurationMinutes,
            Location = ev.Location,
            ImageUrl = ev.ImageUrl,
            RecurrenceUnit = captureRule ? ev.Series!.Unit : null,
            RecurrenceInterval = captureRule ? ev.Series!.Interval : null,
            RecurrenceMonthlyMode = captureRule ? ev.Series!.MonthlyMode : null,
            CreatedAt = clock.GetCurrentInstant(),
            Notifications = [.. ev.Notifications
                .OrderByDescending(n => n.MinutesBefore)
                .Select(n => new EventTemplateNotification
                {
                    Id = Guid.NewGuid(),
                    MinutesBefore = n.MinutesBefore,
                    Message = n.Message,
                    Mentions = n.Mentions,
                    ChannelId = n.ChannelId,
                })],
        };
        db.EventTemplates.Add(template);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
        })
        {
            return Results.Conflict(new ErrorResponse($"A template named \"{name}\" already exists."));
        }

        return Results.Created($"/templates/{template.Id}", ToDto(template));
    }

    /// <summary>Lists the guild's templates, name-ordered.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ListTemplates(
        HttpContext context, GuildAccessService access, long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var templates = await db.EventTemplates
            .Include(t => t.Notifications)
            .Where(t => t.GuildId == guildId)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);
        return Results.Ok(templates.Select(ToDto));
    }

    /// <summary>Deletes a template (creator or manager; non-members get 404 so ids can't be probed).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The template id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> DeleteTemplate(
        HttpContext context, GuildAccessService access, Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var template = await db.EventTemplates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return Results.NotFound();
        }

        if (await EventEndpoints.GuardMutateAsync(
                context, access, template.GuildId, template.CreatorId,
                "Only the template creator or a server manager can delete this template.", cancellationToken) is { } denied)
        {
            return denied;
        }

        db.EventTemplates.Remove(template);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Projects a template row (notifications loaded) to its DTO.</summary>
    /// <param name="template">The template row.</param>
    /// <returns>The projected DTO.</returns>
    internal static EventTemplateDto ToDto(EventTemplate template) => new(
        template.Id,
        template.GuildId,
        template.CreatorId,
        template.Name,
        template.Title,
        template.Description,
        template.DurationMinutes,
        template.Location,
        template.ImageUrl,
        template.RecurrenceUnit is { } unit
            ? new RecurrenceRuleDto(unit, template.RecurrenceInterval!.Value, template.RecurrenceMonthlyMode!.Value)
            : null,
        [.. template.Notifications
            .OrderByDescending(n => n.MinutesBefore)
            .Select(n => new TemplateNotificationDto(n.MinutesBefore, n.Message, n.Mentions, n.ChannelId))],
        template.CreatedAt.ToDateTimeOffset());
}
