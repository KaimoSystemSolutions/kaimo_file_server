/**
 * Smooth size changes for dialogs whose content swaps (tab switch, loading → loaded).
 *
 * FLIP-style: Blazor patches the DOM synchronously, and the MutationObserver callback runs as a
 * microtask right after that patch, before the browser paints. There we read the new natural
 * size and animate the element from its previous size to it, so the jump is never painted.
 * Layout sizes (offsetWidth/Height) are used so the dialog's scale-in transform is ignored.
 * While animating, the element carries `smooth-resizing` so CSS can suppress scrollbars.
 */
(function () {
    const DURATION = 320;
    const EASING = 'cubic-bezier(0.32, 0.72, 0, 1)';
    // Longest a loading state may hold the previous height before the dialog shrinks to it.
    const HOLD_LIMIT = 1500;
    const observed = new WeakSet<HTMLElement>();

    window.kaimoSmoothResize = {
        observe(el: HTMLElement | null) {
            if (!el || observed.has(el)) return;
            observed.add(el);

            const size = () => ({ w: el.offsetWidth, h: el.offsetHeight });
            let last = size();
            // Non-null while the element's size is driven by an animation (including a hold).
            let anim: Animation | null = null;
            let holdTimer = 0;

            const settle = (mayHold: boolean) => {
                clearTimeout(holdTimer);
                // Mid-animation the current (animated) size is the start; otherwise the last settled one.
                const from = anim ? size() : last;
                anim?.cancel();
                anim = null;
                const to = size();
                last = to;

                if (Math.abs(from.w - to.w) < 1 && Math.abs(from.h - to.h) < 1) {
                    el.classList.remove('smooth-resizing');
                    return;
                }
                if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

                el.classList.add('smooth-resizing');
                const frames = (h: number) =>
                    [{ width: from.w + 'px', height: from.h + 'px' }, { width: to.w + 'px', height: h + 'px' }];

                // A loading spinner is usually shorter than the content it stands in for: keep the
                // height instead of dipping and growing again, until the content (or a timeout) arrives.
                if (mayHold && to.h < from.h && el.querySelector('.loading-state')) {
                    anim = el.animate(frames(from.h), { duration: DURATION, easing: EASING, fill: 'forwards' });
                    holdTimer = window.setTimeout(() => settle(false), HOLD_LIMIT);
                    return;
                }

                const current = el.animate(frames(to.h), { duration: DURATION, easing: EASING });
                anim = current;
                current.onfinish = () => {
                    if (anim !== current) return;
                    anim = null;
                    el.classList.remove('smooth-resizing');
                };
            };

            new MutationObserver(() => settle(true)).observe(el, { childList: true, subtree: true, characterData: true });

            // Keep the settled size current for changes that are not DOM mutations (window resize).
            new ResizeObserver(() => {
                if (anim === null) last = { w: el.offsetWidth, h: el.offsetHeight };
            }).observe(el);
        }
    };
})();
