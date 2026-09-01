using CalCrony.Bot;

namespace CalCrony.Bot.Tests;

/// <summary>The pure half of role-restricted signup on the bot side: parsing the restrict-to
/// mention list, the pre-checks, and the live check a button click runs before calling the API.</summary>
public class RoleRestrictionSpecTests
{
    [Fact]
    public void Parses_one_or_more_mentions_separated_by_spaces_or_commas()
    {
        Assert.True(RoleRestrictionSpec.TryParseMentions("<@&11>", out var one, out var error));
        Assert.Null(error);
        Assert.Equal([11L], one);

        Assert.True(RoleRestrictionSpec.TryParseMentions("<@&11> <@&22>, <@&33>", out var several, out _));
        Assert.Equal([11L, 22L, 33L], several);

        // Glued mentions (what a mobile client can produce) and repeats both read.
        Assert.True(RoleRestrictionSpec.TryParseMentions("<@&11><@&22> <@&11>", out var glued, out _));
        Assert.Equal([11L, 22L], glued);
    }

    [Fact]
    public void Text_that_never_became_a_mention_is_an_error_not_a_silent_nobody()
    {
        Assert.False(RoleRestrictionSpec.TryParseMentions("<@&11> raiders", out _, out var error));
        Assert.Contains("raiders", error);

        Assert.False(RoleRestrictionSpec.TryParseMentions("", out _, out var empty));
        Assert.Contains("at least one role", empty);
    }

    [Fact]
    public void More_than_the_cap_is_rejected_before_the_api_sees_it()
    {
        var six = string.Join(' ', Enumerable.Range(1, RoleRestrictionSpec.MaxRoles + 1).Select(i => $"<@&{i}>"));

        Assert.False(RoleRestrictionSpec.TryParseMentions(six, out var roles, out var error));
        Assert.Contains($"at most {RoleRestrictionSpec.MaxRoles}", error);
        Assert.Empty(roles);
    }

    [Fact]
    public void Prechecks_refuse_a_missing_role_and_everyone_only()
    {
        Assert.Contains("isn't a role in this server", RoleRestrictionSpec.Validate(5, exists: false, isEveryone: false));
        Assert.Contains("@everyone", RoleRestrictionSpec.Validate(5, exists: true, isEveryone: true));
        // No hierarchy or Manage Roles check: the bot never grants a restriction role.
        Assert.Null(RoleRestrictionSpec.Validate(5, exists: true, isEveryone: false));
    }

    [Fact]
    public void The_live_check_admits_holders_bypassers_and_vacuous_restrictions_and_names_what_refused()
    {
        static bool Exists(long id) => id != 3; // role 3 was deleted Discord-side

        Assert.True(RoleRestrictionSpec.IsAllowed([], Exists, new HashSet<long>(), bypass: false, out _));
        Assert.True(RoleRestrictionSpec.IsAllowed([1], Exists, new HashSet<long>(), bypass: true, out _));
        Assert.True(RoleRestrictionSpec.IsAllowed([1, 2], Exists, new HashSet<long> { 2 }, bypass: false, out _));

        // A deleted role drops out; all deleted means no restriction at all.
        Assert.True(RoleRestrictionSpec.IsAllowed([3], Exists, new HashSet<long>(), bypass: false, out var vacuous));
        Assert.Empty(vacuous);

        Assert.False(RoleRestrictionSpec.IsAllowed([3, 1], Exists, new HashSet<long> { 9 }, bypass: false, out var effective));
        Assert.Equal([1L], effective);
        Assert.Equal("<@&1>", RoleRestrictionSpec.Mentions(effective));
    }
}
