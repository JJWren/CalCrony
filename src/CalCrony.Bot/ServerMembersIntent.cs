using Discord;
using Discord.Net;

namespace CalCrony.Bot;

/// <summary>What <see cref="ServerMembersIntent.SettingKey"/> asks for.</summary>
public enum ServerMembersIntentMode
{
    /// <summary>Ask for the intent exactly when the application has it, read from Discord at start.</summary>
    Auto = 0,

    /// <summary>Always ask for it — the behaviour before the setting existed.</summary>
    On = 1,

    /// <summary>Never ask for it.</summary>
    Off = 2,
}

/// <summary>Whether the bot asks Discord for the privileged Server Members intent
/// (<c>GUILD_MEMBERS</c>), decided once at start. Discord refuses a connection that asks for an
/// intent the application does not have (gateway close code 4014), which took the whole bot
/// offline when the intent was missing; deciding first lets the bot run without it, with the two
/// features that need it switched off: role lists in /availability, and the member half of the
/// role snapshots the website checks restricted choices against (ADR 0004).</summary>
/// <param name="Mode">What the setting asked for.</param>
/// <param name="Enabled">Whether the bot asks for the intent.</param>
/// <param name="Reason">Why, in words, for the start-up log line.</param>
/// <param name="LookupError">In auto, why the application could not be read; null when it was,
/// or when nothing had to be read.</param>
public sealed record ServerMembersIntent(ServerMembersIntentMode Mode, bool Enabled, string Reason, string? LookupError = null)
{
    /// <summary>The configuration key (<c>Discord__ServerMembersIntent</c> as an environment variable).</summary>
    public const string SettingKey = "Discord:ServerMembersIntent";

    /// <summary>The gateway close code Discord sends for an intent the application may not use.</summary>
    public const int RefusedCloseCode = 4014;

    /// <summary>What /availability answers when asked to list a role's members without the intent.</summary>
    public const string RoleListOff =
        "Listing who holds a role needs Discord's Server Members access, which CalCrony doesn't have right now. "
        + "`/availability event`, and `/availability slots` for yourself or a member, still work.";

    /// <summary>The critical log line when the flags, read again while running, no longer show the intent.</summary>
    public const string LostMessage =
        "The application no longer has the Server Members intent (its flags, read again from Discord). The bot stops "
        + "now so that it restarts without it (" + SettingKey + " is auto).";

    /// <summary>How long the bot waits after Discord refuses the intent before it stops to restart.
    /// Discord resets a bot's token when it opens more than 1,000 new connections in a day; at one
    /// restart every five minutes, even a refusal met on every start stays under 300.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromMinutes(5);

    /// <summary>How often, in auto with the intent asked for, the application's flags are read again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>The application flags that mean it has the intent: approved, or switched on in the
    /// Developer Portal.</summary>
    private const ApplicationFlags MembersFlags =
        ApplicationFlags.GatewayGuildMembers | ApplicationFlags.GatewayGuildMembersLimited;

    /// <summary>The intents the bot connects with.</summary>
    public GatewayIntents GatewayIntents =>
        Enabled ? GatewayIntents.Guilds | GatewayIntents.GuildMembers : GatewayIntents.Guilds;

    /// <summary>Whether a refusal of the intent while running stops the bot, so that its container
    /// restarts and the decision is made again: only in auto, and only when the intent was asked for.
    /// The flags are read again while running in the same case.</summary>
    public bool RestartsOnRefusal => Mode == ServerMembersIntentMode.Auto && Enabled;

    /// <summary>The critical log line for a refusal (close code 4014), saying what happens next.</summary>
    public string RefusalMessage => Enabled
        ? RestartsOnRefusal
            ? $"Discord refused the Server Members intent (gateway close code {RefusedCloseCode}). The bot stops in "
              + $"{RestartDelay.TotalMinutes:0} minutes so that it restarts and decides again from the application's "
              + $"settings ({SettingKey} is auto); the wait keeps restarts well inside Discord's daily limit on new "
              + "connections. If it keeps restarting, set it to off."
            : $"Discord refused the Server Members intent (gateway close code {RefusedCloseCode}). {SettingKey} is on, "
              + "so the bot stays offline: set it to auto or off, or switch the intent on for the application in the "
              + "Discord Developer Portal, and restart the bot."
        : $"Discord refused an intent the bot asked for (gateway close code {RefusedCloseCode}).";

