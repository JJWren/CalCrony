using CalCrony.Bot.Api;
using CalCrony.Bot.Modules;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Bot.Tests;

/// <summary>The /availability group as Discord.Net builds it for registration — no gateway
/// connection needed. Pins the shape Discord is asked to accept: the member-or-role picker, the
/// bounds Discord enforces before the command runs, and its description limits.</summary>
public class AvailabilityCommandShapeTests : IAsyncLifetime
{
    private const int DiscordDescriptionLimit = 100;

    private readonly DiscordSocketClient client = new();
    private InteractionService interactions = null!;

    public async Task InitializeAsync()
    {
        interactions = new InteractionService(client);
        var services = new ServiceCollection()
            .AddSingleton(new CalCronyApiClient(new HttpClient { BaseAddress = new Uri("http://localhost") }))
            .BuildServiceProvider();
        await interactions.AddModuleAsync(typeof(AvailabilityModule), services);
    }

    public async Task DisposeAsync()
    {
        interactions.Dispose();
        await client.DisposeAsync();
    }

    [Fact]
    public void Slots_takes_a_member_or_role_and_bounds_its_numbers()
    {
        var slots = Command("slots");

        Assert.Equal(["who", "when", "days", "duration"], slots.Parameters.Select(p => p.Name));

        var who = slots.Parameters[0];
        Assert.Equal(ApplicationCommandOptionType.Mentionable, who.DiscordOptionType);
        Assert.True(who.IsRequired);

        // The bounds come from the same constants the API validates against. Discord turns down
        // first-day text over the length limit — the one every date typed in words has — before
        // the command ever runs.
        var when = slots.Parameters[1];
        Assert.Equal(ApplicationCommandOptionType.String, when.DiscordOptionType);
        Assert.False(when.IsRequired);
        Assert.Equal(DateTextLimits.MaxLength, when.MaxLength);

        var days = slots.Parameters[2];
        Assert.Equal(ApplicationCommandOptionType.Integer, days.DiscordOptionType);
        Assert.False(days.IsRequired);
        Assert.Equal(FreeSlotLimits.MinDays, days.MinValue);
        Assert.Equal(FreeSlotLimits.MaxDays, days.MaxValue);

        var duration = slots.Parameters[3];
        Assert.Equal(ApplicationCommandOptionType.Integer, duration.DiscordOptionType);
        Assert.False(duration.IsRequired);
        Assert.Equal(FreeSlotLimits.MinMinimumMinutes, duration.MinValue);
        Assert.Equal(FreeSlotLimits.MaxMinimumMinutes, duration.MaxValue);
    }

    [Fact]
    public void The_option_descriptions_state_the_limits_and_defaults_the_code_applies()
    {
        // An attribute takes literals only, so the numbers in a description are typed by hand.
        // This is what notices when a limit or a default moves and the description does not.
        var slots = Command("slots");

        Assert.Contains($"{FreeSlotLimits.MinDays}-{FreeSlotLimits.MaxDays}", slots.Parameters[2].Description);
        Assert.Contains($"default {FreeSlotLimits.DefaultDays}", slots.Parameters[2].Description);
        Assert.Contains($"default {FreeSlotLimits.DefaultMinimumMinutes}", slots.Parameters[3].Description);
    }

    [Fact]
    public void The_descriptions_promise_no_more_than_the_rules_for_who_allow()
    {
        // The picker offers every member, but FreeSlotTarget lists a single member only for the
        // caller themself. What Discord shows beside the command and the option has to say so,
        // and to change when that rule does.
        var slots = Command("slots");

        Assert.Equal("Find free time slots for yourself or a role over one or more days", slots.Description);
        Assert.Equal("Yourself or a role", slots.Parameters[0].Description);
    }

    [Fact]
    public void Role_and_event_keep_the_options_they_had()
    {
        var role = Command("role");
        Assert.Equal(["role", "when", "duration"], role.Parameters.Select(p => p.Name));
        Assert.Equal(ApplicationCommandOptionType.Role, role.Parameters[0].DiscordOptionType);
        Assert.Equal([true, true, false], role.Parameters.Select(p => p.IsRequired));

        var ev = Command("event");
        Assert.Equal(["name"], ev.Parameters.Select(p => p.Name));
        Assert.True(ev.Parameters[0].IsAutocomplete);
    }

    [Fact]
    public void The_group_has_exactly_its_three_subcommands_within_discords_description_limit()
    {
        Assert.Equal(["event", "role", "slots"], interactions.SlashCommands.Select(c => c.Name).Order());

        Assert.All(interactions.SlashCommands, command =>
        {
            Assert.Equal("availability", command.Module.SlashGroupName);
            Assert.InRange(command.Description.Length, 1, DiscordDescriptionLimit);
            Assert.All(command.Parameters, parameter =>
                Assert.InRange(parameter.Description.Length, 1, DiscordDescriptionLimit));
        });
    }

    private SlashCommandInfo Command(string name) => interactions.SlashCommands.Single(c => c.Name == name);
}
