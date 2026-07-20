using System.Text.Json;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Poll CRUD, voting, options, close, and time-poll conversion, with event-parity guards.</summary>
public static class PollEndpoints
{
    private const int MinOptions = 2;
    private const int MaxOptions = 10;

    /// <summary>Maps poll routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapPollEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/guilds/{guildId:long}/polls", CreatePoll);
        app.MapGet("/guilds/{guildId:long}/polls", ListPolls);
        app.MapGet("/polls/{id:guid}", GetPoll);
        app.MapPut("/polls/{id:guid}/message", SetMessage).RequireAuthorization("BotOnly");
        app.MapPut("/polls/{id:guid}/votes/{userId:long}", PutVotes);
        app.MapPost("/polls/{id:guid}/options", AddOption);
        app.MapPost("/polls/{id:guid}/close", ClosePoll);
        app.MapPost("/polls/{id:guid}/convert", ConvertPoll);
        app.MapDelete("/polls/{id:guid}", DeletePoll);
    }

    /// <summary>Creates a poll; time polls parse every option as a slot in the creator's zone (failures name the option).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="parser">The natural-language datetime parser.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> CreatePoll(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CreatePollRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);

        var isBot = context.User.IsBot();
        var creatorId = isBot ? request.CreatorId : context.User.WebUserId()!.Value;
        long channelId;
        if (isBot)
        {
            channelId = request.ChannelId;
        }
        else if (guild.DefaultChannelId is long defaultChannel)
        {
            channelId = defaultChannel;
        }
        else
        {
            return Results.BadRequest(new ErrorResponse(
                "This server has no default events channel yet — a manager must run /settings default-channel in Discord."));
        }

        var question = request.Question?.Trim() ?? "";
        if (question.Length is 0 or > 252)
        {
            return Results.BadRequest(new ErrorResponse("The question must be 1-252 characters."));
        }

        var optionTexts = (request.Options ?? []).Select(o => o?.Trim() ?? "").ToList();
        if (optionTexts.Count is < MinOptions or > MaxOptions || optionTexts.Any(t => t.Length is 0 or > 100))
        {
            return Results.BadRequest(new ErrorResponse(
                $"A poll needs {MinOptions}-{MaxOptions} options, each 1-100 characters."));
        }

        var zone = await EventEndpoints.ResolveZoneAsync(db, creatorId, guild, cancellationToken);
        var now = clock.GetCurrentInstant();

        // Every text in this request parses against the same captured reference — separate
        // per-call clock reads can straddle a minute boundary, making duplicate relative slots
        // ("in 2 hours" vs "in 120 minutes") resolve a minute apart and dodge the 409.
        Instant? closesAt = null;
        if (!string.IsNullOrWhiteSpace(request.ClosesText))
        {
            if (!parser.TryResolve(request.ClosesText, zone, out var resolved, out var error, now))
            {
                return Results.BadRequest(new ErrorResponse($"Close time: {error}"));
            }

            closesAt = resolved;
        }

        var options = new List<PollOption>();
        if (request.IsTimePoll)
        {
            var slots = new List<(string Text, Instant Slot)>();
            foreach (var text in optionTexts)
            {
                if (!parser.TryResolve(text, zone, out var slot, out var error, now))
                {
                    return Results.BadRequest(new ErrorResponse($"Option \"{text}\": {error}"));
                }

                if (slots.Any(s => s.Slot == slot))
                {
                    return Results.Conflict(new ErrorResponse($"Option \"{text}\" resolves to a time that's already an option."));
                }

                slots.Add((text, slot));
            }

            // Time-poll options display chronologically regardless of entry order.
            options.AddRange(slots.OrderBy(s => s.Slot).Select((s, i) => new PollOption
            {
                Id = Guid.NewGuid(),
                Text = s.Text,
                SlotAt = s.Slot,
                SortOrder = i,
            }));
        }
        else
        {
            options.AddRange(optionTexts.Select((t, i) => new PollOption
            {
                Id = Guid.NewGuid(),
                Text = t,
                SortOrder = i,
            }));
        }

        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatorId = creatorId,
            Question = question,
            IsTimePoll = request.IsTimePoll,
            // Time polls are inherently multi-vote: voters mark every slot they can make.
            SingleVote = !request.IsTimePoll && request.SingleVote,
            Anonymous = request.Anonymous,
            AllowUserOptions = request.AllowUserOptions,
            ChannelId = channelId,
            Status = PollStatus.Open,
            ClosesAt = closesAt,
            TimeZone = zone.Id,
            CreatedAt = now,
            Options = options,
        };
        db.Polls.Add(poll);

        if (!isBot)
        {
            db.Deliveries.Add(NewDelivery(DeliveryType.PostPollMessage, channelId,
                JsonSerializer.Serialize(new PostPollMessagePayload(poll.Id)), now));
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/polls/{poll.Id}", ToDto(poll, context));
    }

    /// <summary>Lists a guild's polls, newest first, optionally filtered by status.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ListPolls(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CalCronyDbContext db,
        CancellationToken cancellationToken,
        PollStatus? status = null,
        int limit = 10)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        limit = Math.Clamp(limit, 1, 25);
        var query = db.Polls
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .Where(p => p.GuildId == guildId);
        if (status is not null)
        {
            query = query.Where(p => p.Status == status);
        }

        var polls = await query.OrderByDescending(p => p.CreatedAt).Take(limit).ToListAsync(cancellationToken);
        return Results.Ok(polls.Select(p => ToDto(p, context)));
    }

    /// <summary>Fetches one poll with caller-aware anonymity shaping (non-members get 404).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The poll id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetPoll(
        HttpContext context, GuildAccessService access, Guid id, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var poll = await LoadPollAsync(db, id, cancellationToken);
        if (poll is null)
        {
            return Results.NotFound();
        }

        if (await GuardPollReadAsync(context, access, poll, cancellationToken) is { } denied)
        {
            return denied;
        }

        return Results.Ok(ToDto(poll, context));
    }

    /// <summary>Records where the bot posted the poll's embed (BotOnly).</summary>
    /// <param name="id">The poll id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="db">The database context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> SetMessage(
        Guid id, SetPollMessageRequest request, HttpContext context, CalCronyDbContext db, CancellationToken cancellationToken)
    {
        var poll = await LoadPollAsync(db, id, cancellationToken);
        if (poll is null)
        {
            return Results.NotFound();
        }

        poll.ChannelId = request.ChannelId;
        poll.MessageId = request.MessageId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToDto(poll, context));
    }

    /// <summary>Atomically replaces a user's vote set (self-only for web callers); a same-user race trips the unique index and returns 409.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The poll id.</param>
    /// <param name="userId">The Discord user id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutVotes(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        long userId,
        PutPollVotesRequest request,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var poll = await LoadPollAsync(db, id, cancellationToken);
        if (poll is null)
        {
            return Results.NotFound();
        }

        if (await GuardPollReadAsync(context, access, poll, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!context.User.IsBot() && context.User.WebUserId() != userId)
        {
            return GuildAccessService.SelfOnly();
        }

        if (poll.Status == PollStatus.Closed)
        {
            return Results.Conflict(new ErrorResponse("This poll is closed."));
        }

        var optionIds = (request.OptionIds ?? []).Distinct().ToList();
        if (optionIds.Any(o => poll.Options.All(po => po.Id != o)))
        {
            return Results.BadRequest(new ErrorResponse("Unknown poll option."));
        }

        if (poll.SingleVote && optionIds.Count > 1)
        {
            return Results.BadRequest(new ErrorResponse("This poll allows only one choice."));
        }

        var now = clock.GetCurrentInstant();
        var existing = poll.Votes.Where(v => v.UserId == userId).ToList();
        db.PollVotes.RemoveRange(existing);
        foreach (var optionId in optionIds)
        {
            // Explicit Add: client-set Guid keys attached via graph fixup alone would be
            // treated as existing rows and issued as UPDATEs (the Rsvp-insert gotcha).
            db.PollVotes.Add(new PollVote
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                UserId = userId,
                OptionId = optionId,
                CreatedAt = now,
            });
        }

        await EnqueuePollSyncAsync(context, db, poll, clock, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Same-user double-submit interleaving trips the (PollId, UserId, OptionId) unique
            // index; different users touch disjoint rows and can't conflict. Anything else
            // (FK/connection failures) deliberately propagates as a 500 for diagnosis.
            return Results.Conflict(new ErrorResponse("Your vote changed at the same time — try again."));
        }

        var fresh = await LoadPollAsync(db, id, cancellationToken);
        return Results.Ok(ToDto(fresh!, context));
    }

    /// <summary>Adds an option to an open poll (any member when AllowUserOptions, else creator/manager); time polls parse the text as a slot.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The poll id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="parser">The natural-language datetime parser.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> AddOption(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        AddPollOptionRequest request,
        CalCronyDbContext db,
        NaturalDateTimeParser parser,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var poll = await LoadPollAsync(db, id, cancellationToken);
        if (poll is null)
        {
            return Results.NotFound();
        }

        if (await GuardPollReadAsync(context, access, poll, cancellationToken) is { } denied)
        {
            return denied;
        }

        // The flag gates voters; the poll owner/managers can always add.
        if (!poll.AllowUserOptions &&
            await GuardPollMutateAsync(context, access, poll, cancellationToken) is { } mutateDenied)
        {
            return mutateDenied;
        }

        if (poll.Status == PollStatus.Closed)
        {
            return Results.Conflict(new ErrorResponse("This poll is closed."));
        }

        if (poll.Options.Count >= MaxOptions)
        {
            return Results.Conflict(new ErrorResponse($"A poll can have at most {MaxOptions} options."));
        }

        var text = request.Text?.Trim() ?? "";
        if (text.Length is 0 or > 100)
        {
            return Results.BadRequest(new ErrorResponse("Option text must be 1-100 characters."));
        }

        Instant? slotAt = null;
        if (poll.IsTimePoll)
        {
            var zone = Mapping.FindZone(poll.TimeZone) ?? DateTimeZone.Utc;
            if (!parser.TryResolve(text, zone, out var slot, out var error))
            {
                return Results.BadRequest(new ErrorResponse(error!));
            }

            if (poll.Options.Any(o => o.SlotAt == slot))
            {
                return Results.Conflict(new ErrorResponse("That time is already an option."));
            }

            slotAt = slot;
        }

        var option = new PollOption
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Text = text,
            SlotAt = slotAt,
            AddedByUserId = context.User.IsBot() ? request.UserId : context.User.WebUserId()!.Value,
            SortOrder = poll.Options.Count == 0 ? 0 : poll.Options.Max(o => o.SortOrder) + 1,
        };
        db.PollOptions.Add(option);

        await EnqueuePollSyncAsync(context, db, poll, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var fresh = await LoadPollAsync(db, id, cancellationToken);
        return Results.Created($"/polls/{poll.Id}", ToDto(fresh!, context));
    }

    /// <summary>Closes a poll; idempotent — closing a closed poll returns it unchanged.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The poll id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ClosePoll(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var poll = await LoadPollAsync(db, id, cancellationToken);
        if (poll is null)
        {
            return Results.NotFound();
        }

        if (await GuardPollMutateAsync(context, access, poll, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (poll.Status == PollStatus.Open)
        {
            poll.Status = PollStatus.Closed;
            poll.ClosedAt = clock.GetCurrentInstant();
            await EnqueuePollSyncAsync(context, db, poll, clock, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ToDto(poll, context));
    }

    /// <summary>Converts a closed time poll's winning slot into an event posted to the poll's channel; ConvertedEventId makes it idempotent.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The poll id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> ConvertPoll(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        ConvertPollRequest request,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var poll = await LoadPollAsync(db, id, cancellationToken);
        if (poll is null)
        {
            return Results.NotFound();
        }

        if (await GuardPollMutateAsync(context, access, poll, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!poll.IsTimePoll)
        {
            return Results.BadRequest(new ErrorResponse("Only time polls can be converted to events."));
        }

        if (poll.Status != PollStatus.Closed)
        {
            return Results.Conflict(new ErrorResponse("Close the poll before converting it."));
        }

        if (poll.ConvertedEventId is not null)
        {
            return Results.Conflict(new ErrorResponse($"Already converted — event {poll.ConvertedEventId}."));
        }

        var winner = Winner(poll);
        if (winner?.SlotAt is not Instant slot)
        {
            return Results.BadRequest(new ErrorResponse("This poll has no time options to convert."));
        }

        var now = clock.GetCurrentInstant();
        if (slot <= now)
        {
            // Convert bypasses the parser's future-only rule; re-establish it here.
            return Results.Conflict(new ErrorResponse("The winning time has already passed."));
        }

        var title = (request.Title ?? poll.Question).Trim();
        if (title.Length > FieldLimits.EventTitle)
        {
            title = title[..FieldLimits.EventTitle];
        }

        if (Validation.BadDuration(request.DurationMinutes) is { } invalid)
        {
            return invalid;
        }

        var converterId = context.User.IsBot() ? request.UserId : context.User.WebUserId()!.Value;
        var ev = new Event
        {
            Id = Guid.NewGuid(),
            GuildId = poll.GuildId,
            CreatorId = converterId,
            Title = title,
            StartsAt = slot,
            TimeZone = poll.TimeZone,
            DurationMinutes = request.DurationMinutes,
            // The poll's audience lives where the poll is — not the guild default channel.
            ChannelId = poll.ChannelId,
            Status = EventStatus.Scheduled,
            CreatedAt = now,
            Options = EventEndpoints.DefaultRsvpOptions(),
        };
        db.Events.Add(ev);
        poll.ConvertedEventId = ev.Id;

        // Always via the outbox — bot and web conversions behave identically, and the
        // hardened PostEventMessage handler owns the posting.
        db.Deliveries.Add(NewDelivery(DeliveryType.PostEventMessage, poll.ChannelId,
            JsonSerializer.Serialize(new PostEventMessagePayload(ev.Id)), now));

        await EnqueuePollSyncAsync(context, db, poll, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/events/{ev.Id}", ev.ToDto());
    }

    /// <summary>Deletes a poll; web callers capture the embed ids into a delete delivery first.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="id">The poll id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> DeletePoll(
        HttpContext context,
        GuildAccessService access,
        Guid id,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (poll is null)
        {
            return Results.NotFound();
        }

        if (await GuardPollMutateAsync(context, access, poll, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!context.User.IsBot() && poll.MessageId is long messageId)
        {
            var now = clock.GetCurrentInstant();
            db.Deliveries.Add(NewDelivery(DeliveryType.DeletePollMessage, poll.ChannelId,
                JsonSerializer.Serialize(new DeletePollMessagePayload(poll.ChannelId, messageId)), now));
        }

        db.Polls.Remove(poll);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Most votes; ties break earliest SlotAt for time polls, lowest SortOrder otherwise.
    /// The embed builder recomputes the same rule from VoteCounts, so the two never drift.</summary>
    /// <param name="poll">The poll.</param>
    /// <returns>The winning option, or null when the poll has no options.</returns>
    internal static PollOption? Winner(Poll poll)
    {
        if (poll.Options.Count == 0)
        {
            return null;
        }

        return poll.Options
            .OrderByDescending(o => poll.Votes.Count(v => v.OptionId == o.Id))
            .ThenBy(o => poll.IsTimePoll ? (IComparable)(o.SlotAt ?? Instant.MaxValue) : o.SortOrder)
            .First();
    }

    /// <summary>Read guard: bot passes, members pass, others get 404 so poll ids cannot be probed.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="poll">The poll.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    internal static async Task<IResult?> GuardPollReadAsync(
        HttpContext context, GuildAccessService access, Poll poll, CancellationToken cancellationToken)
    {
        if (context.User.IsBot())
        {
            return null;
        }

        var userId = context.User.WebUserId();
        if (userId is null)
        {
            return Results.NotFound();
        }

        return await access.CheckAsync(userId.Value, poll.GuildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.Member or GuildAccess.Manager => null,
            _ => Results.NotFound(),
        };
    }

    /// <summary>Mutate guard: creator or manager; non-members get 404.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="poll">The poll.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    internal static async Task<IResult?> GuardPollMutateAsync(
        HttpContext context, GuildAccessService access, Poll poll, CancellationToken cancellationToken)
    {
        if (context.User.IsBot())
        {
            return null;
        }

        var userId = context.User.WebUserId();
        if (userId is null)
        {
            return Results.NotFound();
        }

        return await access.CheckAsync(userId.Value, poll.GuildId, cancellationToken) switch
        {
            GuildAccess.Stale => GuildAccessService.StaleSnapshot(),
            GuildAccess.Manager => null,
            GuildAccess.Member when poll.CreatorId == userId.Value => null,
            GuildAccess.Member => Results.Json(
                new ErrorResponse("Only the poll creator or a server manager can change this poll."),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

    /// <summary>Enqueues a poll-embed re-render for web-side changes, coalescing with an identical pending sync.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="db">The database context.</param>
    /// <param name="poll">The poll.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private static async Task EnqueuePollSyncAsync(
        HttpContext context, CalCronyDbContext db, Poll poll, IClock clock, CancellationToken cancellationToken)
    {
        if (context.User.IsBot() || poll.MessageId is null)
        {
            return;
        }

        var payloadJson = JsonSerializer.Serialize(new SyncPollMessagePayload(poll.Id));
        var alreadyQueued = await db.Deliveries.AnyAsync(
            d => d.Type == DeliveryType.SyncPollMessage
                 && d.Status == DeliveryStatus.Pending
                 && d.PayloadJson == payloadJson,
            cancellationToken);
        if (alreadyQueued)
        {
            return;
        }

        db.Deliveries.Add(NewDelivery(
            DeliveryType.SyncPollMessage, poll.ChannelId, payloadJson, clock.GetCurrentInstant()));
    }

    /// <summary>Builds a pending outbox row.</summary>
    /// <param name="type">The delivery type.</param>
    /// <param name="channelId">The Discord channel id.</param>
    /// <param name="payloadJson">The serialized delivery payload.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The pending outbox row (not yet added to the context).</returns>
    private static Delivery NewDelivery(DeliveryType type, long channelId, string payloadJson, Instant now) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        ChannelId = channelId,
        PayloadJson = payloadJson,
        DueAt = now,
        Status = DeliveryStatus.Pending,
        CreatedAt = now,
    };

    /// <summary>Loads a poll with options and votes.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="id">The poll id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The poll with options and votes, or null.</returns>
    private static Task<Poll?> LoadPollAsync(CalCronyDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Polls
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    /// <summary>Projects the poll with anonymity shaping for the current caller.</summary>
    /// <param name="poll">The poll.</param>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <returns>The caller-shaped poll DTO.</returns>
    private static PollDto ToDto(Poll poll, HttpContext context) =>
        poll.ToDto(context.User.WebUserId(), context.User.IsBot());
}
