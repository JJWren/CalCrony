using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;

namespace CalCrony.Bot.Modules;

/// <summary>/help — what CalCrony is, first steps, and where to go next.</summary>
/// <param name="configuration">The application configuration.</param>
public class HelpModule(IConfiguration configuration) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly string Version =
        (typeof(HelpModule).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?
        .InformationalVersion.Split('+')[0] ?? "dev";

    /// <summary>Replies with an ephemeral orientation embed. The community and donate links are
    /// config-driven and omitted when unset, so test and self-hosted deployments never advertise
    /// the hosted instance's server or tip jar.</summary>
    [SlashCommand("help", "What CalCrony is, first steps, and where to get support")]
    public async Task HelpAsync()
    {
        var lines = new List<string>
        {
            "Schedule events with RSVP buttons, recurring series, time polls, availability grids, " +
            "and calendar sync — right from slash commands, with a web view at " +
            "[calcrony.app](https://calcrony.app).",
            "",
            "**New server?** Run `/settings server-timezone` and `/settings default-channel` " +
            "first — they shape everything else.",
            "",
            "📖 [Docs & full command list](https://calcrony.app/docs)",
        };

        if (ConfiguredHttpsUrl("Discord:SupportServerInvite") is { } invite)
        {
            lines.Add($"💬 [Community & support server]({invite})");
        }

        if (ConfiguredHttpsUrl("Donations:BuyMeACoffeeUrl") is { } donate)
        {
            lines.Add($"☕ [Support the project]({donate})");
        }

        lines.Add("🛠️ [Source on GitHub](https://github.com/JJWren/CalCrony)");

        var embed = new EmbedBuilder()
            .WithTitle("CalCrony — events & calendars for Discord")
            .WithDescription(string.Join("\n", lines))
            .WithFooter($"CalCrony {Version}")
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }

    /// <summary>The configured value as an absolute https URL, or null to omit its line.</summary>
    private string? ConfiguredHttpsUrl(string key) =>
        Uri.TryCreate(configuration[key], UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : null;
}
