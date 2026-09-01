using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Guild and per-user settings endpoints.</summary>
public static class SettingsEndpoints
{
    /// <summary>Maps settings routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/guilds/{guildId:long}/settings", GetGuildSettings);
        app.MapPut("/guilds/{guildId:long}/settings", PutGuildSettings);
        app.MapGet("/users/{userId:long}/settings", GetUserSettings);
        app.MapPut("/users/{userId:long}/settings", PutUserSettings);
    }

    /// <summary>Reads a guild's timezone and default channel.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetGuildSettings(
        HttpContext context, GuildAccessService access, long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
        return Results.Ok(new GuildSettingsDto(
            guild?.TimeZone ?? "UTC", guild?.DefaultChannelId, guild?.MirrorNativeEvents ?? false));
    }

    /// <summary>Updates guild settings (managers only for web callers); validates the timezone id.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutGuildSettings(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        GuildSettingsDto settings,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildManageAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (Mapping.FindZone(settings.TimeZone) is null)
        {
            return Results.BadRequest(new ErrorResponse($"Unknown time zone \"{settings.TimeZone}\". Use an IANA id like America/Chicago."));
        }

        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);

        // The PUT is a whole-object write, so the log diffs it against the stored row and names
        // only what actually changed — the field names, never the submitted values (the log's
        // contract, and what the privacy policy promises); a no-op save (the bot's
        // read-modify-write of an unchanged field, a first-touch guild row) writes no entry.
        var changed = ActionLog.Changed(
            ("timezone", guild.TimeZone != settings.TimeZone),
            ("default channel", guild.DefaultChannelId != settings.DefaultChannelId),
            ("native events", guild.MirrorNativeEvents != settings.MirrorNativeEvents));
        guild.TimeZone = settings.TimeZone;
        guild.DefaultChannelId = settings.DefaultChannelId;
        guild.MirrorNativeEvents = settings.MirrorNativeEvents;
        if (changed.Count > 0)
        {
            ActionLog.Record(
                db, guildId, ActionLog.ActorFor(context), ActionLogAction.SettingsChanged, ActionTargetType.Guild, null,
                $"Changed server settings — {string.Join(", ", changed)}", clock.GetCurrentInstant(),
                new { fields = changed });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new GuildSettingsDto(guild.TimeZone, guild.DefaultChannelId, guild.MirrorNativeEvents));
    }

    /// <summary>Reads a user's personal settings (self-only for web callers).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetUserSettings(
        HttpContext context, long userId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
        return Results.Ok(ToDto(user));
    }

    /// <summary>The settings a caller sees; an absent profile reads as the defaults (DM reminders OFF).</summary>
    private static UserSettingsDto ToDto(UserProfile? user) => new(
        user?.TimeZone,
        user?.DmConfirmations ?? true,
        ValidThemeOrNull(user?.Theme),
        user?.DmReminders ?? false,
        user?.DmRemindersBlockedAt?.ToDateTimeOffset());

    /// <summary>Responses never carry a theme id clients don't know: a stored value that is no
    /// longer valid (renamed/retired theme) reads as null, i.e. the default. PUT validates on the
    /// way in, so this only matters for values that predate a rename.</summary>
    /// <param name="theme">The stored theme value.</param>
    /// <returns>The theme when it is currently valid; otherwise null.</returns>
    private static string? ValidThemeOrNull(string? theme) =>
        theme is not null && InterfaceThemes.IsValid(theme) ? theme : null;

    /// <summary>Updates a user's personal settings (self-only for web callers); validates the timezone id.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutUserSettings(
        HttpContext context, long userId, UserSettingsDto settings, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        if (settings.TimeZone is not null && Mapping.FindZone(settings.TimeZone) is null)
        {
            return Results.BadRequest(new ErrorResponse($"Unknown time zone \"{settings.TimeZone}\". Use an IANA id like America/Chicago."));
        }

        if (settings.Theme is not null && !InterfaceThemes.IsValid(settings.Theme))
        {
            return Results.BadRequest(new ErrorResponse(
                $"Unknown theme \"{settings.Theme}\". Valid themes: {string.Join(", ", InterfaceThemes.All)}."));
        }

        // One transaction for the profile write and any queued-DM withdrawal it implies: the
        // row lock the UPDATE takes means a concurrent re-enable (and anything the scheduler
        // would enqueue under it) can only land after the opt-out AND its withdrawal committed.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            user = new UserProfile { Id = userId };
            db.UserProfiles.Add(user);
        }

        user.TimeZone = settings.TimeZone;
        user.DmConfirmations = settings.DmConfirmations;
        // Null keeps the stored theme (see UserSettingsDto.Theme) — the bot's timezone/DM writes
        // never carry a theme and must not reset a web-chosen one.
        user.Theme = settings.Theme ?? user.Theme;
        // Same null-keeps rule for the DM-reminder opt-in. Turning it on also consumes the
        // one-time offer (someone who found the setting themselves is never prompted later) and
        // clears the closed-DMs marker so they can retry after opening their DMs; turning it off
        // withdraws anything already queued, so revoked consent can't be bypassed by the outbox.
        var withdrawQueued = false;
        if (settings.DmReminders is bool dmReminders)
        {
            withdrawQueued = user.DmReminders && !dmReminders;
            user.DmReminders = dmReminders;
            if (dmReminders)
            {
                user.DmRemindersOffered = true;
                user.DmRemindersBlockedAt = null;
                user.DmRemindersEnabledAt = clock.GetCurrentInstant(); // the consent version a late refusal is compared against
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (withdrawQueued)
        {
            await Services.DmReminderFanOut.CancelPendingAsync(db, userId, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToDto(user));
    }
}
