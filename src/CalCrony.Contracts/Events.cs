namespace CalCrony.Contracts;

/// <summary>Event lifecycle. The Scheduled/Started numeric values are pinned by the partial
/// unique index IX_Events_SeriesId_Live ("Status" IN (0, 1)) — renumbering breaks the
/// live-occurrence guard.</summary>
public enum EventStatus
{
    Scheduled = 0,
    Started = 1,
    Ended = 2,
    Cancelled = 3,
}

/// <summary>Request to create an event. Datetimes arrive as natural-language text and are parsed
/// server-side. Recurrence: RepeatUntilText and RepeatCount are mutually exclusive and require
/// a Recurrence rule; the rule anchors on the first occurrence.</summary>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="Title">The event title.</param>
/// <param name="WhenText">Natural-language start time.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Recurrence">The repeat rule, when the event should recur.</param>
/// <param name="RepeatUntilText">Natural-language last repeat date.</param>
/// <param name="RepeatCount">Total occurrences including the first.</param>
/// <param name="TemplateId">Template to apply: explicit request fields win, the template fills
/// gaps, and its notification specs are always copied onto the created event.</param>
/// <param name="NoRecurrence">Explicitly suppresses a template's repeat rule (unset does not —
/// a template rule applies when no explicit rule is sent). Conflicts with Recurrence.</param>
/// <param name="AttendeeRoleId">Existing Discord role granted to attending RSVPs and revoked when
/// the event ends. Bot callers only — the web can't enumerate roles, so it is ignored there.</param>
/// <param name="WantsThread">Opens a discussion thread on the posted embed message; attending
/// RSVPers are auto-added and the thread archives when the event ends. Honored for both
/// caller types (unlike AttendeeRoleId — no Discord data is needed to say yes).</param>
/// <param name="RsvpOptions">Custom RSVP options replacing the default Going/Not going/Maybe set
/// (1-10 entries; exactly one may be flagged attending — none flagged means the first).</param>
/// <param name="AttendeeLimit">Capacity for the attending option — shorthand that works with the
/// default option set too. Conflicts with an explicit capacity on the attending spec.</param>
/// <param name="RsvpCloseText">When RSVPs stop accepting changes: relative to start ("2h before")
/// or a natural-language absolute time ("friday 5pm"), parsed server-side.</param>
public record CreateEventRequest(
    long CreatorId,
    string Title,
    string WhenText,
    long ChannelId,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null,
    RecurrenceRuleDto? Recurrence = null,
    string? RepeatUntilText = null,
    int? RepeatCount = null,
    Guid? TemplateId = null,
    bool NoRecurrence = false,
    long? AttendeeRoleId = null,
    bool WantsThread = false,
    IReadOnlyList<RsvpOptionSpec>? RsvpOptions = null,
    int? AttendeeLimit = null,
    string? RsvpCloseText = null);

/// <summary>Partial update; null fields are left unchanged. Scope is required when the target is
/// the live occurrence of a non-ended series and ignored otherwise.</summary>
/// <param name="EditorId">The editing user's Discord id (ignored for web callers).</param>
/// <param name="Title">The event title.</param>
/// <param name="WhenText">Natural-language start time.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="Scope">Whether the change applies to this occurrence or the whole series.</param>
/// <param name="AttendeeRoleId">Replaces the attendee role (bot callers only; existing grants are
/// re-synced to the new role). Null leaves it unchanged — clear with ClearAttendeeRole.</param>
/// <param name="ClearAttendeeRole">Removes the attendee role (existing grants are revoked).
/// Conflicts with AttendeeRoleId.</param>
/// <param name="RsvpOptions">Replaces the option set. Options are matched to existing ones by
/// label (case-insensitive): matches keep their RSVPs, new labels append, and an option with
/// RSVPs cannot be removed (409). Null leaves the options unchanged.</param>
/// <param name="AttendeeLimit">Sets the attending option's capacity. Null leaves it unchanged —
/// clear with ClearAttendeeLimit. Conflicts with an explicit capacity on the attending spec.</param>
/// <param name="ClearAttendeeLimit">Removes the attending option's capacity (the whole waitlist
/// is seated). Conflicts with AttendeeLimit.</param>
/// <param name="RsvpCloseText">Replaces the RSVP cutoff — relative ("2h before") or absolute
/// natural language. Null leaves it unchanged — clear with ClearRsvpClose.</param>
/// <param name="ClearRsvpClose">Removes the RSVP cutoff. Conflicts with RsvpCloseText.</param>
public record UpdateEventRequest(
    long EditorId,
    string? Title = null,
    string? WhenText = null,
    string? Description = null,
    int? DurationMinutes = null,
    string? Location = null,
    string? ImageUrl = null,
    EventStatus? Status = null,
    EditScope? Scope = null,
    long? AttendeeRoleId = null,
    bool ClearAttendeeRole = false,
    IReadOnlyList<RsvpOptionSpec>? RsvpOptions = null,
    int? AttendeeLimit = null,
    bool ClearAttendeeLimit = false,
    string? RsvpCloseText = null,
    bool ClearRsvpClose = false);

