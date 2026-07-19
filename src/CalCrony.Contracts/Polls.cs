namespace CalCrony.Contracts;

/// <summary>Poll lifecycle: open for voting or closed (manually, by deadline, or before conversion).</summary>
public enum PollStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>Request to create a poll. For time polls each option is natural-language datetime
/// text, parsed server-side in the creator's zone. Web callers' CreatorId/ChannelId are
/// overridden server-side (identity from the session; channel from the guild default).</summary>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="Question">The poll question.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="Options">The poll options (option texts on create).</param>
/// <param name="IsTimePoll">True when options are candidate time slots.</param>
/// <param name="SingleVote">When true, each voter gets exactly one choice.</param>
/// <param name="Anonymous">When true, embeds show counts without voter names.</param>
/// <param name="AllowUserOptions">When true, voters may add options.</param>
/// <param name="ClosesText">Optional natural-language close deadline.</param>
public record CreatePollRequest(
    long CreatorId,
    string Question,
    long ChannelId,
    IReadOnlyList<string> Options,
    bool IsTimePoll = false,
    bool SingleVote = false,
    bool Anonymous = false,
    bool AllowUserOptions = false,
    string? ClosesText = null);

/// <summary>One poll choice; SlotAtUtc is set for time polls, AddedByUserId for voter-added options.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Text">The option text (a natural-language slot on time polls).</param>
/// <param name="SlotAtUtc">The resolved time slot (time polls only).</param>
/// <param name="AddedByUserId">The adding voter's id; null for creator-supplied options.</param>
/// <param name="SortOrder">Display ordering index.</param>
/// <param name="VoteCount">Number of votes on the option.</param>
public record PollOptionDto(
    Guid Id, string Text, DateTimeOffset? SlotAtUtc, long? AddedByUserId, int SortOrder, int VoteCount)
{
    public long? SlotAtUnix => SlotAtUtc?.ToUnixTimeSeconds();
}

/// <summary>One user's vote for one option (multi-vote polls have several rows per user).</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="OptionId">The RSVP/poll option id.</param>
public record PollVoteDto(long UserId, Guid OptionId);

/// <summary>For anonymous polls, web callers receive only their own rows in Votes (counts stay
/// complete via each option's VoteCount); the bot receives everything and hides names itself.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="Question">The poll question.</param>
/// <param name="IsTimePoll">True when options are candidate time slots.</param>
/// <param name="SingleVote">When true, each voter gets exactly one choice.</param>
/// <param name="Anonymous">When true, embeds show counts without voter names.</param>
/// <param name="AllowUserOptions">When true, voters may add options.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id.</param>
/// <param name="Status">The poll lifecycle status.</param>
/// <param name="ClosesAtUtc">When voting closes, when set.</param>
/// <param name="ClosedAtUtc">When the poll closed.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="ConvertedEventId">The created event id once converted.</param>
/// <param name="Options">The poll options (option texts on create).</param>
/// <param name="Votes">The vote rows (caller-shaped on anonymous polls).</param>
public record PollDto(
    Guid Id,
    long GuildId,
    long CreatorId,
    string Question,
    bool IsTimePoll,
    bool SingleVote,
    bool Anonymous,
    bool AllowUserOptions,
    long ChannelId,
    long? MessageId,
    PollStatus Status,
    DateTimeOffset? ClosesAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string TimeZone,
    Guid? ConvertedEventId,
    IReadOnlyList<PollOptionDto> Options,
    IReadOnlyList<PollVoteDto> Votes)
{
    public long? ClosesAtUnix => ClosesAtUtc?.ToUnixTimeSeconds();

    public long? ClosedAtUnix => ClosedAtUtc?.ToUnixTimeSeconds();
}

/// <summary>Atomic set-replacement of one user's votes; empty clears them.</summary>
/// <param name="OptionIds">The full vote set to store.</param>
public record PutPollVotesRequest(IReadOnlyList<Guid> OptionIds);

/// <summary>Voter-added option; Text is a natural-language datetime for time polls.</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="Text">The option text (a natural-language slot on time polls).</param>
public record AddPollOptionRequest(long UserId, string Text);

/// <summary>Converts a closed time poll's winning slot into an event (title defaults to the question, truncated).</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="Title">The event title.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
public record ConvertPollRequest(long UserId, string? Title = null, int? DurationMinutes = null);

/// <summary>Records where the bot posted a poll's embed (bot-only).</summary>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id.</param>
public record SetPollMessageRequest(long ChannelId, long MessageId);
