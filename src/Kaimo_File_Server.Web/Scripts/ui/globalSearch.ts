// Keyboard navigation support for the global search dropdown (GlobalSearch.razor).
// The selection logic lives in Blazor; this file only covers what Blazor cannot do:
//  • stop ArrowUp/ArrowDown from moving the caret to the start/end of the input while
//    results are shown (Blazor's preventDefault cannot be conditional per key), and
//  • scroll the keyboard-selected result into view inside the scrollable dropdown, and
//  • scroll the selected row of the results page (/search) into view.
(function () {
    document.addEventListener('keydown', function (e) {
        if ((e.key === 'ArrowUp' || e.key === 'ArrowDown')
            && (e.target as Element).classList && (e.target as Element).classList.contains('search-input')
            && document.querySelector('.global-search .search-result-item[role="option"]')) {
            e.preventDefault();
        }
    });

    window.kaimoGlobalSearch = {
        revealActive: function () {
            const item = document.querySelector('.global-search .search-result-item.active');
            if (item) item.scrollIntoView({ block: 'nearest' });
        },
        // Results page: bring the selected tree row (e.g. the share opened from "n more in …")
        // into view and give it the keyboard focus.
        revealTreeSelection: function () {
            const row = document.querySelector('.search-tree-row--selected') as HTMLElement | null;
            if (!row) return;
            row.focus({ preventScroll: true });
            row.scrollIntoView({ block: 'center' });
        },
        // Results page: the search lives in the history entry Blazor writes ({ userState, _index }).
        // After a reload Blazor does not hand it to .NET, so the page reads it here; it stays
        // until the user leaves the page.
        historyState: function (): string | null {
            const state = history.state as { userState?: unknown } | null;
            return state && typeof state.userState === 'string' ? state.userState : null;
        },
        // Live updates from the search bar replace the current entry (no new back step) and keep
        // Blazor's own _index, so back/forward and reload restore the latest search.
        replaceHistoryState: function (json: string) {
            const state = (history.state && typeof history.state === 'object') ? history.state : {};
            history.replaceState({ ...state, userState: json }, '', location.href);
        }
    };
})();
