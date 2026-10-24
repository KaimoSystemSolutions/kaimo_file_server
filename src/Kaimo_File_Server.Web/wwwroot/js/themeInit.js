"use strict";
// Loaded synchronously in <head>: applies the stored theme and accent before the
// first paint (prevents a flash) and wires the reconnect dialog's reload button.
// Kept out of inline markup so the Content-Security-Policy can forbid inline script.
(function () {
    var root = document.documentElement;
    function apply() {
        root.setAttribute('data-theme', localStorage.getItem('kaimo_theme') || 'dark');
        root.setAttribute('data-accent', localStorage.getItem('kaimo_accent') || 'green');
    }
    apply();
    // Blazor's enhanced navigation merges the fetched page into the document, <html>
    // attributes included, and strips the two set above (the server markup has none) —
    // e.g. the redirect to /login on a first visit, which runs before the interactive
    // router is attached. This script does not run again then, so restore them here
    // (before the next paint, so nothing flashes).
    new MutationObserver(function () {
        if (!root.hasAttribute('data-theme') || !root.hasAttribute('data-accent'))
            apply();
    }).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-accent'] });
    document.addEventListener('click', function (e) {
        if (e.target instanceof Element && e.target.closest('.reconnect-reload'))
            location.reload();
    });
})();