    /// <summary>Reads the setting: empty, missing or <c>auto</c> is auto; <c>on</c> and <c>off</c>
    /// as written; case and surrounding spaces do not matter.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns>The mode.</returns>
    /// <exception cref="InvalidOperationException">Any other value — the bot does not start.</exception>
    public static ServerMembersIntentMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => ServerMembersIntentMode.Auto,
        "on" => ServerMembersIntentMode.On,
        "off" => ServerMembersIntentMode.Off,
        _ => throw new InvalidOperationException(
            $"{SettingKey} (DISCORD_SERVER_MEMBERS_INTENT in the compose files) must be auto, on or off."),
    };

    /// <summary>Whether application flags show the intent: approved, or switched on.</summary>
    /// <param name="flags">The application's flags.</param>
    /// <returns>True when the application has the intent.</returns>
    public static bool HasMembersIntent(ApplicationFlags flags) => (flags & MembersFlags) != 0;

    /// <summary>The decision, pure: on and off as set; auto asks for the intent when the
    /// application has it, and — as before the setting existed — when it could not be read.</summary>
    /// <param name="mode">What the setting asked for.</param>
    /// <param name="flags">The application's flags, or null when they could not be read.</param>
    /// <param name="lookupError">Why they could not be read.</param>
    /// <returns>The decision.</returns>
    public static ServerMembersIntent Decide(ServerMembersIntentMode mode, ApplicationFlags? flags, string? lookupError = null) =>
        mode switch
        {
            ServerMembersIntentMode.On => new(mode, true, "set to on"),
            ServerMembersIntentMode.Off => new(mode, false, "set to off"),
            _ when flags is null => new(
                mode, true,
                $"auto: the application could not be read, so the bot asks for it as before: {lookupError ?? "no answer"}",
                lookupError ?? "no answer"),
            _ when HasMembersIntent(flags.Value) => new(mode, true, "auto: the application has it"),
            _ => new(mode, false, "auto: the application does not have it"),
        };

    /// <summary>Reads the setting and, in auto, the bot's own application flags from Discord, with
    /// the bot's token, before the gateway client exists — its intents are fixed when it is built.
    /// The read has a time limit, and any failure leaves the bot asking for the intent as before.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="reader">Reads the application's flags.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="InvalidOperationException">The setting has a value other than auto, on or off.</exception>
    public static async Task<ServerMembersIntent> ResolveAsync(
        IConfiguration configuration, ApplicationFlagsReader reader, CancellationToken cancellationToken = default)
    {
        var mode = ParseMode(configuration[SettingKey]);
        if (mode != ServerMembersIntentMode.Auto)
        {
            return Decide(mode, null);
        }

        var token = configuration["Discord:BotToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            return Decide(mode, null, "no bot token is configured");
        }

        try
        {
            return Decide(mode, await reader.ReadAsync(token, cancellationToken));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Decide(mode, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Whether flags read again while running show the intent gone, so that the bot must
    /// restart without it: only in auto with the intent asked for.</summary>
    /// <param name="flags">The application's flags as read again.</param>
    /// <returns>True when the bot should restart.</returns>
    public bool LostOnRecheck(ApplicationFlags flags) => RestartsOnRefusal && !HasMembersIntent(flags);

    /// <summary>Whether a disconnect was Discord refusing an intent: a gateway close with code
    /// 4014, however Discord.Net wrapped it.</summary>
    /// <param name="exception">The exception the disconnect carried.</param>
    /// <returns>True for a refusal.</returns>
    public static bool IsRefusal(Exception? exception) => exception switch
    {
        null => false,
        WebSocketClosedException { CloseCode: RefusedCloseCode } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsRefusal),
        _ => IsRefusal(exception.InnerException),
    };

    /// <summary>The start-up log line: what was decided, why, and what is off without the intent.</summary>
    /// <returns>The line.</returns>
    public string Describe() => Enabled
        ? $"Server Members intent: on ({Reason})."
        : $"Server Members intent: off ({Reason}). Role lists in /availability and role checks on the website are off.";
}
