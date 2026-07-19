using CalCrony.Bot;

namespace CalCrony.Bot.Tests;

public class EventThreadManagerSpecTests
{
    [Fact]
    public void Thread_name_is_the_title_capped_at_discord_limit()
    {
        Assert.Equal("Raid Night", EventThreadManager.BuildName("Raid Night"));

        var longTitle = new string('x', 128);
        var name = EventThreadManager.BuildName(longTitle);
        Assert.Equal(100, name.Length);
        Assert.Equal(longTitle[..100], name);
    }
}
