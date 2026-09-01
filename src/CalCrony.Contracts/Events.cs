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
/// <param name="AttendeeRoleId">Shorthand for the ATTENDING option's role (the same setting as
/// that option's spec-level AttendeeRoleId, and giving both is an error) — an existing Discord
/// role granted to its seated RSVPs and revoked when they leave or the event ends. Set roles on
/// the other options through RsvpOptions. Bot callers only — the web can't enumerate roles, so it
/// is ignored there.</param>
/// <param name="WantsThread">Opens a discussion thread on the posted embed message; attending
/// RSVPers are auto-added and the thread archives when the event ends. Honored for both
/// caller types (unlike the attendee roles — no Discord data is needed to say yes).</param>
/// <param name="RsvpOptions">Custom RSVP options replacing the default Going/Not going/Maybe set
/// (1-10 entries; exactly one may be flagged attending — none flagged means the first). Each may
/// carry its own AttendeeRoleId, so one event can hand out a different role per choice.</param>
/// <param name="AttendeeLimit">Capacity for the attending option — shorthand that works with the
/// default option set too. Conflicts with an explicit capacity on the attending spec.</param>
/// <param name="RsvpCloseText">When RSVPs stop accepting changes: relative to start ("2h before")
/// or a natural-language absolute time ("friday 5pm"), parsed server-side.</param>
/// <param name="AllowedRoleIds">Event-level signup restriction: limits EVERY option to members
/// holding at least one of these roles — the convenience form of setting the same
/// AllowedRoleIds on each spec, and giving both is an error (the AttendeeLimit rule). Bot callers
/// only — restrictions are configured in Discord, so the web is ignored here and stripped on the
/// specs. Empty/null = unrestricted.</param>
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
    string? RsvpCloseText = null,
    IReadOnlyList<long>? AllowedRoleIds = null);

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
/// <param name="AttendeeRoleId">Replaces the ATTENDING option's role (bot callers only; existing
/// grants are re-synced to the new role). Null leaves it unchanged — clear with ClearAttendeeRole.
/// Conflicts with a role on the attending spec; edit the other options' roles via RsvpOptions.</param>
/// <param name="ClearAttendeeRole">Removes the attending option's role (existing grants are
/// revoked). Conflicts with AttendeeRoleId.</param>
/// <param name="RsvpOptions">Replaces the option set. Options are matched to existing ones by
/// label (case-insensitive): matches keep their RSVPs, new labels append, and an option with
/// RSVPs cannot be removed (409). A matched option's role is replaced by the spec's, with grants
/// re-synced for its seated users. Null leaves the options unchanged.</param>
/// <param name="AttendeeLimit">Sets the attending option's capacity. Null leaves it unchanged —
/// clear with ClearAttendeeLimit. Conflicts with an explicit capacity on the attending spec.</param>
/// <param name="ClearAttendeeLimit">Removes the attending option's capacity (the whole waitlist
/// is seated). Conflicts with AttendeeLimit.</param>
/// <param name="RsvpCloseText">Replaces the RSVP cutoff — relative ("2h before") or absolute
/// natural language. Null leaves it unchanged — clear with ClearRsvpClose.</param>
/// <param name="ClearRsvpClose">Removes the RSVP cutoff. Conflicts with RsvpCloseText.</param>
/// <param name="AllowedRoleIds">Replaces EVERY option's signup restriction with this role set
/// (bot callers only; the web clears with ClearAllowedRoles). Null leaves restrictions
/// unchanged. Conflicts with a restriction given on any spec in the same request.</param>
/// <param name="ClearAllowedRoles">Removes the signup restriction from every option. Accepted from
/// any caller — clearing needs no knowledge of the roles it removes. Conflicts with AllowedRoleIds.</param>
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
    bool ClearRsvpClose = false,
    IReadOnlyList<long>? AllowedRoleIds = null,
    bool ClearAllowedRoles = false);

