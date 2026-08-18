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
        // Wait until the menu has its final dimensions. This is essential for
        // Blazor Server because the first post-render layout can still contain
        // the scale-in animation's smaller bounding box.
        return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
            var margin = 8;
            el.style.animation = 'none';
            el.style.maxHeight = Math.max(0, window.innerHeight - 2 * margin) + 'px';
            el.style.maxWidth = Math.max(0, window.innerWidth - 2 * margin) + 'px';

            var rect = el.getBoundingClientRect();
            var top = Math.min(Math.max(margin, rect.top), Math.max(margin, window.innerHeight - rect.height - margin));
            var left = Math.min(Math.max(margin, rect.left), Math.max(margin, window.innerWidth - rect.width - margin));
            el.style.top = top + 'px';
            el.style.left = left + 'px';
            resolve();
        })));
    }
};
