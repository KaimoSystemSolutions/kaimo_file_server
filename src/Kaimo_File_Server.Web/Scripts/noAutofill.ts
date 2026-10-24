// Application-wide autofill opt-out. Chromium browsers (Brave's "Create new email alias",
// address/e-mail autofill) and password-manager extensions otherwise attach suggestions to
// almost every text field. Any input/textarea WITHOUT an explicit autocomplete attribute is
// opted out here; fields that declare a real token (autocomplete="username",
// "current-password", "new-password" — login, share-link and connection credentials) are
// left untouched, so password managers keep working exactly there.
(function () {
    const SELECTOR = 'input:not([autocomplete]), textarea:not([autocomplete])';

    function optOut(el: Element) {
        el.setAttribute('autocomplete', 'off');
        el.setAttribute('data-form-type', 'other');
        el.setAttribute('data-1p-ignore', 'true');   // 1Password
        el.setAttribute('data-lpignore', 'true');    // LastPass
        el.setAttribute('data-bwignore', 'true');    // Bitwarden
    }

    // root is the document on the first scan, an inserted element afterwards.
    function scan(root: Element | Document) {
        if ((root as Element).matches && (root as Element).matches(SELECTOR)) optOut(root as Element);
        if (root.querySelectorAll) root.querySelectorAll(SELECTOR).forEach(optOut);
    }

    // Blazor renders interactively after load, so watch for inserted nodes.
    new MutationObserver(mutations => {
        for (const m of mutations) m.addedNodes.forEach(n => { if (n.nodeType === 1) scan(n as Element); });
    }).observe(document.documentElement, { childList: true, subtree: true });

    scan(document);
})();
