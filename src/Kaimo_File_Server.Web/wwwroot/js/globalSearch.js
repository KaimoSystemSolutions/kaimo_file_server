// Keyboard navigation support for the global search dropdown (GlobalSearch.razor).
// The selection logic lives in Blazor; this file only covers what Blazor cannot do:
//  • stop ArrowUp/ArrowDown from moving the caret to the start/end of the input while
//    results are shown (Blazor's preventDefault cannot be conditional per key), and
//  • scroll the keyboard-selected result into view inside the scrollable dropdown.
(function () {
    document.addEventListener('keydown', function (e) {
        if ((e.key === 'ArrowUp' || e.key === 'ArrowDown')
            && e.target.classList && e.target.classList.contains('search-input')
            && document.querySelector('.global-search .search-result-item[role="option"]')) {
            e.preventDefault();
        }
    });

    window.kaimoGlobalSearch = {
        revealActive: function () {
            const item = document.querySelector('.global-search .search-result-item.active');
            if (item) item.scrollIntoView({ block: 'nearest' });
        }
    };
})();
