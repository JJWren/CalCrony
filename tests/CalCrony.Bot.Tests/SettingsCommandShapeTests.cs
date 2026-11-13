using System.Reflection;
using CalCrony.Bot.Api;
using CalCrony.Bot.Modules;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Bot.Tests;

/// <summary>The /settings group as Discord.Net builds it for registration — no gateway connection
/// needed. Pins that the group keeps its subcommands and gains the nested <c>available-hours</c>
/// group, the first nested group in the bot, with the option types, the limits Discord enforces
/// before a command runs, and its description limits.</summary>
public class SettingsCommandShapeTests : IAsyncLifetime
{
    private const int DiscordDescriptionLimit = 100;
    private const int DiscordChoiceLimit = 25;

    private readonly DiscordSocketClient client = new();
    private InteractionService interactions = null!;

    public async Task InitializeAsync()
    {
        interactions = new InteractionService(client);
        var services = new ServiceCollection()
            .AddSingleton(new CalCronyApiClient(new HttpClient { BaseAddress = new Uri("http://localhost") }))
            .BuildServiceProvider();
        await interactions.AddModuleAsync(typeof(SettingsModule), services);
    }

    public async Task DisposeAsync()
    {
        interactions.Dispose();
        await client.DisposeAsync();
    }

    [Fact]
    public void The_group_keeps_the_subcommands_it_had()
    {
        Assert.Equal(
            ["default-channel", "dm-reminders", "native-events", "public-calendar", "server-timezone", "timezone", "view"],
            interactions.SlashCommands.Where(c => !c.Module.IsSubModule).Select(c => c.Name).Order());

        Assert.All(
            interactions.SlashCommands.Where(c => !c.Module.IsSubModule),
            command => Assert.Equal("settings", command.Module.SlashGroupName));
    }

    [Fact]
    public void Available_hours_is_a_group_of_three_nested_under_settings()
    {
        var hours = HoursCommands();

        Assert.Equal(["off", "reset", "set"], hours.Select(c => c.Name).Order());
        Assert.All(hours, command =>
        {
            Assert.Equal("available-hours", command.Module.SlashGroupName);
            Assert.Equal("settings", command.Module.Parent.SlashGroupName);
            Assert.Null(command.Module.Parent.Parent);
        });
        Assert.Equal(
            "The hours you're usually available; free slots are cut from them", hours[0].Module.Description);
    }

    [Fact]
    public void Set_takes_the_days_and_two_times_with_suggestions_and_a_length_limit()
    {
        var set = Hours("set");

        Assert.Equal(["days", "from", "to"], set.Parameters.Select(p => p.Name));
        Assert.All(set.Parameters, parameter => Assert.True(parameter.IsRequired));
        Assert.Equal(typeof(AvailableHoursDays), set.Parameters[0].ParameterType);

        // Discord turns longer text down before the command runs; what arrives is read by
        // TimeOfDayText, picked from the suggestions or typed.
        foreach (var time in set.Parameters.Skip(1))
        {
            Assert.Equal(ApplicationCommandOptionType.String, time.DiscordOptionType);
            Assert.True(time.IsAutocomplete);
            Assert.Equal(TimeOfDayText.MaxInputLength, time.MaxLength);
        }
    }

    [Fact]
    public void Off_takes_the_days_and_reset_takes_nothing()
    {
        var off = Hours("off");
        Assert.Equal(["days"], off.Parameters.Select(p => p.Name));
        Assert.True(off.Parameters[0].IsRequired);
        Assert.Equal(typeof(AvailableHoursDays), off.Parameters[0].ParameterType);

        Assert.Empty(Hours("reset").Parameters);
    }

    [Fact]
    public void The_days_are_ten_named_choices_within_what_discord_allows()
    {
        var labels = typeof(AvailableHoursDays)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetCustomAttribute<ChoiceDisplayAttribute>()?.Name)
            .ToList();

        Assert.Equal(
            [
                "every day", "weekdays (Mon–Fri)", "weekends (Sat, Sun)",
                "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
            ],
            labels);
        Assert.InRange(labels.Count, 1, DiscordChoiceLimit);
        Assert.All(labels, label => Assert.InRange(label!.Length, 1, DiscordDescriptionLimit));
    }

    [Fact]
    public void The_reset_description_states_the_default_hours_the_code_applies()
    {
        // An attribute takes literals only, so the two times in the description are typed by hand.
        // This is what notices when the default moves and the description does not.
        var description = Hours("reset").Description;

        Assert.Contains(TimeOfDayText.Format(AvailableHoursLimits.DefaultStartMinute), description);
        Assert.Contains(TimeOfDayText.Format(AvailableHoursLimits.DefaultEndMinute), description);
        Assert.Contains(AvailableHoursText.DefaultRange, description);
    }

    [Fact]
    public void Every_description_fits_discords_limit()
    {
        Assert.All(interactions.SlashCommands, command =>
        {
            Assert.InRange(command.Description.Length, 1, DiscordDescriptionLimit);
            Assert.InRange(command.Module.Description.Length, 1, DiscordDescriptionLimit);
            Assert.All(command.Parameters, parameter =>
                Assert.InRange(parameter.Description.Length, 1, DiscordDescriptionLimit));
        });
    }

    [Fact]
    public void No_hours_command_takes_a_member()
    {
        // Each acts on whoever runs it: nobody sets or reads another member's hours.
        Assert.All(HoursCommands().SelectMany(command => command.Parameters), parameter =>
            Assert.DoesNotContain(
                parameter.DiscordOptionType,
                new ApplicationCommandOptionType?[]
                {
                    ApplicationCommandOptionType.User, ApplicationCommandOptionType.Mentionable, ApplicationCommandOptionType.Role,
                }));
    }

    private List<SlashCommandInfo> HoursCommands() => [.. interactions.SlashCommands.Where(c => c.Module.IsSubModule)];

    private SlashCommandInfo Hours(string name) => HoursCommands().Single(c => c.Name == name);
}
