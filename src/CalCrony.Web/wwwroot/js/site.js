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
        // elsewhere — e.g. the picker's Parchment face flip. invokeMethodAsync returns a Promise:
        // a disposed reference usually REJECTS rather than throwing synchronously, so dead
        // watchers are pruned from the rejection handler (the sync catch covers the throw case).
        cc.modeWatchers.slice().forEach(function (ref) {
            var pending;
            try { pending = ref.invokeMethodAsync("OnModeChanged", theme); } catch { cc.unwatchMode(ref); return; }
            if (pending && typeof pending.catch === "function") {
                pending.catch(function () { cc.unwatchMode(ref); });
            }
        });
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

// Saves a file the page fetched through the authenticated API client (the CSV export). The
// access token lives only in memory — never a cookie or storage — so a plain <a href> to the
// API could not carry it; Blazor fetches and streams the body here as a DotNetStreamReference.
// Where the browser offers a streaming sink (File System Access API: Chromium), the response is
// piped straight to the chosen file, so memory stays flat however large the export is. Elsewhere
// (Firefox, Safari) the only download path is a Blob, which has to be assembled in memory, so
// that fallback is bounded: past the cap it aborts and reports "too-large" instead of exhausting
// the tab. Returns "saved", "cancelled" (the user closed the picker), "too-large", or "failed"
// (any rejection: a broken response stream, a write that could not complete) — never throws,
// so the page can always report what happened.
window.calcronyDownload = async function (fileName, streamRef, contentType) {
    var type = contentType || "application/octet-stream";
    var readable;
    try {
        readable = await streamRef.stream();
    } catch (e) {
        return "failed";
    }

    if (typeof window.showSaveFilePicker === "function") {
        var handle = null;
        try {
            handle = await window.showSaveFilePicker({
                suggestedName: fileName,
                types: [{ description: "CSV", accept: { "text/csv": [".csv"] } }]
            });
        } catch (e) {
            if (e && e.name === "AbortError") {
                // cancel() rejects if the .NET stream is already closed — the caller's finally
                // disposes the response either way, so cleanup failure must not mask "cancelled".
                try { await readable.cancel(); } catch (ignored) { /* already closed */ }
                return "cancelled";
            }
            // e.g. NotAllowedError when the user gesture expired during the fetch — fall through
            // to the bounded in-memory path rather than fail the download.
            handle = null;
        }
        if (handle) {
            var writable = null;
            try {
                writable = await handle.createWritable();
                await readable.pipeTo(writable);
                return "saved";
            } catch (e) {
                // pipeTo aborts the sink itself on a source error; abort explicitly for the
                // other failure modes so no half-written file is left behind, then report.
                try { if (writable) { await writable.abort(); } } catch (ignored) { /* already aborted */ }
                try { await readable.cancel(); } catch (ignored) { /* already closed */ }
                return "failed";
            }
        }
    }

    var limit = 256 * 1024 * 1024;
    var parts = [];
    var total = 0;
    var reader = readable.getReader();
    try {
        while (true) {
            var next = await reader.read();
            if (next.done) { break; }
            total += next.value.byteLength;
            if (total > limit) {
                try { await reader.cancel(); } catch (ignored) { /* already closed */ }
                return "too-large";
            }
            parts.push(next.value);
        }
    } catch (e) {
        // An interrupted response stream rejects read(); report rather than throw.
        try { await reader.cancel(); } catch (ignored) { /* already closed */ }
        return "failed";
    }

    // Blob allocation near the cap, createObjectURL, and the click can all throw; this helper
    // must still resolve to an outcome so the caller reports it instead of faulting the event.
    var url = null;
    var a = null;
    try {
        url = URL.createObjectURL(new Blob(parts, { type: type }));
        a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        a = null;
        // Revoke after the click has had a chance to start the download.
        var started = url;
        setTimeout(function () { URL.revokeObjectURL(started); }, 1000);
        return "saved";
    } catch (e) {
        if (a) { try { a.remove(); } catch (ignored) { /* not attached */ } }
        if (url) { try { URL.revokeObjectURL(url); } catch (ignored) { /* already revoked */ } }
        return "failed";
    }
};

// Page helpers invoked from Blazor. scrollToFragment: bring the element whose id matches the
// URL fragment into view after the page has rendered — a fresh load of /docs#features has no
// such element yet when the browser does its own fragment scroll, so the page asks once it does.
(function () {
    var page = window.calcronyPage = window.calcronyPage || {};
    page.scrollToFragment = function (id) {
        if (!id) { return; }
        var el = document.getElementById(id);
        if (el && typeof el.scrollIntoView === "function") { el.scrollIntoView(); }
    };
})();
