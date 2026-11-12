using System.Reflection;
using CalCrony.Bot.Modules;
using CalCrony.Contracts;
using Discord.Interactions;

namespace CalCrony.Bot.Tests;

/// <summary>Every slash-command option that is read as a date or time in words carries the length
/// limit the API's parser applies (<see cref="DateTextLimits.MaxLength"/>), so Discord turns
/// longer text down before the command runs. A new option of that kind belongs in the list.
/// <c>/poll time</c>'s <c>slots</c> is not in it: that option is a comma-separated list of times,
/// and the API caps each entry.</summary>
public class DateOptionLimitTests
{
    public static TheoryData<Type, string, string> DateOptions() => new()
    {
        { typeof(AvailabilityModule), "role", "when" },
        { typeof(AvailabilityModule), "slots", "when" },
        { typeof(EventModule), "create", "when" },
        { typeof(EventModule), "create", "repeat-until" },
        { typeof(EventModule), "create", "rsvp-close" },
        { typeof(EventModule), "edit", "when" },
        { typeof(EventModule), "edit", "rsvp-close" },
        { typeof(PollModule), "create", "closes" },
        { typeof(PollModule), "time", "closes" },
        { typeof(ReminderModule), "remind", "when" },
        { typeof(SeriesModule), "edit", "until" },
        { typeof(TimestampModule), "timestamp", "when" },
    };

    [Theory]
    [MemberData(nameof(DateOptions))]
    public void Date_options_carry_the_shared_length_limit(Type module, string command, string option)
    {
        var method = Assert.Single(
            module.GetMethods(), m => m.GetCustomAttribute<SlashCommandAttribute>()?.Name == command);
        var parameter = Assert.Single(method.GetParameters(), p => OptionName(p) == option);

        Assert.Equal(typeof(string), parameter.ParameterType);
        Assert.Equal(DateTextLimits.MaxLength, parameter.GetCustomAttribute<MaxLengthAttribute>()?.Length);
    }

    /// <summary>The option name Discord sees: the one [Summary] gives, else the parameter's own.</summary>
    /// <param name="parameter">A slash-command method parameter.</param>
    /// <returns>The option name.</returns>
    private static string OptionName(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<SummaryAttribute>()?.Name ?? parameter.Name!;
}
