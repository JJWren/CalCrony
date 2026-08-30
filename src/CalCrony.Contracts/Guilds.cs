namespace CalCrony.Contracts;

/// <summary>Bot-reported presence change for one guild (join or leave).</summary>
/// <param name="Present">Whether the bot is now in the guild.</param>
/// <param name="Name">The guild's current Discord name; null leaves the stored snapshot untouched
/// (leaves send null — a departed guild keeps its last-known name).</param>
public record GuildPresenceRequest(bool Present, string? Name = null);

/// <summary>One guild in the bot's Ready-time reconcile: its id and current Discord name.</summary>
/// <param name="Id">The guild id.</param>
/// <param name="Name">The guild's current Discord name; null leaves any stored snapshot untouched.</param>
public record GuildSnapshotDto(long Id, string? Name = null);

/// <summary>The bot's complete current guild list, reported at Ready to reconcile presence
/// and refresh guild-name snapshots.</summary>
/// <param name="Guilds">Every guild the bot is currently in.</param>
public record SyncGuildPresenceRequest(IReadOnlyList<GuildSnapshotDto> Guilds);

/// <summary>Presence counts after a sync.</summary>
/// <param name="Present">Guilds now marked bot-present.</param>
/// <param name="Absent">Known guilds now marked bot-absent.</param>
public record SyncGuildPresenceResponse(int Present, int Absent);

/// <summary>A channel the API references somewhere (an event, a series, or a guild default),
/// for the bot's Ready-time channel-name reconcile.</summary>
/// <param name="GuildId">The guild the channel belongs to.</param>
/// <param name="ChannelId">The channel id.</param>
public record ReferencedChannelDto(long GuildId, long ChannelId);

/// <summary>The channels the API currently references and wants name snapshots for.</summary>
/// <param name="Channels">The referenced channels.</param>
public record ReferencedChannelsResponse(IReadOnlyList<ReferencedChannelDto> Channels);

/// <summary>One channel-name snapshot resolved by the bot.</summary>
/// <param name="ChannelId">The channel id.</param>
/// <param name="GuildId">The guild the channel belongs to.</param>
/// <param name="Name">The channel's current Discord name.</param>
public record ChannelSnapshotDto(long ChannelId, long GuildId, string Name);

/// <summary>Bulk channel-name upsert (bot Ready-time reconcile).</summary>
/// <param name="Channels">The snapshots to store.</param>
public record SyncChannelsRequest(IReadOnlyList<ChannelSnapshotDto> Channels);

/// <summary>A single channel rename observed by the bot. Updates an existing snapshot only —
/// renames of channels CalCrony has never referenced are ignored, keeping the table small.</summary>
/// <param name="Name">The channel's new Discord name.</param>
public record ChannelNameRequest(string Name);
