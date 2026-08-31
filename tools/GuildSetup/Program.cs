using Discord;
using Discord.Rest;

// Applies the CalCrony community-server layout to a guild, idempotently.
// Run by a throwaway setup app with Administrator — never the production bot.
//
//   $env:SETUP_BOT_TOKEN = "..."; $env:GUILD_ID = "..."; dotnet run --project tools/GuildSetup

string? token = Environment.GetEnvironmentVariable("SETUP_BOT_TOKEN");
string? guildIdRaw = Environment.GetEnvironmentVariable("GUILD_ID");

if (string.IsNullOrWhiteSpace(token) || !ulong.TryParse(guildIdRaw, out ulong guildId))
{
    Console.Error.WriteLine("Set SETUP_BOT_TOKEN and GUILD_ID first, e.g.:");
    Console.Error.WriteLine("  $env:SETUP_BOT_TOKEN = \"...\"; $env:GUILD_ID = \"...\"; dotnet run --project tools/GuildSetup");
    return 1;
}

using DiscordRestClient client = new();
await client.LoginAsync(TokenType.Bot, token);

RestGuild? guild = await client.GetGuildAsync(guildId);
if (guild is null)
{
    Console.Error.WriteLine($"The setup bot is not in guild {guildId} — check GUILD_ID and the Administrator invite.");
    return 1;
}

Console.WriteLine($"Applying layout to \"{guild.Name}\" ({guild.Id})");

if (!guild.Features.HasFeature(GuildFeature.Community))
{
    Console.Error.WriteLine("This guild does not have Community enabled — forum and announcement channels need it.");
    Console.Error.WriteLine("Enable Community in Server Settings first (walkthrough phase 2), then re-run.");
    return 1;
}

List<RestGuildChannel> channels = [.. await guild.GetChannelsAsync()];

// ---- roles -----------------------------------------------------------------

await EnsureRole("Maintainer", new GuildPermissions(administrator: true), new Color(0x0B6AA8), hoist: true);
await EnsureRole("Release Ping", GuildPermissions.None, color: null, hoist: false);

// ---- categories ------------------------------------------------------------

RestCategoryChannel startHere = await EnsureCategory("START HERE", 0);
RestCategoryChannel community = await EnsureCategory("COMMUNITY", 1);
RestCategoryChannel playground = await EnsureCategory("PLAYGROUND", 2);

// The Community wizard usually creates #rules; reuse it as #welcome so the
// guild's rules-channel mapping carries over.
if (Find("welcome") is null && Find("rules") is RestTextChannel rules)
{
    await rules.ModifyAsync(p => p.Name = "welcome");
    Console.WriteLine("  ~ renamed #rules -> #welcome");
    channels = [.. await guild.GetChannelsAsync()];
}

// ---- channels --------------------------------------------------------------

RestTextChannel welcome = await EnsureText("welcome", startHere, "Start here — what CalCrony is and the house rules.");
RestTextChannel? announcements = await EnsureNews("announcements", startHere, "Releases and news. Opt into @Release Ping in onboarding.");
RestTextChannel faq = await EnsureText("faq", startHere, "Answers to the questions #support sees most. Full docs: calcrony.app/docs");

RestTextChannel general = await EnsureText("general", community, "The tavern. Anything CalCrony-adjacent.");
await EnsureForum("support", community,
    "One post per question — search existing posts first. Include what you ran and what happened. " +
    "Confirmed bugs graduate to GitHub issues (github.com/JJWren/CalCrony/issues); a maintainer will point the way.");
RestTextChannel feedback = await EnsureText("feedback", community, "Feature ideas and discussion before they become GitHub issues.");

RestTextChannel tryChannel = await EnsureText("try-calcrony", playground, "The playground — try /create, /poll, or /availability right here.");

// ---- read-only permissions -------------------------------------------------
// Maintainer needs no overwrites: Administrator bypasses all of them.

OverwritePermissions readOnly = new(
    sendMessages: PermValue.Deny,
    sendMessagesInThreads: PermValue.Deny,
    createPublicThreads: PermValue.Deny,
    createPrivateThreads: PermValue.Deny);

