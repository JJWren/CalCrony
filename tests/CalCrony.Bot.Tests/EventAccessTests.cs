using System.Reflection;
using Discord;

namespace CalCrony.Bot.Tests;

/// <summary>Who may change an event from Discord: its creator, or a holder of Manage Server.
/// The members here are stand-ins that answer for their id and their permissions.</summary>
public class EventAccessTests
{
    private const long CreatorId = 501;

    [Fact]
    public void The_creator_may_change_the_event() =>
        Assert.True(EventAccess.CanManage(Member(501, manageServer: false), CreatorId));

    [Fact]
    public void A_holder_of_manage_server_may_change_anyones_event() =>
        Assert.True(EventAccess.CanManage(Member(502, manageServer: true), CreatorId));

    [Fact]
    public void Another_member_may_not() =>
        Assert.False(EventAccess.CanManage(Member(502, manageServer: false), CreatorId));

    [Fact]
    public void Outside_a_server_only_the_creator_may()
    {
        Assert.True(EventAccess.CanManage(User(501), CreatorId));
        Assert.False(EventAccess.CanManage(User(502), CreatorId));
    }

    [Fact]
    public void The_notify_command_refuses_before_it_calls_the_api()
    {
        // The command has no test that runs it against Discord, so its order is read from its
        // source: the check stands between finding the event and adding the notification.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CalCrony.Bot", "Modules", "NotifyModule.cs"));
        var found = source.IndexOf("EventFinder.FindSingleAsync(", StringComparison.Ordinal);
        var check = source.IndexOf("if (!EventAccess.CanManage(Context.User, ev.CreatorId))", StringComparison.Ordinal);
        var call = source.IndexOf("api.CreateNotificationAsync(", StringComparison.Ordinal);

        Assert.True(found >= 0 && check > found && call > check);
        var refusal = source[check..call];
        Assert.Contains("FollowupAsync(\"Only the event creator or a server manager can add a notification to this event.\", ephemeral: true);", refusal);
        Assert.Contains("return;", refusal);
    }

    private static IGuildUser Member(ulong id, bool manageServer)
    {
        var member = DispatchProxy.Create<IGuildUser, StandIn>();
        var standIn = (StandIn)(object)member;
        standIn.Id = id;
        standIn.Permissions = new GuildPermissions(manageGuild: manageServer);
        return member;
    }

    private static IUser User(ulong id)
    {
        var user = DispatchProxy.Create<IUser, StandIn>();
        ((StandIn)(object)user).Id = id;
        return user;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CalCrony.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("CalCrony.slnx was not found above the test's directory.");
    }

    /// <summary>Answers for a member's id and permissions; anything else asked of it is a mistake
    /// in the code under test.</summary>
    public class StandIn : DispatchProxy
    {
        public ulong Id { get; set; }

        public GuildPermissions Permissions { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_Id" => Id,
            "get_GuildPermissions" => Permissions,
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }
}
