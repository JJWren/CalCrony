namespace CalCrony.Contracts;

/// <summary>The web app's interface themes. Each theme has a dark and a light face; the face is a
/// per-device choice (the existing dark/light/auto toggle) and is never stored server-side —
/// only the theme name is.</summary>
public static class InterfaceThemes
{
    /// <summary>The default theme ("Candlelit Slate"), used when a user has never picked one.</summary>
    public const string Default = "slate";

    /// <summary>Every valid <see cref="UserSettingsDto.Theme"/> value, in display order:
    /// Candlelit Slate, Tavern Ember, Feywild Moss, Parchment, Obsidian Azure.</summary>
    public static readonly IReadOnlyList<string> All = ["slate", "ember", "moss", "parchment", "obsidian"];

    /// <summary>Whether <paramref name="theme"/> is a valid stored theme value.</summary>
    /// <param name="theme">The candidate theme id.</param>
    /// <returns>True when the value is one of <see cref="All"/>.</returns>
    public static bool IsValid(string theme) => All.Contains(theme);
}