foreach (RestTextChannel ch in new[] { welcome, faq, announcements }.OfType<RestTextChannel>())
{
    await ch.AddPermissionOverwriteAsync(guild.EveryoneRole, readOnly);
    Console.WriteLine($"  = read-only: #{ch.Name}");
}

// ---- seed copy -------------------------------------------------------------

ulong supportId = Find("support")?.Id ?? 0;

await SeedIfEmpty(welcome, $"""
    # Welcome to CalCrony 🎲
    CalCrony turns your Discord server into a scheduling machine — events with RSVP buttons, recurring series, time polls, availability grids, and calendar sync, all from slash commands. The web app at <https://calcrony.app> gives the same data a friendlier view.

    **Get your bearings**
    - <#{supportId}> — questions, one forum post each
    - <#{feedback.Id}> — ideas before they become GitHub issues
    - <#{tryChannel.Id}> — the playground; try `/create` or `/poll` right now
    - Docs: <https://calcrony.app/docs> · Source: <https://github.com/JJWren/CalCrony>

    **House rules**
    1. Be excellent to each other.
    2. Keep it in English so everyone can follow along.
    3. No spam, ads, or self-promotion.
    4. Questions go to <#{supportId}> — one post per question.
    5. Confirmed bugs graduate to GitHub issues; a maintainer will point the way.

    By being here you agree to the terms (<https://calcrony.app/terms>) and privacy policy (<https://calcrony.app/privacy>). Want a ping when a new version ships? Grab the **Release Ping** role in onboarding.
    """);

await SeedIfEmpty(faq, """
    # FAQ
    **What is CalCrony?**
    A Discord event & calendar suite: `/create` events with RSVP buttons, repeat rules, reminders, time polls, availability grids, Google Calendar sync — plus a web view at <https://calcrony.app>.

    **I just added the bot to my server. What first?**
    Two commands shape everything else:
    1. `/settings server-timezone` — until it's set the server runs on UTC, so "tomorrow 6pm" can land hours off.
    2. `/settings default-channel` — events created from the web post there; web creation is blocked until it's set.
    Optional: `/settings native-events on` mirrors events into Discord's built-in Events tab (the bot needs **Manage Events**).

    **Why isn't the bot assigning roles?**
    It needs **Manage Roles** (the standard invite grants it) *and* the roles it manages must sit **below the bot's own role** in Server Settings → Roles. Role ordering is something no invite can set — drag it yourself.
    """, """
    **Interest roles vs attendee roles?**
    Interest roles (`@dnd`) are standing groups people join and keep — point `/availability role` and `/notify` at them. Attendee roles are temporary badges CalCrony manages via `/create attendee-role:` — granted on "Going", removed when the event ends. Never reuse an interest role as an attendee role: it empties when the event ends.

    **My calendar app doesn't show a new event.**
    The `/link` feed updates instantly, but Google Calendar re-fetches URL subscriptions only every 12–24 hours. Apple Calendar and Outlook let you pick a faster interval.

    **Google says the app isn't verified.**
    Click **Advanced → Continue**. The permission is free/busy only — CalCrony never sees your event details. See <https://calcrony.app/privacy>.

    **What does the web login see?**
    Your identity and server list. Never your messages.

    Full docs: <https://calcrony.app/docs>
    """);

// ---- summary ---------------------------------------------------------------

string[] expected = ["welcome", "announcements", "faq", "general", "support", "feedback", "try-calcrony",
                     "START HERE", "COMMUNITY", "PLAYGROUND"];
List<RestGuildChannel> leftovers = [.. (await guild.GetChannelsAsync())
    .Where(c => !expected.Contains(c.Name, StringComparer.OrdinalIgnoreCase))];
if (leftovers.Count > 0)
{
    Console.WriteLine("Left untouched (delete by hand if unwanted): "
        + string.Join(", ", leftovers.Select(c => c.Name)));
}

Console.WriteLine("Done. Hand-finish per walkthrough phase 5: Maintainer role for yourself, onboarding question, community channel mappings.");
return 0;

// ---- helpers ---------------------------------------------------------------

