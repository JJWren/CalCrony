namespace CalCrony.Contracts;

/// <summary>Bot-reported presence change for one guild (join or leave).</summary>
/// <param name="Present">Whether the bot is now in the guild.</param>
public record GuildPresenceRequest(bool Present);

/// <summary>The bot's complete current guild list, reported at Ready to reconcile presence.</summary>
/// <param name="GuildIds">Every guild id the bot is currently in.</param>
public record SyncGuildPresenceRequest(IReadOnlyList<long> GuildIds);

/// <summary>Presence counts after a sync.</summary>
/// <param name="Present">Guilds now marked bot-present.</param>
/// <param name="Absent">Known guilds now marked bot-absent.</param>
public record SyncGuildPresenceResponse(int Present, int Absent);
