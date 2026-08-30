// Runs synchronously in <head> so the stored theme applies before first paint.
// Two stored choices (issue #78): "calcrony-theme" is the dark/light/auto FACE — the default is
// DARK, an explicit product decision — and "calcrony-theme-name" is the interface THEME
// (default "slate", Candlelit Slate).
//
// This file also seeds window.calcronyTheme with the single JS source of truth for the valid
// ids; site.js attaches its functions to the same object and reuses these arrays. (The server
// keeps its own copy in InterfaceThemes.cs — a cross-language duplicate by necessity — and the
// API sanitizes on read/write, so any drift fails safe to the defaults.)
(function () {
    var cc = window.calcronyTheme = {
        modes: ["dark", "light", "auto"],
        themes: ["slate", "ember", "moss", "parchment", "obsidian"]
    };
    var mode, name;
    try { mode = localStorage.getItem("calcrony-theme"); } catch { mode = null; }
    try { name = localStorage.getItem("calcrony-theme-name"); } catch { name = null; }
    mode = cc.modes.indexOf(mode) >= 0 ? mode : "dark";
    name = cc.themes.indexOf(name) >= 0 ? name : "slate";
    var resolved = mode === "auto"
        ? (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
        : mode;
    document.documentElement.setAttribute("data-bs-theme", resolved);
    document.documentElement.setAttribute("data-cc-theme", name);
})();