RestGuildChannel? Find(string name) =>
    channels.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

async Task<RestRole> EnsureRole(string name, GuildPermissions permissions, Color? color, bool hoist)
{
    RestRole? existing = guild.Roles.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
        await existing.ModifyAsync(p =>
        {
            p.Permissions = permissions;
            p.Hoist = hoist;
            p.Mentionable = true;
            if (color is { } c)
            {
                p.Color = c;
            }
        });
        Console.WriteLine($"  = role reconciled: {name}");
        return existing;
    }

    RestRole role = await guild.CreateRoleAsync(name, permissions, color, hoist, isMentionable: true);
    Console.WriteLine($"  + role created: {name}");
    return role;
}

async Task<RestCategoryChannel> EnsureCategory(string name, int position)
{
    if (Find(name) is RestCategoryChannel existing)
    {
        await existing.ModifyAsync(p => p.Position = position);
        Console.WriteLine($"  = category reconciled: {name}");
        return existing;
    }

    RestCategoryChannel category = await guild.CreateCategoryChannelAsync(name, p => p.Position = position);
    channels.Add(category);
    Console.WriteLine($"  + category created: {name}");
    return category;
}

async Task<RestTextChannel> EnsureText(string name, RestCategoryChannel category, string topic)
{
    if (Find(name) is RestTextChannel existing and not RestNewsChannel)
    {
        await existing.ModifyAsync(p =>
        {
            p.CategoryId = category.Id;
            p.Topic = topic;
        });
        Console.WriteLine($"  = channel exists: #{name}");
        return existing;
    }

    RestTextChannel channel = await guild.CreateTextChannelAsync(name, p =>
    {
        p.CategoryId = category.Id;
        p.Topic = topic;
    });
    channels.Add(channel);
    Console.WriteLine($"  + channel created: #{name}");
    return channel;
}

async Task<RestTextChannel?> EnsureNews(string name, RestCategoryChannel category, string topic)
{
    RestGuildChannel? found = Find(name);
    switch (found)
    {
        case RestNewsChannel news:
            await news.ModifyAsync(p =>
            {
                p.CategoryId = category.Id;
                p.Topic = topic;
            });
            Console.WriteLine($"  = announcement channel exists: #{name}");
            return news;
        case RestTextChannel text:
            await text.ModifyAsync(p =>
            {
                p.CategoryId = category.Id;
                p.Topic = topic;
            });
            Console.WriteLine($"  ! #{name} exists as a plain text channel — flip it to Announcement in channel settings by hand");
            return text;
        default:
            RestTextChannel created = await guild.CreateNewsChannelAsync(name, p =>
            {
                p.CategoryId = category.Id;
                p.Topic = topic;
            });
            channels.Add(created);
            Console.WriteLine($"  + announcement channel created: #{name}");
            return created;
    }
}

async Task EnsureForum(string name, RestCategoryChannel category, string guidelines)
{
    RestGuildChannel? found = Find(name);
    if (found is RestForumChannel forum)
    {
        await forum.ModifyAsync(p =>
        {
            p.CategoryId = category.Id;
            p.Topic = guidelines;
        });
        Console.WriteLine($"  = forum exists: #{name}");
        return;
    }

    if (found is not null)
    {
        Console.WriteLine($"  ! #{name} exists but is not a forum channel — delete it and re-run, or convert by hand");
        return;
    }

    RestForumChannel created = await guild.CreateForumChannelAsync(name, p =>
    {
        p.CategoryId = category.Id;
        p.Topic = guidelines;
    });
    channels.Add(created);
    Console.WriteLine($"  + forum created: #{name}");
}

async Task SeedIfEmpty(RestTextChannel channel, params string[] messages)
{
    IEnumerable<RestMessage> existing = await channel.GetMessagesAsync(1).FlattenAsync();
    if (existing.Any())
    {
        Console.WriteLine($"  = #{channel.Name} already has messages — seed copy skipped");
        return;
    }

    foreach (string message in messages)
    {
        await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
    }

    Console.WriteLine($"  + seeded #{channel.Name}");
}
