using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CalCrony.Bot;

/// <summary>Field mapping for a mirrored Discord scheduled event (External type).</summary>
/// <param name="Name">The scheduled event name (title, capped at 100).</param>
/// <param name="Description">The description with the RSVP pointer line, capped at 1000.</param>
/// <param name="StartTime">The scheduled start.</param>
/// <param name="EndTime">The scheduled end (External events require one).</param>
/// <param name="Location">The external location text, capped at 100.</param>
public sealed record NativeEventSpec(
    string Name, string Description, DateTimeOffset StartTime, DateTimeOffset EndTime, string Location);

/// <summary>Mirrors CalCrony events into Discord's native Guild Scheduled Events. Strictly
/// best-effort: every operation catches and logs instead of throwing, so mirroring can never fail
/// a delivery or a slash reply. The guild's MirrorNativeEvents flag gates CREATION only — an
/// event that already has a native twin keeps getting updates/start/complete/delete regardless,
/// so toggling the setting off leaves existing native events accurate until they finish.</summary>
public class NativeEventMirror(DiscordSocketClient client, CalCronyApiClient api, ILogger<NativeEventMirror> logger)
{
    private const int NameLimit = 100;
    private const int DescriptionLimit = 1000;
    private const int LocationLimit = 100;

    /// <summary>Pure field mapping, public for direct testing.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The mapped native-event fields.</returns>
    public static NativeEventSpec BuildSpec(EventDto ev)
    {
        // The pointer line stops people mistaking Discord's "Interested" bell for an RSVP.
        var pointer = $"\n\nRSVP on the event message in <#{ev.ChannelId}>.";
        var body = ev.Description ?? "";
        var bodyBudget = DescriptionLimit - pointer.Length;
        if (body.Length > bodyBudget)
        {
            body = body[..bodyBudget];
        }

        var location = string.IsNullOrWhiteSpace(ev.Location) ? "#event-channel" : ev.Location;
        return new NativeEventSpec(
            Truncate(ev.Title, NameLimit),
            body + pointer,
            ev.StartsAtUtc,
            ev.StartsAtUtc.AddMinutes(ev.DurationMinutes ?? 60),
            Truncate(location, LocationLimit));
    }

    /// <summary>Creates or updates the event's native twin. Creation happens only when the guild's
    /// mirror flag is on, the event is Scheduled, and the start is still in the future (Discord
    /// rejects past starts); updates happen whenever a native id exists. A native event that was
    /// deleted Discord-side gets its stale id cleared and, when the flag allows, recreated.</summary>
    /// <param name="ev">The event (must carry NativeEventId when one was recorded).</param>
    public async Task TryUpsertAsync(EventDto ev)
    {
        try
        {
            if (client.GetGuild((ulong)ev.GuildId) is not SocketGuild guild)
            {
                return;
            }

            var spec = BuildSpec(ev);
            if (ev.NativeEventId is long nativeId)
            {
                var existing = await guild.GetEventAsync((ulong)nativeId);
                if (existing is not null)
                {
                    var startStillEditable = ev.Status == EventStatus.Scheduled && spec.StartTime > DateTimeOffset.UtcNow;
                    await existing.ModifyAsync(props =>
                    {
                        props.Name = spec.Name;
                        props.Description = spec.Description;
                        props.Location = spec.Location;
                        props.EndTime = spec.EndTime;
                        if (startStillEditable)
                        {
                            props.StartTime = spec.StartTime;
                        }
                    });
                    return;
                }

                // Deleted Discord-side: clear the stale id, then fall through to maybe recreate.
                await api.SetNativeEventAsync(ev.Id, new SetNativeEventRequest(null));
            }

            if (ev.Status != EventStatus.Scheduled || ev.StartsAtUtc <= DateTimeOffset.UtcNow)
            {
                return;
            }

            var settings = await api.GetGuildSettingsAsync(ev.GuildId);
            if (settings.Value?.MirrorNativeEvents != true)
            {
                return;
            }

            var created = await guild.CreateEventAsync(
                spec.Name,
                spec.StartTime,
                GuildScheduledEventType.External,
                description: spec.Description,
                endTime: spec.EndTime,
                location: spec.Location);
            await api.SetNativeEventAsync(ev.Id, new SetNativeEventRequest((long)created.Id));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native-event upsert failed for event {EventId} in guild {GuildId}.", ev.Id, ev.GuildId);
        }
    }

    /// <summary>Deletes the native twin; null id or already-gone counts as done.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="nativeEventId">The mirrored scheduled-event id, when mirrored.</param>
    public async Task TryDeleteAsync(long guildId, long? nativeEventId)
    {
        if (nativeEventId is not long nativeId)
        {
            return;
        }

        try
        {
            if (client.GetGuild((ulong)guildId) is not SocketGuild guild)
            {
                return;
            }

            var existing = await guild.GetEventAsync((ulong)nativeId);
            if (existing is not null)
            {
                await existing.DeleteAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native-event delete failed for {NativeEventId} in guild {GuildId}.", nativeId, guildId);
        }
    }

    /// <summary>Flips the native twin to active when the event starts. Scheduled→Active is the
    /// only legal start transition, so any other state is left alone.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="nativeEventId">The mirrored scheduled-event id.</param>
    public async Task TryStartAsync(long guildId, long nativeEventId)
    {
        try
        {
            if (client.GetGuild((ulong)guildId) is not SocketGuild guild)
            {
                return;
            }

            var existing = await guild.GetEventAsync((ulong)nativeEventId);
            if (existing is { Status: GuildScheduledEventStatus.Scheduled })
            {
                await existing.StartAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native-event start failed for {NativeEventId} in guild {GuildId}.", nativeEventId, guildId);
        }
    }

    /// <summary>Completes the native twin after the event ends. A twin that never went active is
    /// deleted instead (Scheduled→Completed is an illegal transition); anything else is done.</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="nativeEventId">The mirrored scheduled-event id.</param>
    public async Task TryCompleteAsync(long guildId, long nativeEventId)
    {
        try
        {
            if (client.GetGuild((ulong)guildId) is not SocketGuild guild)
            {
                return;
            }

            var existing = await guild.GetEventAsync((ulong)nativeEventId);
            switch (existing?.Status)
            {
                case GuildScheduledEventStatus.Active:
                    await existing.EndAsync();
                    break;
                case GuildScheduledEventStatus.Scheduled:
                    await existing.DeleteAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native-event complete failed for {NativeEventId} in guild {GuildId}.", nativeEventId, guildId);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
