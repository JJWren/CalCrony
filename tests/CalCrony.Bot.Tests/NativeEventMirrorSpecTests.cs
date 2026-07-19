using CalCrony.Bot;
using CalCrony.Contracts;

namespace CalCrony.Bot.Tests;

public class NativeEventMirrorSpecTests
{
    private const long ChannelId = 4242;

    [Fact]
    public void Name_caps_at_discords_100()
    {
        var spec = NativeEventMirror.BuildSpec(Sample(title: new string('t', 150)));
        Assert.Equal(100, spec.Name.Length);
    }

    [Fact]
    public void Description_reserves_room_for_the_rsvp_pointer_within_1000()
    {
        var spec = NativeEventMirror.BuildSpec(Sample(description: new string('d', 1500)));

        Assert.True(spec.Description.Length <= 1000);
        Assert.EndsWith($"RSVP on the event message in <#{ChannelId}>.", spec.Description);
    }

    [Fact]
    public void Pointer_renders_the_channel_mention_even_without_a_description()
    {
        var spec = NativeEventMirror.BuildSpec(Sample(description: null));
        Assert.Contains($"<#{ChannelId}>", spec.Description);
    }

    [Fact]
    public void End_time_uses_duration_and_the_60_minute_default()
    {
        var start = DateTimeOffset.UtcNow.AddHours(2);

        Assert.Equal(start.AddMinutes(90), NativeEventMirror.BuildSpec(Sample(startsAt: start, duration: 90)).EndTime);
        Assert.Equal(start.AddMinutes(60), NativeEventMirror.BuildSpec(Sample(startsAt: start, duration: null)).EndTime);
    }

    [Fact]
    public void Location_falls_back_and_caps_at_100()
    {
        Assert.Equal("#event-channel", NativeEventMirror.BuildSpec(Sample(location: null)).Location);
        Assert.Equal("#event-channel", NativeEventMirror.BuildSpec(Sample(location: "  ")).Location);
        Assert.Equal(100, NativeEventMirror.BuildSpec(Sample(location: new string('l', 140))).Location.Length);
    }

    private static EventDto Sample(
        string title = "Raid Night", string? description = "Bring snacks",
        DateTimeOffset? startsAt = null, int? duration = 60, string? location = "Voice chat") => new(
        Guid.NewGuid(), 1, 2, title, description, startsAt ?? DateTimeOffset.UtcNow.AddHours(3),
        "UTC", duration, ChannelId, null, location, null, EventStatus.Scheduled, [], []);
}
