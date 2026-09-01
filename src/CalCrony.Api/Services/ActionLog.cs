using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Composes server action log rows for the mutation endpoints. Sites call
/// <see cref="Record"/> inside their own unit of work — the row is just another tracked entity,
/// so it commits with the change (and rolls back with it) without a second SaveChanges. Nothing
/// here touches the database directly, which keeps the composition rules unit-testable.</summary>
public static class ActionLog
{
    /// <summary>Titles longer than this are clipped inside summaries so two of them (poll
    /// conversion names both the poll and the event) still fit the Summary column.</summary>
    public const int MaxQuotedLength = 100;

    private static readonly JsonSerializerOptions DetailsOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Who performed a logged action and through which client.</summary>
    /// <param name="UserId">The Discord user id, or null when a bot call named nobody.</param>
    /// <param name="Source">Discord (bot-relayed) or the web app.</param>
    public readonly record struct Actor(long? UserId, ActionSource Source)
    {
        /// <summary>True when a user is known — log sites skip entries that would name nobody.</summary>
        public bool IsKnown => UserId is not null;
    }

    /// <summary>Resolves the actor behind a request. Web callers are always their session's
    /// subject (a body id is ignored, matching how the endpoints treat CreatorId/EditorId). Bot
    /// callers are the body's user id when the request carries one, else the
    /// <see cref="ActionLogHeaders.ActorUserId"/> header the bot sets on body-less calls.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="bodyUserId">The user id from the request body, when the contract has one.</param>
    /// <returns>The resolved actor; its UserId is null only for a bot call that named nobody.</returns>
    public static Actor ActorFor(HttpContext context, long? bodyUserId = null)
    {
        if (!context.User.IsBot())
        {
            return new Actor(context.User.WebUserId(), ActionSource.Web);
        }

        if (bodyUserId is { } fromBody)
        {
            return new Actor(fromBody, ActionSource.Discord);
        }

        var header = context.Request.Headers[ActionLogHeaders.ActorUserId].ToString();
        return new Actor(long.TryParse(header, out var fromHeader) ? fromHeader : null, ActionSource.Discord);
    }

    /// <summary>Adds an entry to the unit of work. Entries with an unknown actor are dropped
    /// rather than written — a nameless line ("someone deleted X") is noise, and every user path
    /// names its user, so a missing one means a system path reached a logged site.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="actor">Who acted, and from where.</param>
    /// <param name="action">What happened.</param>
    /// <param name="targetType">The kind of thing touched.</param>
    /// <param name="targetId">The touched row's id, or null for guild-level actions.</param>
    /// <param name="summary">The human sentence (see <see cref="Quote"/> for titles).</param>
    /// <param name="now">The current instant.</param>
    /// <param name="details">Optional machine detail, serialized as a JSON object; null omits it.</param>
    /// <returns>The entry added, or null when the actor was unknown and nothing was written.</returns>
    public static ActionLogEntry? Record(
        CalCronyDbContext db,
        long guildId,
        Actor actor,
        ActionLogAction action,
        ActionTargetType targetType,
        Guid? targetId,
        string summary,
        Instant now,
        object? details = null)
    {
        if (!actor.IsKnown)
        {
            return null;
        }

        var entry = Compose(guildId, actor, action, targetType, targetId, summary, now, details);
        db.ActionLogEntries.Add(entry);
        return entry;
    }

    /// <summary>Builds an entry without touching a context (the pure core of <see cref="Record"/>).</summary>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="actor">Who acted, and from where.</param>
    /// <param name="action">What happened.</param>
    /// <param name="targetType">The kind of thing touched.</param>
    /// <param name="targetId">The touched row's id, or null for guild-level actions.</param>
    /// <param name="summary">The human sentence.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="details">Optional machine detail, serialized as a JSON object; null omits it.</param>
    /// <returns>The composed entry (not yet tracked).</returns>
    public static ActionLogEntry Compose(
        long guildId,
        Actor actor,
        ActionLogAction action,
        ActionTargetType targetType,
        Guid? targetId,
        string summary,
        Instant now,
        object? details = null) => new()
    {
        Id = Guid.NewGuid(),
        GuildId = guildId,
        ActorUserId = actor.UserId,
        Source = actor.Source,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        // Sites compose bounded summaries (clipped titles), so this is a backstop against a
        // Postgres truncation error, not a formatting rule.
        Summary = Clip(summary, FieldLimits.ActionSummary),
        DetailsJson = SerializeDetails(details),
        CreatedAt = now,
    };

    /// <summary>Wraps a title in curly quotes, clipped to <see cref="MaxQuotedLength"/>.</summary>
    /// <param name="title">The title, question, or name to quote.</param>
    /// <returns>The quoted, possibly clipped text.</returns>
    public static string Quote(string? title) => $"“{Clip(title, MaxQuotedLength)}”";

    /// <summary>Clips text to a maximum length with a trailing ellipsis; whitespace is collapsed
    /// to a single line so a multi-line title can't break the one-line summary. The cut never
    /// splits a surrogate pair (an emoji straddling the boundary is dropped whole) — a lone
    /// surrogate would render as garbage and fail to serialize as JSON.</summary>
    /// <param name="text">The text to clip.</param>
    /// <param name="max">The maximum length including the ellipsis.</param>
    /// <returns>The clipped text (empty for null).</returns>
    public static string Clip(string? text, int max)
    {
        var flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= max)
        {
            return flat;
        }

        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(flat[cut - 1]))
        {
            cut--;
        }

        return flat[..cut] + "…";
    }

    /// <summary>The names whose flag is set — for "which fields changed" detail lists.</summary>
    /// <param name="fields">Field name / changed pairs.</param>
    /// <returns>The changed names in the order given.</returns>
    public static IReadOnlyList<string> Changed(params (string Name, bool Changed)[] fields) =>
        [.. fields.Where(f => f.Changed).Select(f => f.Name)];

    /// <summary>An edit summary: <c>Edited "Title" — field, field</c>, or just <c>Edited "Title"</c>
    /// when nothing specific was named (an empty PATCH still counts as a touch).</summary>
    /// <param name="verb">The leading verb, e.g. "Edited".</param>
    /// <param name="title">The target's title.</param>
    /// <param name="fields">The changed field names.</param>
    /// <returns>The composed summary.</returns>
    public static string EditSummary(string verb, string? title, IReadOnlyList<string> fields) =>
        fields.Count == 0
            ? $"{verb} {Quote(title)}"
            : $"{verb} {Quote(title)} — {string.Join(", ", fields)}";

    private static string? SerializeDetails(object? details)
    {
        if (details is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(details, DetailsOptions);
        // Details are field names and scopes, never payloads, so this never triggers in
        // practice; an over-long object degrades to a marker rather than invalid JSON.
        return json.Length <= FieldLimits.ActionDetails ? json : """{"truncated":true}""";
    }
}
