using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

/// <summary>Wording of the opt-in DM reminders (issue #123).</summary>
public class DmReminderTextTests
{
    private static DmEventReminderPayload Sample(bool isStart = false, string? message = "Bring dice", long? messageId = 777, string? guildName = "The Keep") =>
        new(42, Guid.NewGuid(), "Raid Night", 1_800_000_000, message, isStart, 1, 2, messageId, guildName);

    [Fact]
    public void Reminder_names_the_event_server_time_message_and_jump_link_and_always_says_how_to_stop()
    {
        var text = DmReminderText.Format(Sample());

        Assert.StartsWith("🔔 **Raid Night** in **The Keep** starts <t:1800000000:R> (<t:1800000000:F>).", text);
        Assert.Contains("\nBring dice", text);
        Assert.Contains("https://discord.com/channels/1/2/777", text);
        Assert.Contains("/settings dm-reminders enabled:false", text);
    }

    [Fact]
    public void Start_announcement_uses_the_starting_now_wording()
    {
        var text = DmReminderText.Format(Sample(isStart: true, message: null));

        Assert.StartsWith("🎉 **Raid Night** in **The Keep** is starting now!", text);
        Assert.DoesNotContain("starts <t:", text);
    }

    [Fact]
    public void Missing_snapshots_are_omitted_rather_than_rendered_as_placeholders()
    {
        var text = DmReminderText.Format(Sample(message: null, messageId: null, guildName: null));

        Assert.StartsWith("🔔 **Raid Night** starts", text);
        Assert.DoesNotContain(" in **", text);
        Assert.DoesNotContain("discord.com/channels", text);
    }
}
