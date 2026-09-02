using System.Globalization;

namespace CalCrony.Contracts;

/// <summary>Builds the bot invite URL — the one place the invite's scopes and permission integer
/// live, shared by the web app's buttons and the bot's "add me to this server" replies. On the web
/// the Discord application id is runtime config (<c>Discord:AppId</c>, injected via the
/// DISCORD_APP_ID container variable) so a test deployment advertises its own test bot instead of
/// production's; the bot uses the application id Discord stamps on each interaction. The
/// permission integer stays code: it tracks the feature set (Manage Events / Manage Roles / thread
/// permissions) and only changes with releases.</summary>
public static class DiscordInvite
{
    /// <summary>The production CalCrony application, used when no Discord:AppId is configured —
    /// a stock deployment keeps working with zero web config.</summary>
    public const string DefaultAppId = "1527749302443835532";

    /// <summary>Bot permissions the invite grants; keep in sync with README's go-live checklist
    /// and the pinned invite-URL test.</summary>
    public const string Permissions = "335275969536";

    /// <summary>The full invite URL for the given application id (null/blank = production).
    /// The id is trimmed and URL-escaped so a sloppy .env value can't break the URL.</summary>
    /// <param name="appId">The Discord application id from configuration.</param>
    /// <returns>The invite URL.</returns>
    public static string Url(string? appId) =>
        Build(string.IsNullOrWhiteSpace(appId) ? DefaultAppId : appId.Trim());

    /// <summary>The full invite URL for a known application id — the bot's own, taken from the
    /// interaction it is answering, so a test bot never advertises production's invite.</summary>
    /// <param name="appId">The Discord application id.</param>
    /// <returns>The invite URL.</returns>
    public static string Url(ulong appId) => Build(appId.ToString(CultureInfo.InvariantCulture));

    private static string Build(string appId) =>
        $"https://discord.com/oauth2/authorize?client_id={Uri.EscapeDataString(appId)}"
        + $"&permissions={Permissions}&integration_type=0&scope=bot+applications.commands";
}
