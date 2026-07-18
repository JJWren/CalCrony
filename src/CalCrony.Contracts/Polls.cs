namespace CalCrony.Contracts;

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

public record PollOptionDto(
    Guid Id, string Text, DateTimeOffset? SlotAtUtc, long? AddedByUserId, int SortOrder, int VoteCount)
{
    public long? SlotAtUnix => SlotAtUtc?.ToUnixTimeSeconds();
}

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

public record AddPollOptionRequest(long UserId, string Text);

public record ConvertPollRequest(long UserId, string? Title = null, int? DurationMinutes = null);

public record SetPollMessageRequest(long ChannelId, long MessageId);
