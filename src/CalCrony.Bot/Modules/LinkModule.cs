using CalCrony.Bot.Api;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;

namespace CalCrony.Bot.Modules;

/// <summary>/link — the server's ICS subscribe URL.</summary>
[RequireContext(ContextType.Guild)]
public class LinkModule(CalCronyApiClient api, IConfiguration configuration) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Replies with the tokenized feed URL, minting the token on first use.</summary>
    [SlashCommand("link", "Get this server's calendar feed URL (importable into Google/Apple/Outlook)")]
    public async Task LinkAsync()
    {
        await DeferAsync(ephemeral: true);

        var result = await api.GetOrCreateFeedTokenAsync((long)Context.Guild.Id);
        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        // PublicBaseUrl is the address calendar apps can reach; falls back to the bot's API address.
        var baseUrl = (configuration["Api:PublicBaseUrl"] ?? configuration["Api:BaseUrl"] ?? "").TrimEnd('/');
        await FollowupAsync(
            "📅 Subscribe to this server's events from any calendar app:\n" +
            $"```{baseUrl}{result.Value.Path}```\n" +
            "In Google Calendar: **Other calendars → + → From URL**. " +
            "Anyone with this URL can read this server's events.",
            ephemeral: true);
    }
}
