using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CalCrony.Contracts;

/// <summary>The one emoji classifier every entry point shares — the bot's option syntax and the
/// API's option validation must agree on what counts as an emote, or a button the bot accepted
/// is rejected by the API (or vice versa).</summary>
public static partial class EmoteText
{
    /// <summary>Custom server emote syntax (&lt;:name:id&gt;) — recognized only to reject it,
    /// since Discord buttons on a CalCrony message can't carry another server's emotes.</summary>
    [GeneratedRegex(@"^<a?:\w+:\d+>$")]
    private static partial Regex CustomEmote();

    /// <summary>Keycap emojis (#️⃣, 5⃣) are ASCII-led, so they get an exact match ahead of the
    /// rune walk in <see cref="IsLikelyEmoji"/>.</summary>
    [GeneratedRegex("^[0-9#*]\uFE0F?\u20E3$")]
    private static partial Regex KeycapEmoji();

    /// <summary>Whether text is custom server emote syntax.</summary>
    /// <param name="text">The candidate text.</param>
    /// <returns>True for &lt;:name:id&gt; / &lt;a:name:id&gt;.</returns>
    public static bool IsCustomEmote(string text) => CustomEmote().IsMatch(text);

    /// <summary>Whether text plausibly renders as ONE Unicode emoji: a single grapheme cluster
    /// whose runes are all emoji-shaped — outside the BMP, in a BMP symbol category, one of the
    /// few BMP stragglers (‼ ⁉ 〰 〽), or a joiner/variation selector riding along. Permissive at
    /// the margins by design; what it must reject is ordinary text like "abc".</summary>
    /// <param name="emote">The candidate emote text (pre-trimmed).</param>
    /// <returns>True when the text looks like a single emoji.</returns>
    public static bool IsLikelyEmoji(string emote)
    {
        if (KeycapEmoji().IsMatch(emote))
        {
            return true;
        }

        if (new StringInfo(emote).LengthInTextElements != 1)
        {
            return false;
        }

        var sawBase = false;
        foreach (var rune in emote.EnumerateRunes())
        {
            if (rune.Value is 0x200D or 0xFE0E or 0xFE0F)
            {
                continue;
            }

            if (rune.Value <= 0xFFFF
                && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol)
                && rune.Value is not (0x203C or 0x2049 or 0x3030 or 0x303D))
            {
                return false;
            }

            sawBase = true;
        }

        return sawBase;
    }
}
