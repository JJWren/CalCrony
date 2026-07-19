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
public record PollOptionDto(
    Guid Id, string Text, DateTimeOffset? SlotAtUtc, long? AddedByUserId, int SortOrder, int VoteCount)
{
    public long? SlotAtUnix => SlotAtUtc?.ToUnixTimeSeconds();
}

/// <summary>One user's vote for one option (multi-vote polls have several rows per user).</summary>
public record PollVoteDto(long UserId, Guid OptionId);

/// <summary>For anonymous polls, web callers receive only their own rows in Votes (counts stay
/// complete via each option's VoteCount); the bot receives everything and hides names itself.</summary>
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
public record PutPollVotesRequest(IReadOnlyList<Guid> OptionIds);

/// <summary>Voter-added option; Text is a natural-language datetime for time polls.</summary>
public record AddPollOptionRequest(long UserId, string Text);

/// <summary>Converts a closed time poll's winning slot into an event (title defaults to the question, truncated).</summary>
public record ConvertPollRequest(long UserId, string? Title = null, int? DurationMinutes = null);

/// <summary>Records where the bot posted a poll's embed (bot-only).</summary>
public record SetPollMessageRequest(long ChannelId, long MessageId);
