using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Event templates: reusable event shapes saved from existing events. Any member saves
/// and uses; deleting requires the creator or a manager. Names are unique per guild — the API
/// rejects case-insensitive duplicates (the index catches exact-case races; differently-cased
/// simultaneous saves can both land, accepted at this scale).</summary>
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
        app.MapDelete("/templates/{id:guid}", DeleteTemplate);
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

        var lowered = name.ToLower();
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
