using System.Reflection;
using CalCrony.Contracts;
using Discord;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot.Tests;

/// <summary>Stand-ins for what the delivery classes work with, since nothing here can reach
/// Discord: a channel that answers for its guild and keeps what is sent on it, a logger that
/// keeps what it is given, and deliveries as the API serves them.</summary>
public static class DeliveryStandIns
{
    public const long ChannelId = 4100;

    /// <summary>A stand-in for a channel of the given kind, belonging to the given guild.</summary>
    public static T Channel<T>(ulong? guildId)
        where T : class, IChannel
    {
        var channel = DispatchProxy.Create<T, ChannelStandIn>();
        ((ChannelStandIn)(object)channel).GuildId = guildId;
        return channel;
    }

    /// <summary>What was sent on a channel made by <see cref="Channel{T}"/>.</summary>
    public static IReadOnlyList<(string? Text, AllowedMentions? Mentions)> Sent(IChannel channel) =>
        ((ChannelStandIn)(object)channel).Sent;

    /// <summary>Channels that find this one channel, whatever id is asked for.</summary>
    public static DeliveryChannels Channels(IChannel? channel, ILogger? logger = null) =>
        new(_ => ValueTask.FromResult(channel), logger ?? new CollectingLogger());

    /// <summary>A delivery of the given kind for the stand-in channel, with this payload.</summary>
    public static DeliveryDto Delivery<TPayload>(DeliveryType type, TPayload payload) =>
        new(Guid.NewGuid(), type, ChannelId, System.Text.Json.JsonSerializer.Serialize(payload), DateTimeOffset.UnixEpoch);

    /// <summary>Answers for a channel's guild and keeps each message sent on it; anything else
    /// asked of it is a mistake in the code under test.</summary>
    public class ChannelStandIn : DispatchProxy
    {
        public ulong? GuildId { get; set; }

        public List<(string? Text, AllowedMentions? Mentions)> Sent { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_GuildId":
                    return GuildId ?? throw new InvalidOperationException("This channel has no guild.");
                case nameof(IMessageChannel.SendMessageAsync):
                    var parameters = targetMethod.GetParameters();
                    Sent.Add((
                        (string?)args![Array.FindIndex(parameters, p => p.Name == "text")],
                        (AllowedMentions?)args[Array.FindIndex(parameters, p => p.Name == "allowedMentions")]));
                    return Task.FromResult<IUserMessage>(null!);
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    /// <summary>Keeps every line it is given.</summary>
    public sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
