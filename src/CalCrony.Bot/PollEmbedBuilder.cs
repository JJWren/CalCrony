using System.Text;
using CalCrony.Contracts;
using Discord;

namespace CalCrony.Bot;

public static class PollEmbedBuilder
{
    private static readonly Color PollColor = new(0x57, 0xB9, 0xE2);

    private const int BarCells = 6;
    private const int MaxMentionsPerOption = 15;
    private const int DescriptionBudget = 3800;

    /// <summary>Options beyond this count render as a select menu instead of buttons.</summary>
    public const int ButtonOptionLimit = 5;

    private static readonly string[] DigitEmojis =
        ["1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "🔟"];

    public static Embed Build(PollDto poll)
    {
        var description = new StringBuilder();
        description.AppendLine(Header(poll));
        description.AppendLine();

        var withNames = !poll.Anonymous;
        var body = RenderOptions(poll, withNames);
        if (withNames && body.Length > DescriptionBudget)
        {
            // Too chatty for Discord's limits — degrade every option to counts only.
            body = RenderOptions(poll, withNames: false);
        }

        description.Append(body);

        var builder = new EmbedBuilder()
            .WithTitle($"📊 {poll.Question}")
            .WithColor(PollColor)
            .WithDescription(description.ToString())
            .WithFooter($"Poll {poll.Id}");

        return builder.Build();
    }

    public static MessageComponent BuildComponents(PollDto poll)
    {
        var builder = new ComponentBuilder();

        if (poll.Status == PollStatus.Open)
        {
            if (poll.Options.Count <= ButtonOptionLimit)
            {
                var row = new ActionRowBuilder();
                foreach (var (option, index) in poll.Options.Select((o, i) => (o, i)))
                {
                    // Time-poll labels can't render <t:> timestamps — voters map the digit
                    // to the embed line, which is the localized source of truth.
                    var label = poll.IsTimePoll ? null : Truncate(option.Text, 80);
                    row.WithButton(
                        label,
                        customId: $"pollvote:{poll.Id}:{option.Id}",
                        style: ButtonStyle.Secondary,
                        emote: new Emoji(DigitEmojis[index]));
                }

                builder.AddRow(row);
            }
            else
            {
                var menu = new SelectMenuBuilder()
                    .WithCustomId($"pollselect:{poll.Id}")
                    .WithPlaceholder(poll.SingleVote ? "Pick one option" : "Pick your options")
                    .WithMinValues(0)
                    .WithMaxValues(poll.SingleVote ? 1 : poll.Options.Count);
                foreach (var (option, index) in poll.Options.Select((o, i) => (o, i)))
                {
                    // Select labels can't render dynamic timestamps either; fall back to fixed UTC.
                    var label = poll.IsTimePoll && option.SlotAtUtc is { } slot
                        ? slot.UtcDateTime.ToString("ddd MMM d · HH:mm 'UTC'")
                        : Truncate(option.Text, 100);
                    menu.AddOption(label, option.Id.ToString(), emote: new Emoji(DigitEmojis[index]));
                }

                builder.AddRow(new ActionRowBuilder().WithSelectMenu(menu));
            }

            if (poll.AllowUserOptions && poll.Options.Count < 10)
            {
                builder.AddRow(new ActionRowBuilder().WithButton(
                    "Add option", customId: $"polladd:{poll.Id}", style: ButtonStyle.Secondary, emote: new Emoji("➕")));
            }
        }
        else if (poll.IsTimePoll && poll.ConvertedEventId is null)
        {
            builder.AddRow(new ActionRowBuilder().WithButton(
                "Create event from winner", customId: $"pollconvert:{poll.Id}", style: ButtonStyle.Primary, emote: new Emoji("📅")));
        }

        return builder.Build();
    }

    private static string Header(PollDto poll)
    {
        if (poll.Status == PollStatus.Closed)
        {
            var total = poll.Options.Sum(o => o.VoteCount);
            var closed = poll.ClosedAtUnix is { } unix ? $"Closed <t:{unix}:R>" : "Closed";
            var converted = poll.ConvertedEventId is not null ? " · 🎉 Event created from the winning time." : "";
            return $"🔒 {closed} · {total} vote{(total == 1 ? "" : "s")}{converted}";
        }

        var chips = new List<string> { poll.SingleVote ? "Single choice" : "Multiple choice" };
        if (poll.Anonymous)
        {
            chips.Add("Anonymous");
        }

        if (poll.AllowUserOptions)
        {
            chips.Add("➕ voters can add options");
        }

        var closes = poll.ClosesAtUnix is { } closesUnix ? $" · Closes <t:{closesUnix}:R>" : "";
        return $"{string.Join(" · ", chips)}{closes}";
    }

    private static string RenderOptions(PollDto poll, bool withNames)
    {
        var body = new StringBuilder();
        var max = Math.Max(1, poll.Options.Count == 0 ? 1 : poll.Options.Max(o => o.VoteCount));
        var winnerId = poll.Status == PollStatus.Closed ? ComputeWinnerId(poll) : null;

        foreach (var (option, index) in poll.Options.Select((o, i) => (o, i)))
        {
            var trophy = option.Id == winnerId ? "🏆 " : "";
            var title = poll.IsTimePoll && option.SlotAtUnix is { } unix
                ? $"<t:{unix}:F> (<t:{unix}:R>)"
                : $"**{option.Text}**";
            body.AppendLine($"{DigitEmojis[index]} {trophy}{title}");

            var filled = (int)Math.Round(BarCells * (double)option.VoteCount / max);
            var bar = new string('▰', filled).PadRight(BarCells, '▱');
            var names = "";
            if (withNames && option.VoteCount > 0)
            {
                var voters = poll.Votes.Where(v => v.OptionId == option.Id).Select(v => $"<@{v.UserId}>").ToList();
                var shown = voters.Take(MaxMentionsPerOption).ToList();
                var more = voters.Count - shown.Count;
                names = $" — {string.Join(", ", shown)}{(more > 0 ? $" +{more} more" : "")}";
            }

            body.AppendLine($"{bar} {option.VoteCount}{names}");
        }

        return body.ToString();
    }

    /// <summary>Same rule as the API's winner helper: most votes; ties → earliest slot for
    /// time polls, first (lowest sort) otherwise. Options arrive pre-ordered from the DTO.</summary>
    private static Guid? ComputeWinnerId(PollDto poll) =>
        poll.Options.Count == 0
            ? null
            : poll.Options.OrderByDescending(o => o.VoteCount).First().Id;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
