/**
 * Info-button tooltip/popover positioning helper.
 *
 * The panels (tooltip and popover) are rendered `position: fixed` in a high
 * stacking layer so they can never be clipped by an `overflow: hidden` ancestor
 * or slip underneath a neighbouring element. This helper anchors a panel to its
 * trigger: it prefers a spot just below the trigger, flips above when there is
 * no room, and clamps horizontally (and, as a last resort, vertically) so the
 * panel always stays inside the viewport.
 *
 * Tooltips (CSS :hover/:focus driven) are positioned just-in-time via delegated
 * listeners here, so no Blazor round-trip is needed and hover stays instant.
 * The popover (click, Blazor `_open` state) calls `place()` after it renders.
 *
 * ponytail: uses `position: fixed` (same approach as contextMenu.js). Ceiling —
 * a transformed/filtered ancestor rebases fixed to itself and would offset the
 * panel; switch to the Popover API top layer if that ever bites.
 */
window.infoPopover = {
    MARGIN: 8, // keep this far from every viewport edge
    GAP: 7,    // distance between trigger and panel

    /**
     * Position `panel` relative to `anchor`. Waits two frames first so a
     * Blazor-Server-rendered panel has its final measured size.
     * @param {HTMLElement} panel
     * @param {HTMLElement} anchor
     */
    place: function (panel, anchor) {
        if (!panel || !anchor) return;
        return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
            this._position(panel, anchor);
            resolve();
        })));
    },

    _position: function (panel, anchor) {
        var m = this.MARGIN, gap = this.GAP;

        panel.style.position = 'fixed';
        // Never let a panel be taller than the viewport; its body scrolls if huge.
        panel.style.maxHeight = Math.max(0, window.innerHeight - 2 * m) + 'px';

        var a = anchor.getBoundingClientRect();
        var p = panel.getBoundingClientRect();
        var w = p.width, h = p.height;

        // Horizontal: centre on the trigger, then clamp into the viewport.
        var left = a.left + a.width / 2 - w / 2;
        left = Math.min(Math.max(m, left), Math.max(m, window.innerWidth - w - m));

        // Vertical: prefer below the trigger; flip above when it would overflow
        // the bottom; if neither fits, clamp to the bottom edge.
        var below = a.bottom + gap;
        var above = a.top - gap - h;
        var top;
        if (below + h <= window.innerHeight - m) top = below;
        else if (above >= m) top = above;
        else top = Math.max(m, window.innerHeight - h - m);

        panel.style.left = Math.round(left) + 'px';
        panel.style.top = Math.round(top) + 'px';
    }
};

// Position tooltips as the pointer/focus reaches the trigger, before the CSS
// fade-in. Delegation avoids per-instance wiring and any server round-trip.
(function () {
    function onEnter(e) {
        var t = e.target;
        var trigger = t && t.closest ? t.closest('.info-btn__trigger') : null;
        if (!trigger) return;
        // Only when entering from outside the trigger (mimic pointerenter/focus).
        if (e.relatedTarget && trigger.contains(e.relatedTarget)) return;
        var tip = trigger.querySelector('.info-btn__tip');
        if (tip) window.infoPopover._position(tip, trigger);
    }
    document.addEventListener('pointerover', onEnter, true);
    document.addEventListener('focusin', onEnter, true);
})();