/// <summary>A creator-supplied RSVP option for create/edit requests; ids and sort order are
/// assigned server-side from list position.</summary>
/// <param name="Emote">The option emoji.</param>
/// <param name="Label">The display label.</param>
/// <param name="Capacity">Optional attendee cap.</param>
/// <param name="IsAttending">Marks the option whose RSVPs count as attending (threads,
/// availability, waitlist). At most one per request; none flagged means the first.</param>
/// <param name="AttendeeRoleId">Existing Discord role granted to users seated on THIS option and
/// revoked when they leave it or the event ends — how one event hands out Tank/Healer/DPS. Bot
/// callers only (the web can't enumerate roles). On the attending option this is the same setting
/// the request-level AttendeeRoleId shorthand writes, and giving both is an error.</param>
/// <param name="AllowedRoleIds">Signup restriction: only members holding at least one of these
/// roles may pick THIS option (the creator and server managers always may). Empty/null = anyone.
/// Bot callers only — the web can't enumerate roles, so its specs are stripped on create and the
/// option's stored restriction is carried over by label on edit. Conflicts with the request-level
/// AllowedRoleIds, which writes every option.</param>
public record RsvpOptionSpec(
    string Emote, string Label, int? Capacity = null, bool IsAttending = false, long? AttendeeRoleId = null,
    IReadOnlyList<long>? AllowedRoleIds = null)
{
    /// <summary>Value equality over the role list, so two specs built from the same option compare
    /// equal — the web form decides whether the option set changed by comparing spec lists, and a
    /// reference comparison would make every edit look like an option replacement.</summary>
    /// <param name="other">The spec to compare with.</param>
    /// <returns>True when every field, including the restriction set, matches.</returns>
    public virtual bool Equals(RsvpOptionSpec? other) =>
        other is not null
        && Emote == other.Emote
        && Label == other.Label
        && Capacity == other.Capacity
        && IsAttending == other.IsAttending
        && AttendeeRoleId == other.AttendeeRoleId
        && (AllowedRoleIds ?? []).SequenceEqual(other.AllowedRoleIds ?? []);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Emote, Label, Capacity, IsAttending, AttendeeRoleId, (AllowedRoleIds ?? []).Count);
}

/// <summary>One RSVP choice on an event (emote + label, optional capacity).</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Emote">The option emoji.</param>
/// <param name="Label">The display label.</param>
/// <param name="SortOrder">Display ordering index.</param>
/// <param name="Capacity">Optional attendee cap.</param>
/// <param name="IsAttending">Whether this option's RSVPs count as attending.</param>
/// <param name="AttendeeRoleId">The Discord role seated users on this option hold, when set.</param>
/// <param name="AllowedRoles">Signup restriction: the roles a member must hold at least one of to
/// pick this option (the creator and managers bypass). Null or empty = unrestricted. Names are
/// the API's snapshots and may be null — fall back to the id.</param>
/// <param name="AttendeeRoleName">The attendee role's name snapshot, when the API holds one
/// (it does when the same role is also named by a live restriction); null otherwise.</param>
public record RsvpOptionDto(
    Guid Id, string Emote, string Label, int SortOrder, int? Capacity, bool IsAttending = false,
    long? AttendeeRoleId = null, IReadOnlyList<RoleRefDto>? AllowedRoles = null, string? AttendeeRoleName = null)
{
    /// <summary>Whether picking this option is limited by role.</summary>
    public bool IsRestricted => AllowedRoles is { Count: > 0 };
}

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
/// <param name="AttendeeRoleId">The ATTENDING option's role, when set — a convenience mirror of
/// that option's Options entry. Per-option roles are read from Options.</param>
/// <param name="WantsThread">Whether a discussion thread should open on the posted embed.</param>
/// <param name="ThreadId">The Discord thread-channel id once the thread exists.</param>
/// <param name="ChannelName">The channel's name snapshot, when one is stored (attached to every
/// single-event response, never list rows); consumers must omit gracefully when null.</param>
/// <param name="RsvpClosesAtUtc">The effective RSVP cutoff (relative cutoffs already resolved
/// against the current start time); null when RSVPs never close early.</param>
/// <param name="AllowedRoles">The signup restriction every option shares — empty when no option
/// is restricted, null when the options differ (read them from Options then). A convenience
/// mirror of the per-option sets, the way AttendeeRoleId mirrors the attending option's role.</param>
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
    DateTimeOffset? RsvpClosesAtUtc = null,
    IReadOnlyList<RoleRefDto>? AllowedRoles = null)
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

    /// <summary>Every option that grants a Discord role, in display order — what a client renders
    /// as the event's role legend. One entry is the common "Going grants @Raider" case; several is
    /// the Tank/Healer/DPS case.</summary>
    public IReadOnlyList<RsvpOptionDto> RoleGrantingOptions =>
        [.. Options.Where(o => o.AttendeeRoleId is not null).OrderBy(o => o.SortOrder)];

    /// <summary>Every option whose signup is limited by role, in display order — what a client
    /// renders as 🔒 lines. Computed from Options rather than read from AllowedRoles so a DTO
    /// built without the mirror still answers correctly.</summary>
    public IReadOnlyList<RsvpOptionDto> RestrictedOptions =>
        [.. Options.Where(o => o.IsRestricted).OrderBy(o => o.SortOrder)];

    /// <summary>The restriction shared by EVERY option, when they all agree on one non-empty
    /// set (the "whole event is limited to @Raiders" case, worth a single line); null when
    /// options differ or nothing is restricted. Computed from Options like RestrictedOptions.</summary>
    public IReadOnlyList<RoleRefDto>? SharedRestriction
    {
        get
        {
            if (Options.Count == 0 || !Options[0].IsRestricted)
            {
                return null;
            }

            var first = Options[0].AllowedRoles!;
            var firstIds = first.Select(r => r.Id).ToHashSet();
            return Options.All(o => o.AllowedRoles is { } roles
                                    && roles.Count == firstIds.Count
                                    && roles.All(r => firstIds.Contains(r.Id)))
                ? first
                : null;
        }
    }

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
/// <param name="DmReminders">Opt-in DM delivery of reminders/start pings for events the user attends
/// (default off). Null on a write means "keep the stored value" — writers that don't handle it
/// (the bot's /settings timezone, older clients) must not clobber it; never null on a read.</param>
/// <param name="DmRemindersBlockedAtUtc">Read-only: when Discord last refused a DM and the
/// preference was switched off automatically; null when that has never happened.</param>
public record UserSettingsDto(
    string? TimeZone,
    bool DmConfirmations,
    string? Theme = null,
    bool? DmReminders = null,
    DateTimeOffset? DmRemindersBlockedAtUtc = null);

