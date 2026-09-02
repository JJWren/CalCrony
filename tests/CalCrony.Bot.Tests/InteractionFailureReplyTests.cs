using Discord.Interactions;

namespace CalCrony.Bot.Tests;

/// <summary>Which failures the caller hears about, and how (issue #163): a precondition's own
/// reason, a generic apology for exceptions, silence for successes and commands that aren't
/// ours.</summary>
public class InteractionFailureReplyTests
{
    [Fact]
    public void Successes_say_nothing()
    {
        Assert.Null(InteractionFailureReply.For(ExecuteResult.FromSuccess()));
    }

    [Fact]
    public void An_unmet_precondition_repeats_its_reason()
    {
        Assert.Equal("❌ Not here.", InteractionFailureReply.For(PreconditionResult.FromError("Not here.")));
    }

    [Fact]
    public void Argument_failures_repeat_their_reason()
    {
        Assert.Equal(
            "❌ Could not read that.",
            InteractionFailureReply.For(ExecuteResult.FromError(InteractionCommandError.ConvertFailed, "Could not read that.")));
    }

    [Fact]
    public void An_exception_gets_the_generic_apology_without_the_exception_text()
    {
        var reply = InteractionFailureReply.For(ExecuteResult.FromError(new NullReferenceException("secret internals")));

        Assert.Equal(InteractionFailureReply.Unexpected, reply);
        Assert.DoesNotContain("secret internals", reply);
    }

    [Fact]
    public void Unknown_commands_are_not_answered()
    {
        Assert.Null(InteractionFailureReply.For(ExecuteResult.FromError(InteractionCommandError.UnknownCommand, "stale")));
    }
}
