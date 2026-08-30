// Theme helpers invoked from Blazor. Two independent choices (issue #78):
// the dark/light/auto FACE ("calcrony-theme", per-device, ThemeToggle) and the interface
// THEME name ("calcrony-theme-name", per-account via UserSettings, InterfaceThemePicker).
// The valid-id arrays live on window.calcronyTheme, seeded by theme-init.js before first
// paint; the fallbacks below only matter if theme-init ever failed to run.
(function () {
    var cc = window.calcronyTheme = window.calcronyTheme || {};
    cc.modes = cc.modes || ["dark", "light", "auto"];
    cc.themes = cc.themes || ["slate", "ember", "moss", "parchment", "obsidian"];
    cc.modeWatchers = [];

    // Sanitize on read AND write, matching theme-init.js: stale/edited localStorage must never
    // put an unsupported value on data-bs-theme.
    cc.getTheme = function () {
        var mode;
        try { mode = localStorage.getItem("calcrony-theme"); } catch { mode = null; }
        return cc.modes.indexOf(mode) >= 0 ? mode : "dark";
    };

    cc.setTheme = function (theme) {
        if (cc.modes.indexOf(theme) < 0) { return; }
        try { localStorage.setItem("calcrony-theme", theme); } catch { /* private mode */ }
        cc.apply(theme);
        // Notify Blazor subscribers (ThemeToggle) so their highlight follows mode changes made
        // elsewhere — e.g. the picker's Parchment face flip. Dead references are pruned.
        for (var i = cc.modeWatchers.length - 1; i >= 0; i--) {
            try { cc.modeWatchers[i].invokeMethodAsync("OnModeChanged", theme); } catch { cc.modeWatchers.splice(i, 1); }
        }
    };

    cc.apply = function (theme) {
        var resolved = theme === "auto"
            ? (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
            : theme;
        document.documentElement.setAttribute("data-bs-theme", resolved);
    };

    cc.getThemeName = function () {
        var name;
        try { name = localStorage.getItem("calcrony-theme-name"); } catch { name = null; }
        return cc.themes.indexOf(name) >= 0 ? name : "slate";
    };

    cc.setThemeName = function (name) {
        if (cc.themes.indexOf(name) < 0) { return; }
        try { localStorage.setItem("calcrony-theme-name", name); } catch { /* private mode */ }
        document.documentElement.setAttribute("data-cc-theme", name);
    };

    cc.watchMode = function (dotnetRef) {
        cc.modeWatchers.push(dotnetRef);
    };

    cc.unwatchMode = function (dotnetRef) {
        var i = cc.modeWatchers.indexOf(dotnetRef);
        if (i >= 0) { cc.modeWatchers.splice(i, 1); }
    };
})();

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
