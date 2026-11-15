/**
 * Positioning of floating panels rendered `position: fixed` (context menus, info-button
 * tooltips/popovers, the DetailHeader menu), so they are never clipped by an
 * `overflow: hidden` ancestor and always stay inside the viewport.
 *
 * ponytail: corrects for a transformed ancestor's offset (info popovers). Ceiling — such an
 * ancestor that also clips (overflow) or scales would still clip/skew the panel; switch to the
 * Popover API top layer if that ever bites.
 */
(function () {
    const MARGIN = 8; // keep this far from every viewport edge
    const GAP = 7;    // distance between trigger and info panel

    // Blazor Server's first post-render layout can still carry a scale-in animation's smaller
    // box; two frames later the element has its final measured size.
    function afterLayout(work: () => void) {
        return new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
            work();
            resolve();
        })));
    }

    // Clamps a start coordinate so [start, start + size] fits into [MARGIN, viewport - MARGIN].
    function clamp(start: number, size: number, viewport: number) {
        return Math.min(Math.max(MARGIN, start), Math.max(MARGIN, viewport - size - MARGIN));
    }

    /**
     * Context menus are rendered at the raw click coordinates; near the bottom or right edge
     * they would overflow and their lower entries become unreachable, so shift them back.
     */
    window.contextMenu = {
        reposition(el: HTMLElement | null) {
            if (!el) return;
            return afterLayout(() => {
                el.style.animation = 'none';
                el.style.maxHeight = Math.max(0, window.innerHeight - 2 * MARGIN) + 'px';
                el.style.maxWidth = Math.max(0, window.innerWidth - 2 * MARGIN) + 'px';

                const rect = el.getBoundingClientRect();
                el.style.top = clamp(rect.top, rect.height, window.innerHeight) + 'px';
                el.style.left = clamp(rect.left, rect.width, window.innerWidth) + 'px';
            });
        }
    };

    /**
     * Info panels are anchored to their trigger: just below it, flipped above when there is no
     * room, clamped horizontally (and, as a last resort, vertically). Tooltips are positioned
     * just-in-time by the delegated listeners below (no Blazor round-trip, hover stays instant);
     * other panels call `place()` after they render.
     */
    window.infoPopover = {
        MARGIN,
        GAP,

        place(panel: HTMLElement | null, anchor: Element | null) {
            if (!panel || !anchor) return;
            return afterLayout(() => this._position(panel, anchor));
        },

        _position(panel: HTMLElement, anchor: Element) {
            panel.style.position = 'fixed';
            // Never let a panel be taller than the viewport; its body scrolls if huge.
            panel.style.maxHeight = Math.max(0, window.innerHeight - 2 * MARGIN) + 'px';

            const a = anchor.getBoundingClientRect();
            const p = panel.getBoundingClientRect();

            // Horizontal: centre on the trigger, then clamp into the viewport.
            const left = clamp(a.left + a.width / 2 - p.width / 2, p.width, window.innerWidth);

            // Vertical: prefer below the trigger; flip above when it would overflow the
            // bottom; if neither fits, clamp to the bottom edge.
            const below = a.bottom + GAP;
            const above = a.top - GAP - p.height;
            let top;
            if (below + p.height <= window.innerHeight - MARGIN) top = below;
            else if (above >= MARGIN) top = above;
            else top = Math.max(MARGIN, window.innerHeight - p.height - MARGIN);

            panel.style.left = Math.round(left) + 'px';
            panel.style.top = Math.round(top) + 'px';

            // A transformed/filtered ancestor (e.g. a modal backdrop with backdrop-filter)
            // rebases `fixed` onto itself; shift by the measured offset so the panel still
            // lands at the intended viewport coordinates.
            const placed = panel.getBoundingClientRect();
            panel.style.left = Math.round(2 * left - placed.left) + 'px';
            panel.style.top = Math.round(2 * top - placed.top) + 'px';
        }
    };

    // Position tooltips as the pointer/focus reaches the trigger, before the CSS fade-in.
    function onEnter(e: PointerEvent | FocusEvent) {
        const t = e.target as Element | null;
        const trigger = t && t.closest ? t.closest('.info-btn__trigger') : null;
        if (!trigger) return;
        // Only when entering from outside the trigger (mimic pointerenter/focus).
        if (e.relatedTarget && trigger.contains(e.relatedTarget as Node)) return;
        const tip = trigger.parentElement && trigger.parentElement.querySelector<HTMLElement>('.info-btn__tip');
        if (tip) window.infoPopover._position(tip, trigger);
    }
    document.addEventListener('pointerover', onEnter, true);
    document.addEventListener('focusin', onEnter, true);

    // The trigger is a <span role="button"> (see InfoButton.razor); keep Space from
    // scrolling the page, as a native button would. Blazor handles the toggle itself.
    document.addEventListener('keydown', function (e: KeyboardEvent) {
        const t = e.target as Element | null;
        if (e.key === ' ' && t && t.classList && t.classList.contains('info-btn__trigger')) e.preventDefault();
    });
})();
