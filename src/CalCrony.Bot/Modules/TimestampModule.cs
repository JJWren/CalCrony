using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord.Interactions;

namespace CalCrony.Bot.Modules;

/// <summary>/timestamp — natural language to Discord timestamp codes.</summary>
public class TimestampModule(CalCronyApiClient api) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Replies with the &lt;t:...&gt; variants for the parsed instant.</summary>
    [SlashCommand("timestamp", "Turn a natural-language time into Discord timestamp codes")]
    public async Task TimestampAsync(
        [Summary("when", "e.g. \"friday 8pm\" or \"in 3 hours\"")] string when)
    {
        await DeferAsync(ephemeral: true);

        var result = await api.ParseDateTimeAsync(new ParseDateTimeRequest(
            when,
            (long)Context.User.Id,
            Context.Guild is null ? null : (long)Context.Guild.Id));

        if (!result.Success || result.Value is null)
        {
            await FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        var unix = result.Value.Unix;
        var formats = new[] { "F", "f", "D", "d", "t", "T", "R" }
            .Select(f => $"`<t:{unix}:{f}>` → <t:{unix}:{f}>");
        await FollowupAsync(
            $"Parsed in **{result.Value.TimeZone}**:\n{string.Join("\n", formats)}",
            ephemeral: true);
    }
}
