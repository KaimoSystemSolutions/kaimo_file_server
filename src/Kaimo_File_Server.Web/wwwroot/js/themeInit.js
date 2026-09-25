// Loaded synchronously in <head>: applies the stored theme and accent before the
// first paint (prevents a flash) and wires the reconnect dialog's reload button.
// Kept out of inline markup so the Content-Security-Policy can forbid inline script.
(function () {
    var t = localStorage.getItem('kaimo_theme') || 'dark';
    document.documentElement.setAttribute('data-theme', t);
    var a = localStorage.getItem('kaimo_accent') || 'green';
    document.documentElement.setAttribute('data-accent', a);

    document.addEventListener('click', function (e) {
        if (e.target instanceof Element && e.target.closest('.reconnect-reload'))
            location.reload();
    });
})();
