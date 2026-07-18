// Runs synchronously in <head> so the stored theme applies before first paint.
// CalCrony's default is DARK (not auto): an explicit product decision.
(function () {
    var stored;
    try { stored = localStorage.getItem("calcrony-theme"); } catch { stored = null; }
    var theme = stored === "light" || stored === "dark" || stored === "auto" ? stored : "dark";
    var resolved = theme === "auto"
        ? (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
        : theme;
    document.documentElement.setAttribute("data-bs-theme", resolved);
})();
