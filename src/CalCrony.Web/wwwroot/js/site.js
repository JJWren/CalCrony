// Theme helper invoked from Blazor (ThemeToggle) — persists the tri-state choice and applies it.
window.calcronyTheme = {
    getTheme: function () {
        try { return localStorage.getItem("calcrony-theme") || "dark"; } catch { return "dark"; }
    },
    setTheme: function (theme) {
        try { localStorage.setItem("calcrony-theme", theme); } catch { /* private mode */ }
        window.calcronyTheme.apply(theme);
    },
    apply: function (theme) {
        var resolved = theme === "auto"
            ? (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
            : theme;
        document.documentElement.setAttribute("data-bs-theme", resolved);
    }
};

// Copy-to-clipboard helper for the ICS feed URL.
window.calcronyCopy = function (text) {
    return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
};
