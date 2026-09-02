using CalCrony.Contracts;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot;

/// <summary>Requires the bot to be a member of the server the command was used in — the condition
/// every guild command's <c>Context.Guild</c> dereference silently assumes. Discord.Net's
/// <see cref="RequireContextAttribute"/> is not enough: it only checks that the interaction carries
/// a guild id, which a user-installed CalCrony (or a commands-only install) also does in servers the
/// bot never joined — there the library has no cached guild, resolves the caller as a plain user,
/// and leaves <c>Context.Guild</c> null (issue #163). The failure text tells the caller how to fix
/// it instead of leaving a bare "did not respond".</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireBotInGuildAttribute : PreconditionAttribute
{
    /// <inheritdoc />
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        var problem = Problem(
            botInGuild: context.Guild is not null,
            isDm: context.Interaction.IsDMInteraction,
            context.Interaction.ApplicationId);

        return Task.FromResult(problem is null
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(ErrorMessage ?? problem));
    }

    /// <summary>Why the command can't run here, or null when it can. Pure so the wording is
    /// directly testable.</summary>
    /// <param name="botInGuild">Whether the bot resolved the server — it is a member and the guild is cached.</param>
    /// <param name="isDm">Whether the interaction came from a DM rather than a server channel.</param>
    /// <param name="applicationId">The application the command belongs to, for the invite link.</param>
    /// <returns>The caller-facing reason, or null.</returns>
    public static string? Problem(bool botInGuild, bool isDm, ulong applicationId)
    {
        if (botInGuild)
        {
            return null;
        }

        return isDm
            ? "This command works inside a server, not in DMs — run it in a channel of a server that has CalCrony."
            : "CalCrony hasn't been added to this server, so it can't see its channels, roles, or events. "
              + $"A server admin can add it here: <{DiscordInvite.Url(applicationId)}>";
    }
}
