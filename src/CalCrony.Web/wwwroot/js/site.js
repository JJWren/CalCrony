// Theme helpers invoked from Blazor. Two independent choices (issue #78):
// the dark/light/auto FACE ("calcrony-theme", per-device, ThemeToggle) and the interface
// THEME name ("calcrony-theme-name", per-account via UserSettings, InterfaceThemePicker).
window.calcronyTheme = {
    themes: ["slate", "ember", "moss", "parchment", "obsidian"],
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
    },
    getThemeName: function () {
        var name;
        try { name = localStorage.getItem("calcrony-theme-name"); } catch { name = null; }
        return window.calcronyTheme.themes.indexOf(name) >= 0 ? name : "slate";
    },
    setThemeName: function (name) {
        if (window.calcronyTheme.themes.indexOf(name) < 0) { return; }
        try { localStorage.setItem("calcrony-theme-name", name); } catch { /* private mode */ }
        document.documentElement.setAttribute("data-cc-theme", name);
    }
};

// Closes the mobile nav drawer after a navigation. Called from Blazor on LocationChanged —
// NOT via data-bs-dismiss on the links, which preventDefault()s anchors and breaks routing.
window.calcronyNav = {
    closeDrawer: function () {
        var el = document.getElementById("appSidebar");
        if (!el || !window.bootstrap) { return; }
        var oc = bootstrap.Offcanvas.getInstance(el);
        if (oc) { oc.hide(); }
    }
};

// Copy-to-clipboard helper for the ICS feed URL.
window.calcronyCopy = function (text) {
    return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
};