/// <summary>A creator-supplied RSVP option for create/edit requests; ids and sort order are
/// assigned server-side from list position.</summary>
/// <param name="Emote">The option emoji.</param>
/// <param name="Label">The display label.</param>
/// <param name="Capacity">Optional attendee cap.</param>
/// <param name="IsAttending">Marks the option whose RSVPs count as attending (roles, threads,
/// availability, waitlist). At most one per request; none flagged means the first.</param>
public record RsvpOptionSpec(string Emote, string Label, int? Capacity = null, bool IsAttending = false);

/// <summary>One RSVP choice on an event (emote + label, optional capacity).</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Emote">The option emoji.</param>
/// <param name="Label">The display label.</param>
/// <param name="SortOrder">Display ordering index.</param>
/// <param name="Capacity">Optional attendee cap.</param>
/// <param name="IsAttending">Whether this option's RSVPs count as attending.</param>
public record RsvpOptionDto(Guid Id, string Emote, string Label, int SortOrder, int? Capacity, bool IsAttending = false);

/// <summary>A user's RSVP: which option they picked.</summary>
/// <param name="UserId">The Discord user id.</param>
/// <param name="OptionId">The RSVP/poll option id.</param>
/// <param name="Waitlisted">True while the user is queued past the attending option's capacity;
/// waitlisted RSVPs don't count toward capacity, roles, threads, or availability.</param>
public record RsvpDto(long UserId, Guid OptionId, bool Waitlisted = false);

/// <summary>An event with its RSVP options and current RSVPs. RecurrenceSummary is the human-readable repeat rule, null for one-offs and ended series.</summary>
/// <param name="Id">The unique id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="CreatorId">The creating user's Discord id.</param>
/// <param name="Title">The event title.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="StartsAtUtc">The start instant (UTC).</param>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="DurationMinutes">Duration in minutes.</param>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id.</param>
/// <param name="Location">Optional location text.</param>
/// <param name="ImageUrl">Optional image URL.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="Options">The RSVP options.</param>
/// <param name="Rsvps">The RSVP rows.</param>
/// <param name="SeriesId">The series id.</param>
/// <param name="RecurrenceSummary">Human-readable repeat rule; null for one-offs and ended series.</param>
/// <param name="NativeEventId">The mirrored Discord scheduled-event id, when mirrored.</param>
/// <param name="AttendeeRoleId">The Discord role granted to attending RSVPs, when set.</param>
/// <param name="WantsThread">Whether a discussion thread should open on the posted embed.</param>
/// <param name="ThreadId">The Discord thread-channel id once the thread exists.</param>
/// <param name="ChannelName">The channel's name snapshot, when one is stored (attached to every
/// single-event response, never list rows); consumers must omit gracefully when null.</param>
/// <param name="RsvpClosesAtUtc">The effective RSVP cutoff (relative cutoffs already resolved
/// against the current start time); null when RSVPs never close early.</param>
public record EventDto(
    Guid Id,
    long GuildId,
    long CreatorId,
    string Title,
    string? Description,
    DateTimeOffset StartsAtUtc,
    string TimeZone,
    int? DurationMinutes,
    long ChannelId,
    long? MessageId,
    string? Location,
    string? ImageUrl,
    EventStatus Status,
    IReadOnlyList<RsvpOptionDto> Options,
    IReadOnlyList<RsvpDto> Rsvps,
    Guid? SeriesId = null,
    string? RecurrenceSummary = null,
    long? NativeEventId = null,
    long? AttendeeRoleId = null,
    bool WantsThread = false,
    long? ThreadId = null,
    string? ChannelName = null,
    DateTimeOffset? RsvpClosesAtUtc = null)
{
    /// <summary>Unix seconds of the start time, for Discord &lt;t:...&gt; timestamps.</summary>
    public long StartsAtUnix => StartsAtUtc.ToUnixTimeSeconds();

    /// <summary>Unix seconds of the RSVP cutoff, for Discord &lt;t:...&gt; timestamps.</summary>
    public long? RsvpCloseUnix => RsvpClosesAtUtc?.ToUnixTimeSeconds();

    /// <summary>The attending option — the single source of "who is going" semantics for every
    /// client (roles, threads, availability, counts). Falls back to the lowest SortOrder so
    /// pre-flag data still resolves; null only when the event has no options.</summary>
    public RsvpOptionDto? AttendingOption =>
        Options.FirstOrDefault(o => o.IsAttending) ?? Options.OrderBy(o => o.SortOrder).FirstOrDefault();

    /// <summary>Whether RSVPs are closed as of <paramref name="now"/> (cutoff reached).</summary>
    /// <param name="now">The instant to evaluate against.</param>
    /// <returns>True once the effective cutoff has passed.</returns>
    public bool RsvpsClosed(DateTimeOffset now) => RsvpClosesAtUtc is { } closes && closes <= now;

    /// <summary>Seated (non-waitlisted) RSVPs on one option — what capacity counts.</summary>
    /// <param name="optionId">The RSVP option id.</param>
    /// <returns>How many users hold a seat on the option.</returns>
    public int SeatedCount(Guid optionId) => Rsvps.Count(r => r.OptionId == optionId && !r.Waitlisted);

    /// <summary>The attending option's waitlist in promotion order (RSVP order is preserved).</summary>
    public IReadOnlyList<RsvpDto> Waitlist => AttendingOption is { } attending
        ? [.. Rsvps.Where(r => r.OptionId == attending.Id && r.Waitlisted)]
        : [];
}

