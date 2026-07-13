/**
 * Context-menu positioning helper.
 *
 * The menu is rendered with `position: fixed` at the raw click coordinates.
 * When the click happens near the bottom or right edge of the viewport the menu
 * would otherwise overflow and its lower entries become unreachable. After Blazor
 * paints the menu we measure it and, if it spills past an edge, shift it back so
 * the whole menu stays inside the viewport (with a small margin).
 */
window.contextMenu = {
    /**
     * @param {HTMLElement} el – the `.context-menu` element to reposition.
     */
    reposition: function (el) {
        if (!el) return;

        var margin = 8;
        var rect = el.getBoundingClientRect();
        var vw = window.innerWidth;
        var vh = window.innerHeight;

        var top = rect.top;
        var left = rect.left;

        // Overflowing the bottom: pull the menu up so its bottom edge fits.
        if (rect.bottom > vh - margin) {
            top = Math.max(margin, vh - rect.height - margin);
        }
        // Overflowing the right: pull the menu left so its right edge fits.
        if (rect.right > vw - margin) {
            left = Math.max(margin, vw - rect.width - margin);
        }

        if (top !== rect.top) el.style.top = top + 'px';
        if (left !== rect.left) el.style.left = left + 'px';
    }
};