/// <summary>Answer to the bot's "should I offer DM reminders now?" — true exactly once per user,
/// and only while the preference is off.</summary>
/// <param name="Offer">Whether to show the one-time opt-in prompt.</param>
public record DmReminderOfferResponse(bool Offer);

/// <summary>Outcome of the bot's pre-send claim of a DM reminder.</summary>
public enum DmReminderClaimOutcome
{
    /// <summary>The recipient is still eligible and this caller now owns the attempt — send it.</summary>
    Claimed = 0,

    /// <summary>The recipient is no longer eligible (opted out, un-RSVPed, switched option, or
    /// waitlisted) — the API cancelled the row; nothing to send, acking is a harmless no-op.</summary>
    Cancelled = 1,

    /// <summary>Not this caller's to send right now — another attempt holds the row, another DM
    /// for the SAME recipient is in flight (one at a time per person, so closed DMs are discovered
    /// once), or the row is no longer pending. Do NOT acknowledge it: it stays pending and is
    /// re-served once the live claim settles or ages out.</summary>
    AlreadyClaimed = 2,
}

/// <summary>Answer to the bot's pre-send claim of a DM reminder (see <see cref="DmReminderClaimOutcome"/>).</summary>
/// <param name="Outcome">What the claim decided.</param>
public record DmReminderClaimResponse(DmReminderClaimOutcome Outcome);

/// <summary>TimeZone (IANA id), when set, overrides the user/guild zone resolution — used where
/// the caller must preview in a specific zone, e.g. a series' stored zone for schedule edits.</summary>
/// <param name="Text">The text to parse.</param>
/// <param name="UserId">The Discord user id.</param>
/// <param name="GuildId">The Discord guild (server) id.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
public record ParseDateTimeRequest(string Text, long? UserId = null, long? GuildId = null, string? TimeZone = null);

/// <summary>A parsed datetime: the UTC instant, its Unix seconds, the zone it was resolved in,
/// and the calendar date in that zone. LocalDate is what recurrence anchors on (the API builds
/// schedules in the resolved zone, not the viewer's), so previews that reason about weekdays or
/// days-of-month must use it rather than converting Utc to the browser's zone.</summary>
/// <param name="Utc">The UTC instant.</param>
/// <param name="Unix">Unix seconds of the instant.</param>
/// <param name="TimeZone">The IANA timezone id.</param>
/// <param name="LocalDate">ISO "yyyy-MM-dd" date of the instant in TimeZone; null from older servers.</param>
public record ParseDateTimeResponse(DateTimeOffset Utc, long Unix, string TimeZone, string? LocalDate = null);

/// <summary>A selectable timezone: canonical IANA id + display label with the current UTC offset,
/// e.g. "America/Chicago — UTC-05:00".</summary>
/// <param name="Id">The unique id.</param>
/// <param name="Label">The display label.</param>
public record TimeZoneOptionDto(string Id, string Label);

/// <summary>Uniform error body every non-2xx JSON response carries.</summary>
/// <param name="Error">The user-facing error text.</param>
public record ErrorResponse(string Error);
