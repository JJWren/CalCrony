using System.Reflection;
using CalCrony.Bot.Modules;
using Discord;
using Discord.Interactions;

namespace CalCrony.Bot.Tests;

/// <summary>Pins where each slash-command module may run (issue #163). Server modules are
/// registered for guild installs in guild channels only — so Discord stops offering them from a
/// user-installed CalCrony in servers the bot never joined — and carry the precondition that
/// answers with the invite link if such an interaction still arrives. The user-scoped modules stay
/// reachable from DMs and user installs. Discord.Net's [RequireContext(Guild)] must not come back:
/// it passes on a bare guild id, which is exactly what let the null Context.Guild through.</summary>
public class ModuleContextTests
{
    private static readonly Type[] UserScoped = [typeof(CalendarModule), typeof(HelpModule), typeof(TimestampModule)];

    /// <summary>Every module that contributes at least one slash command, by name.</summary>
    private static IEnumerable<Type> Modules() =>
        typeof(DiscordBotService).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IInteractionModuleBase).IsAssignableFrom(t))
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttribute<SlashCommandAttribute>() is not null))
            .OrderBy(t => t.Name);

    public static TheoryData<Type> SlashModules()
    {
        var data = new TheoryData<Type>();
        foreach (var module in Modules())
        {
            data.Add(module);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SlashModules))]
    public void Every_slash_module_declares_where_it_runs(Type module)
    {
        Assert.NotNull(module.GetCustomAttribute<CommandContextTypeAttribute>());
        Assert.NotNull(module.GetCustomAttribute<IntegrationTypeAttribute>());
        Assert.Null(module.GetCustomAttribute<RequireContextAttribute>());
    }

    [Theory]
    [MemberData(nameof(SlashModules))]
    public void Server_modules_are_guild_install_only_and_guarded(Type module)
    {
        if (UserScoped.Contains(module))
        {
            return;
        }

        Assert.Equal([InteractionContextType.Guild], module.GetCustomAttribute<CommandContextTypeAttribute>()!.ContextTypes);
        Assert.Equal([ApplicationIntegrationType.GuildInstall], module.GetCustomAttribute<IntegrationTypeAttribute>()!.IntegrationTypes);
        Assert.NotNull(module.GetCustomAttribute<RequireBotInGuildAttribute>());
    }

    [Theory]
    [MemberData(nameof(SlashModules))]
    public void User_scoped_modules_stay_reachable_from_dms_and_user_installs(Type module)
    {
        if (!UserScoped.Contains(module))
        {
            return;
        }

        Assert.Contains(InteractionContextType.BotDm, module.GetCustomAttribute<CommandContextTypeAttribute>()!.ContextTypes);
        Assert.Contains(ApplicationIntegrationType.UserInstall, module.GetCustomAttribute<IntegrationTypeAttribute>()!.IntegrationTypes);
        Assert.Null(module.GetCustomAttribute<RequireBotInGuildAttribute>());
    }

    [Fact]
    public void The_user_scoped_set_is_exactly_the_modules_that_never_touch_the_guild()
    {
        // A module that joins this set must not dereference Context.Guild anywhere; /timestamp
        // null-checks it, /help and /calendar never read it.
        Assert.Equal(
            ["CalendarModule", "HelpModule", "TimestampModule"],
            Modules().Where(UserScoped.Contains).Select(t => t.Name));
    }
}
