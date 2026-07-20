namespace CalCrony.Web;

/// <summary>Builds the bot invite URL. The Discord application id is runtime config
/// (<c>Discord:AppId</c>, injected via the DISCORD_APP_ID container variable) so a test
/// deployment advertises its own test bot instead of production's. The permission integer
/// stays code: it tracks the feature set (Manage Events / Manage Roles / thread permissions)
/// and only changes with releases.</summary>
public static class DiscordInvite
{
    /// <summary>The production CalCrony application, used when no Discord:AppId is configured —
    /// a stock deployment keeps working with zero web config.</summary>
    public const string DefaultAppId = "1527749302443835532";

    /// <summary>Bot permissions the invite grants; keep in sync with README's go-live checklist
    /// and the pinned invite-URL test.</summary>
    public const string Permissions = "335007534080";

    /// <summary>The full invite URL for the given application id (null/blank = production).
    /// The id is trimmed and URL-escaped so a sloppy .env value can't break the URL.</summary>
    /// <param name="appId">The Discord application id from configuration.</param>
    /// <returns>The invite URL.</returns>
    public static string Url(string? appId) =>
        $"https://discord.com/oauth2/authorize?client_id={Uri.EscapeDataString(string.IsNullOrWhiteSpace(appId) ? DefaultAppId : appId.Trim())}"
        + $"&permissions={Permissions}&integration_type=0&scope=bot+applications.commands";
}
