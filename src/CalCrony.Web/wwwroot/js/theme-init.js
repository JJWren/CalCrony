// Runs synchronously in <head> so the stored theme applies before first paint.
// Two stored choices (issue #78): "calcrony-theme" is the dark/light/auto FACE — the default is
// DARK, an explicit product decision — and "calcrony-theme-name" is the interface THEME
// (default "slate", Candlelit Slate). site.js keeps both attributes updated after load.
(function () {
    var mode, name;
    try { mode = localStorage.getItem("calcrony-theme"); } catch { mode = null; }
    try { name = localStorage.getItem("calcrony-theme-name"); } catch { name = null; }
    mode = mode === "light" || mode === "dark" || mode === "auto" ? mode : "dark";
    var themes = ["slate", "ember", "moss", "parchment", "obsidian"];
    name = themes.indexOf(name) >= 0 ? name : "slate";
    var resolved = mode === "auto"
        ? (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
        : mode;
    document.documentElement.setAttribute("data-bs-theme", resolved);
    document.documentElement.setAttribute("data-cc-theme", name);
})();
