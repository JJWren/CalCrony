// Theme helpers invoked from Blazor. Two independent choices (issue #78):
// the dark/light/auto FACE ("calcrony-theme", per-device, ThemeToggle) and the interface
// THEME name ("calcrony-theme-name", per-account via UserSettings, InterfaceThemePicker).
window.calcronyTheme = {
    modes: ["dark", "light", "auto"],
    themes: ["slate", "ember", "moss", "parchment", "obsidian"],
    // Sanitize on read AND write, matching theme-init.js: stale/edited localStorage must never
    // put an unsupported value on data-bs-theme.
    getTheme: function () {
        var mode;
        try { mode = localStorage.getItem("calcrony-theme"); } catch { mode = null; }
        return window.calcronyTheme.modes.indexOf(mode) >= 0 ? mode : "dark";
    },
    setTheme: function (theme) {
        if (window.calcronyTheme.modes.indexOf(theme) < 0) { return; }
        try { localStorage.setItem("calcrony-theme", theme); } catch { /* private mode */ }
        window.calcronyTheme.apply(theme);
        // Notify Blazor subscribers (ThemeToggle) so their highlight follows mode changes made
        // elsewhere — e.g. the picker's Parchment face flip. Dead references are pruned.
        var watchers = window.calcronyTheme.modeWatchers;
        for (var i = watchers.length - 1; i >= 0; i--) {
            try { watchers[i].invokeMethodAsync("OnModeChanged", theme); } catch { watchers.splice(i, 1); }
        }
    },
    modeWatchers: [],
    watchMode: function (dotnetRef) {
        window.calcronyTheme.modeWatchers.push(dotnetRef);
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
