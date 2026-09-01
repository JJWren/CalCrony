namespace CalCrony.Contracts;

/// <summary>Where a logged action was initiated from. The bot relays slash commands and
/// component clicks; the web app acts with the signed-in user's session.</summary>
public enum ActionSource
{
    Discord = 0,
    Web = 1,
}

/// <summary>What a server action log entry records. Only user-initiated management actions are
/// logged — never RSVPs, votes, or the scheduler's own housekeeping. Values are stored as
/// integers, so append new members at the end and never renumber.</summary>
public enum ActionLogAction
{
    EventCreated = 0,
    EventEdited = 1,
    EventDeleted = 2,
    EventSkipped = 3,
    SeriesEdited = 4,
    SeriesStopped = 5,
    PollCreated = 6,
    PollClosed = 7,
    PollConverted = 8,
    PollDeleted = 9,
    TemplateCreated = 10,
    TemplateEdited = 11,
    TemplateDeleted = 12,
    SettingsChanged = 13,
    LiveListCreated = 14,
    LiveListRemoved = 15,
    EventsExported = 16,
}

/// <summary>The kind of thing an action log entry points at. Guild-level entries (settings,
/// exports) carry no target id — the entry's guild is the target.</summary>
public enum ActionTargetType
{
    Guild = 0,
    Event = 1,
    Series = 2,
    Poll = 3,
    Template = 4,
    LiveList = 5,
}

/// <summary>One server action log entry as the web app sees it.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="ActorUserId">The acting user's Discord id; null when the actor is unknown.</param>
/// <param name="ActorName">The actor's display-name snapshot from their last web sign-in, or
/// null when they have never signed in (clients fall back to the id).</param>
/// <param name="Source">Whether the action came through Discord or the web app.</param>
/// <param name="Action">What happened.</param>
/// <param name="TargetType">The kind of thing the action touched.</param>
/// <param name="TargetId">The touched row's id, when the target type has one.</param>
/// <param name="TargetExists">Whether the target still exists at read time (an older "created"
/// entry outlives its event) — clients link only when true. Always true for guild-level entries.</param>
/// <param name="Summary">A short human sentence, e.g. <c>Edited "Raid Night" — title, start</c>.</param>
/// <param name="DetailsJson">Optional machine detail (changed field names, scope), or null.</param>
/// <param name="CreatedAtUtc">When the action happened.</param>
public record ActionLogEntryDto(
    Guid Id,
    long GuildId,
    long? ActorUserId,
    string? ActorName,
    ActionSource Source,
    ActionLogAction Action,
    ActionTargetType TargetType,
    Guid? TargetId,
    bool TargetExists,
    string Summary,
    string? DetailsJson,
    DateTimeOffset CreatedAtUtc);

/// <summary>One page of a guild's action log, newest first.</summary>
/// <param name="Entries">The page's entries, newest first.</param>
/// <param name="NextCursor">Pass as <c>before</c> to fetch the next (older) page; null when this
/// page reached the end.</param>
public record ActionLogPageDto(IReadOnlyList<ActionLogEntryDto> Entries, string? NextCursor);

/// <summary>The request header the bot uses to name the Discord user behind a mutation whose
/// body carries no user id (deletes, skips, stops, closes, settings). The API honors it for
/// API-key (bot) callers only — web callers are always identified by their session.</summary>
public static class ActionLogHeaders
{
    public const string ActorUserId = "X-CalCrony-Actor";
}
