using Discord.Interactions;

namespace CalCrony.Bot;

/// <summary>What the caller hears when a command fails after Discord already accepted it. Without
/// a reply, a failure before the first response shows "The application did not respond", and one
/// after a defer sits on "CalCrony is thinking..." until the token expires (issue #163). Pure and
/// static so the wording is testable; nothing here logs — the interaction service already reports
/// the exception through its Log event.</summary>
public static class InteractionFailureReply
{
    /// <summary>The apology for an unexpected exception. It deliberately doesn't claim nothing
    /// changed: a command can fail after its API call succeeded (e.g. /create after the event was
    /// stored but before the embed was posted).</summary>
    public const string Unexpected =
        "❌ Something went wrong on CalCrony's side. Please try again in a moment, or run /help for support links.";

    /// <summary>The command's name for the log, or a placeholder when the interaction service
    /// reported a failed lookup with no command (a stale registration Discord still offers).</summary>
    /// <param name="command">The command that ran, or null.</param>
    /// <returns>The log label.</returns>
    public static string Describe(ICommandInfo? command) => command?.Name ?? "unknown command";

    /// <summary>The ephemeral text for a failed result, or null when there is nothing to say.</summary>
    /// <param name="result">The result the interaction service reported.</param>
    /// <returns>The reply text, or null.</returns>
    public static string? For(IResult result) => result switch
    {
        { IsSuccess: true } => null,
        // Preconditions and argument checks phrase their reason for the caller.
        {
            Error: InteractionCommandError.UnmetPrecondition
                or InteractionCommandError.ParseFailed
                or InteractionCommandError.ConvertFailed
                or InteractionCommandError.BadArgs
        } => $"❌ {result.ErrorReason}",
        { Error: InteractionCommandError.Exception or InteractionCommandError.Unsuccessful } => Unexpected,
        // UnknownCommand: not ours to answer — a stale registration Discord still offers.
        _ => null,
    };
}
