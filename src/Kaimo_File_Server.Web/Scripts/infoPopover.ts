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
 * Other panels (e.g. the DetailHeader menu) call `place()` after they render.
 *
 * ponytail: uses `position: fixed` (same approach as contextMenu.ts) and corrects
 * for a transformed ancestor's offset. Ceiling — such an ancestor that also clips
 * (overflow) or scales would still clip/skew the panel; switch to the Popover API
 * top layer if that ever bites.
 */
window.infoPopover = {
    MARGIN: 8, // keep this far from every viewport edge
    GAP: 7,    // distance between trigger and panel

    /**
     * Position `panel` relative to `anchor`. Waits two frames first so a
     * Blazor-Server-rendered panel has its final measured size.
     */
    place: function (panel: HTMLElement | null, anchor: Element | null) {
        if (!panel || !anchor) return;
        return new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
            this._position(panel, anchor);
            resolve();
        })));
    },

    _position: function (panel: HTMLElement, anchor: Element) {
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

        // A transformed/filtered ancestor (e.g. a modal backdrop with backdrop-filter)
        // rebases `fixed` onto itself; shift by the measured offset so the panel still
        // lands at the intended viewport coordinates.
        var placed = panel.getBoundingClientRect();
        panel.style.left = Math.round(2 * left - placed.left) + 'px';
        panel.style.top = Math.round(2 * top - placed.top) + 'px';
    }
};

// Position tooltips as the pointer/focus reaches the trigger, before the CSS
// fade-in. Delegation avoids per-instance wiring and any server round-trip.
(function () {
    function onEnter(e: PointerEvent | FocusEvent) {
        var t = e.target as Element | null;
        var trigger = t && t.closest ? t.closest('.info-btn__trigger') : null;
        if (!trigger) return;
        // Only when entering from outside the trigger (mimic pointerenter/focus).
        if (e.relatedTarget && trigger.contains(e.relatedTarget as Node)) return;
        var tip = trigger.parentElement && trigger.parentElement.querySelector<HTMLElement>('.info-btn__tip');
        if (tip) window.infoPopover._position(tip, trigger);
    }
    document.addEventListener('pointerover', onEnter, true);
    document.addEventListener('focusin', onEnter, true);

    // The trigger is a <span role="button"> (see InfoButton.razor); keep Space from
    // scrolling the page, as a native button would. Blazor handles the toggle itself.
    document.addEventListener('keydown', function (e: KeyboardEvent) {
        var t = e.target as Element | null;
        if (e.key === ' ' && t && t.classList && t.classList.contains('info-btn__trigger')) e.preventDefault();
    });
})();
