using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;

namespace CalCrony.Api.Endpoints;

/// <summary>Guild and per-user settings endpoints.</summary>
public static class SettingsEndpoints
{
    /// <summary>Maps settings routes.</summary>
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/guilds/{guildId:long}/settings", GetGuildSettings);
        app.MapPut("/guilds/{guildId:long}/settings", PutGuildSettings);
        app.MapGet("/users/{userId:long}/settings", GetUserSettings);
        app.MapPut("/users/{userId:long}/settings", PutUserSettings);
    }

    /// <summary>Reads a guild's timezone and default channel.</summary>
    private static async Task<IResult> GetGuildSettings(
        HttpContext context, GuildAccessService access, long guildId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
        return Results.Ok(new GuildSettingsDto(guild?.TimeZone ?? "UTC", guild?.DefaultChannelId));
    }

    /// <summary>Updates guild settings (managers only for web callers); validates the timezone id.</summary>
    private static async Task<IResult> PutGuildSettings(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        GuildSettingsDto settings,
        CalCronyDbContext db,
        CancellationToken cancellationToken)
    {
        if (!context.User.IsBot())
        {
            var userId = context.User.WebUserId();
            var tier = userId is null
                ? GuildAccess.None
                : await access.CheckAsync(userId.Value, guildId, cancellationToken);
            if (tier == GuildAccess.Stale)
            {
                return GuildAccessService.StaleSnapshot();
            }

            if (tier != GuildAccess.Manager)
            {
                return Results.Json(
                    new ErrorResponse("Only server managers can change server settings."),
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        if (Mapping.FindZone(settings.TimeZone) is null)
        {
            return Results.BadRequest(new ErrorResponse($"Unknown time zone \"{settings.TimeZone}\". Use an IANA id like America/Chicago."));
        }

        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);
        guild.TimeZone = settings.TimeZone;
        guild.DefaultChannelId = settings.DefaultChannelId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new GuildSettingsDto(guild.TimeZone, guild.DefaultChannelId));
    }

    /// <summary>Reads a user's personal settings (self-only for web callers).</summary>
    private static async Task<IResult> GetUserSettings(
        HttpContext context, long userId, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
        return Results.Ok(new UserSettingsDto(user?.TimeZone, user?.DmConfirmations ?? true));
    }

    /// <summary>Updates a user's personal settings (self-only for web callers); validates the timezone id.</summary>
    private static async Task<IResult> PutUserSettings(
        HttpContext context, long userId, UserSettingsDto settings, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        if (settings.TimeZone is not null && Mapping.FindZone(settings.TimeZone) is null)
        {
            return Results.BadRequest(new ErrorResponse($"Unknown time zone \"{settings.TimeZone}\". Use an IANA id like America/Chicago."));
        }

        var user = await db.UserProfiles.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            user = new UserProfile { Id = userId };
            db.UserProfiles.Add(user);
        }

        user.TimeZone = settings.TimeZone;
        user.DmConfirmations = settings.DmConfirmations;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new UserSettingsDto(user.TimeZone, user.DmConfirmations));
    }
}