/// <summary>Records where the bot posted an event's embed (bot-only; only the bot knows message ids).</summary>
/// <param name="ChannelId">The Discord channel id.</param>
/// <param name="MessageId">The Discord message id.</param>
/// <param name="ChannelName">The channel's current Discord name, upserted as a name snapshot;
/// null skips the snapshot.</param>
public record SetEventMessageRequest(long ChannelId, long MessageId, string? ChannelName = null);

/// <summary>Records the Discord Guild Scheduled Event mirroring an event (bot-only). Null clears
/// a stale id after the native event was deleted Discord-side.</summary>
/// <param name="NativeEventId">The Discord scheduled-event id, or null to clear.</param>
public record SetNativeEventRequest(long? NativeEventId);

/// <summary>Records the event's discussion-thread channel (bot-only). Null clears a stale id
/// after the thread was deleted Discord-side.</summary>
/// <param name="ThreadId">The Discord thread-channel id, or null to clear.</param>
public record SetThreadRequest(long? ThreadId);

/// <summary>Sets or replaces the calling user's RSVP to the given option.</summary>
/// <param name="OptionId">The RSVP/poll option id.</param>
public record RsvpRequest(Guid OptionId);

/// <summary>A guild's timezone, the default channel web-created embeds post to, and whether
/// events mirror into Discord's native scheduled events.</summary>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="DefaultChannelId">The channel web-created embeds post to, when set.</param>
/// <param name="MirrorNativeEvents">When true, new events mirror into the server's Events tab
/// (requires the bot to hold Manage Events).</param>
public record GuildSettingsDto(string TimeZone, long? DefaultChannelId, bool MirrorNativeEvents = false);

/// <summary>A user's personal timezone (null = use the server's), DM-confirmation preference,
/// and web interface theme.</summary>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="DmConfirmations">Whether the bot may DM confirmations.</param>
/// <param name="Theme">The web app's interface theme (see <see cref="InterfaceThemes"/>). Null on
/// a write means "keep the stored value" — writers that don't handle theming (the bot's
/// /settings timezone) must not clobber it; null on a read means the user never picked one, i.e.
/// <see cref="InterfaceThemes.Default"/>.</param>
public record UserSettingsDto(string? TimeZone, bool DmConfirmations, string? Theme = null);

/// <summary>TimeZone (IANA id), when set, overrides the user/guild zone resolution — used where
/// the caller must preview in a specific zone, e.g. a series' stored zone for schedule edits.</summary>
/// <param name="Text">The text to parse.</param>
/// <param name="UserId">The Discord user id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
public record ParseDateTimeRequest(string Text, long? UserId = null, long? GuildId = null, string? TimeZone = null);

/// <summary>A parsed datetime: the UTC instant, its Unix seconds, and the zone it was resolved in.</summary>
/// <param name="Utc">The UTC instant.</param>
/// <param name="Unix">Unix seconds of the instant.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
public record ParseDateTimeResponse(DateTimeOffset Utc, long Unix, string TimeZone);

/// <summary>A selectable timezone: canonical IANA id + display label with the current UTC offset,
/// e.g. "America/Chicago — UTC-05:00".</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Label">The display label.</param>
public record TimeZoneOptionDto(string Id, string Label);

/// <summary>Uniform error body every non-2xx JSON response carries.</summary>
/// <param name="Error">The user-facing error text.</param>
public record ErrorResponse(string Error);
