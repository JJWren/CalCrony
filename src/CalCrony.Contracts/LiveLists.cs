namespace CalCrony.Contracts;

/// <summary>A live list: a persistent upcoming-events embed the bot keeps current in one channel.
/// One per channel; a manually deleted message means the list is gone (the bot clears the record
/// on its next sync instead of reposting).</summary>
/// <param name="Id">The unique id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="ChannelId">The Discord channel id the list message lives in.</param>
/// <param name="MessageId">The Discord message id of the list embed.</param>
/// <param name="Limit">Maximum number of events the list shows (1-25).</param>
/// <param name="CreatorId">The creating user's Discord id.</param>
public record LiveListDto(Guid Id, long GuildId, long ChannelId, long MessageId, int Limit, long CreatorId);

/// <summary>Registers a live list the bot just posted (bot-only; only the bot knows message ids).</summary>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="ChannelId">The Discord channel id the list message lives in.</param>
/// <param name="MessageId">The Discord message id of the list embed.</param>
/// <param name="Limit">Maximum number of events the list shows (clamped to 1-25).</param>
/// <param name="ChannelName">The channel's current Discord name, upserted as a name snapshot;
/// null skips the snapshot.</param>
public record CreateLiveListRequest(
    long CreatorId, long ChannelId, long MessageId, int Limit, string? ChannelName = null);
