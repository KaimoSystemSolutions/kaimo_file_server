/**
 * Explorer-style rectangular selection for the virtualized file list.
 *
 * Pointer tracking, edge scrolling, row hit testing, and live previews stay in
 * the browser. Blazor receives one completed set of paths per gesture, avoiding
 * render traffic while the user is dragging or scrolling.
 */
window.fileMarquee = (() => {
    const instances = new Map();
    const minimumDragDistance = 4;
    const edgeScrollZone = 56;
    // Kept below one third of a row per frame so Blazor Virtualize has time to
    // materialize every traversed row, even on a higher-latency server circuit.
    const maximumScrollStep = 12;

    function intersects(a, b) {
        return a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
    }

    function findScrollContainer(element) {
        for (let parent = element.parentElement; parent; parent = parent.parentElement) {
            const overflowY = getComputedStyle(parent).overflowY;
            if ((overflowY === 'auto' || overflowY === 'scroll') && parent.scrollHeight > parent.clientHeight)
                return parent;
        }
        return document.scrollingElement;
    }

    function initialize(selector, dotNetRef) {
        dispose(selector);
        const host = document.querySelector(selector);
        if (!host) return;

        const scrollContainer = findScrollContainer(host);
        const controller = new AbortController();
        const options = { signal: controller.signal };
        let gesture = null;
        let animationFrame = 0;
        let suppressContextMenu = false;
        let suppressClick = false;
        const marquee = document.createElement('div');

        // This element lives directly under body. Inline geometry styles avoid
        // component CSS isolation turning it into a layout-affecting block.
        Object.assign(marquee.style, {
            position: 'fixed', zIndex: '90', pointerEvents: 'none',
            boxSizing: 'border-box', border: '1px solid #3b82f6',
            backgroundColor: 'rgba(59, 130, 246, 0.18)'
        });

        function scrollTop() {
            return scrollContainer === document.scrollingElement
                ? window.scrollY
                : scrollContainer.scrollTop;
        }

        function scrollViewport() {
            if (scrollContainer === document.scrollingElement) {
                return { top: 0, bottom: window.innerHeight, left: 0, right: window.innerWidth };
            }
            return scrollContainer.getBoundingClientRect();
        }

        function scrollBy(delta) {
            if (scrollContainer === document.scrollingElement)
                window.scrollBy(0, delta);
            else
                scrollContainer.scrollTop += delta;
        }

        function contentY(clientY) {
            return clientY - scrollViewport().top + scrollTop();
        }

        function clearPreview() {
            host.querySelectorAll('.file-marquee-preview')
                .forEach(row => row.classList.remove('file-marquee-preview'));
        }

        function captureRenderedRows() {
            if (!gesture) return;
            const viewport = scrollViewport();
            const offset = scrollTop() - viewport.top;

            host.querySelectorAll('.file-grid-row[data-path]').forEach(row => {
                const rect = row.getBoundingClientRect();
                gesture.rowGeometry.set(row.dataset.path, {
                    left: rect.left,
                    right: rect.right,
                    top: rect.top + offset,
                    bottom: rect.bottom + offset
                });
            });
        }

        function logicalRectangle() {
            const bounds = host.getBoundingClientRect();
            return {
                left: Math.max(bounds.left, Math.min(gesture.startX, gesture.currentX)),
                right: Math.min(bounds.right, Math.max(gesture.startX, gesture.currentX)),
                top: Math.min(gesture.startContentY, contentY(gesture.currentY)),
                bottom: Math.max(gesture.startContentY, contentY(gesture.currentY))
            };
        }

        function updateSelection() {
            if (!gesture?.active) return;

            captureRenderedRows();
            const logical = logicalRectangle();
            const viewport = scrollViewport();
            const currentScrollTop = scrollTop();
            const visibleTop = Math.min(viewport.bottom,
                Math.max(viewport.top, logical.top - currentScrollTop + viewport.top));
            const visibleBottom = Math.max(viewport.top,
                Math.min(viewport.bottom, logical.bottom - currentScrollTop + viewport.top));

            marquee.style.left = logical.left + 'px';
            marquee.style.top = visibleTop + 'px';
            marquee.style.width = Math.max(0, logical.right - logical.left) + 'px';
            marquee.style.height = Math.max(0, visibleBottom - visibleTop) + 'px';

            host.querySelectorAll('.file-grid-row[data-path]').forEach(row => {
                const geometry = gesture.rowGeometry.get(row.dataset.path);
                row.classList.toggle('file-marquee-preview', !!geometry && intersects(logical, geometry));
            });
        }

        function edgeScrollStep() {
            if (!gesture?.active) return 0;
            const viewport = scrollViewport();
            const y = gesture.currentY;

            if (y < viewport.top + edgeScrollZone) {
                const strength = Math.min(1, (viewport.top + edgeScrollZone - y) / edgeScrollZone);
                return -Math.max(1, Math.round(maximumScrollStep * strength));
            }
            if (y > viewport.bottom - edgeScrollZone) {
                const strength = Math.min(1, (y - (viewport.bottom - edgeScrollZone)) / edgeScrollZone);
                return Math.max(1, Math.round(maximumScrollStep * strength));
            }
            return 0;
        }

        function animate() {
            if (!gesture?.active) {
                animationFrame = 0;
                return;
            }

            const delta = edgeScrollStep();
            if (delta !== 0) scrollBy(delta);
            updateSelection();
            animationFrame = requestAnimationFrame(animate);
        }

        function stopGesture() {
            if (animationFrame) cancelAnimationFrame(animationFrame);
            animationFrame = 0;
            marquee.remove();
            gesture = null;
        }

        host.addEventListener('pointerdown', event => {
            if (event.button !== 0 && event.button !== 2) return;

            // File rows own their complete width. Only the surrounding gutters
            // and the empty area below the list may begin a marquee gesture.
            if (event.target.closest('.file-grid-row, .file-grid-header, button, a, input')) return;

            const headerBottom = host.querySelector('.file-grid-header')
                ?.getBoundingClientRect().bottom ?? host.getBoundingClientRect().top;
            if (event.clientY < headerBottom) return;

            gesture = {
                startX: event.clientX,
                startContentY: contentY(event.clientY),
                currentX: event.clientX,
                currentY: event.clientY,
                button: event.button,
                addToSelection: event.ctrlKey || event.metaKey,
                active: false,
                rowGeometry: new Map()
            };
            captureRenderedRows();
            host.setPointerCapture?.(event.pointerId);
        }, options);

        host.addEventListener('pointermove', event => {
            if (!gesture) return;

            gesture.currentX = event.clientX;
            gesture.currentY = event.clientY;
            const width = Math.abs(event.clientX - gesture.startX);
            const height = Math.abs(contentY(event.clientY) - gesture.startContentY);
            if (!gesture.active && width < minimumDragDistance && height < minimumDragDistance) return;

            if (!gesture.active) {
                gesture.active = true;
                document.body.appendChild(marquee);
                host.classList.add('file-marquee-active');
                animationFrame = requestAnimationFrame(animate);
            }

            updateSelection();
            event.preventDefault();
        }, options);

        host.addEventListener('pointerup', async event => {
            if (!gesture) return;
            const completedGesture = gesture;
            host.classList.remove('file-marquee-active');

            if (!completedGesture.active) {
                gesture = null;
                return;
            }

            completedGesture.currentX = event.clientX;
            completedGesture.currentY = event.clientY;
            captureRenderedRows();
            const selectionRectangle = logicalRectangle();
            const paths = Array.from(completedGesture.rowGeometry.entries())
                .filter(([, geometry]) => intersects(selectionRectangle, geometry))
                .map(([path]) => path);

            suppressContextMenu = completedGesture.button === 2;
            suppressClick = completedGesture.button === 0;
            clearPreview();
            stopGesture();
            await dotNetRef.invokeMethodAsync(
                'CompleteMarqueeSelection', paths, completedGesture.addToSelection);
        }, options);

        host.addEventListener('pointercancel', () => {
            host.classList.remove('file-marquee-active');
            clearPreview();
            stopGesture();
        }, options);

        scrollContainer.addEventListener('scroll', updateSelection, options);

        // Escape is page-level rather than tied to whichever non-focusable row
        // was clicked last. The component decides whether an open dialog owns it.
        document.addEventListener('keydown', event => {
            if (event.key !== 'Escape' || event.repeat) return;
            // When a modal owns the Escape (focus is inside it), the dialog's own
            // Cancel handles it. Skipping here avoids a race where the dialog
            // closes and this page-level handler then wipes the file selection.
            if (event.target.closest?.('.modal-backdrop')) return;
            void dotNetRef.invokeMethodAsync('ClearSelectionOnEscape');
        }, options);

        host.addEventListener('click', event => {
            if (!suppressClick) return;
            suppressClick = false;
            event.preventDefault();
            event.stopPropagation();
        }, { ...options, capture: true });

        host.addEventListener('contextmenu', event => {
            if (!suppressContextMenu) return;
            suppressContextMenu = false;
            event.preventDefault();
            event.stopPropagation();
        }, { ...options, capture: true });

        instances.set(selector, controller);
    }

    function dispose(selector) {
        instances.get(selector)?.abort();
        instances.delete(selector);
    }

    /**
     * Scrolls the file list so the entry at `index` is centered. Used after a
     * global-search hit selects a file: rows are virtualized, so the target row
     * may not be in the DOM — index math against the uniform row height reaches it
     * regardless of what is currently rendered. `hasParent` accounts for the ".."
     * row that precedes the sorted entries.
     */
    function scrollToIndex(selector, index, itemHeight, hasParent) {
        const host = document.querySelector(selector);
        if (!host || index < 0) return;

        const scroller = findScrollContainer(host);
        const isPage = scroller === document.scrollingElement;
        const viewportHeight = isPage ? window.innerHeight : scroller.clientHeight;

        // Coarse jump by index so Virtualize materializes the (possibly off-screen)
        // target row. Instant, not smooth, so the row is rendered before we center.
        const header = host.querySelector('.file-grid-header');
        const viewportTop = isPage ? 0 : scroller.getBoundingClientRect().top;
        const currentScroll = isPage ? window.scrollY : scroller.scrollTop;
        const headerBottom = header
            ? header.getBoundingClientRect().bottom
            : host.getBoundingClientRect().top;
        const entriesTop = headerBottom - viewportTop + currentScroll;
        const rowTop = entriesTop + (index + (hasParent ? 1 : 0)) * itemHeight;
        const top = Math.max(0, rowTop - viewportHeight / 2 + itemHeight / 2);

        if (isPage) window.scrollTo({ top, behavior: 'auto' });
        else scroller.scrollTop = top;

        // Then center the real row once it renders. scrollIntoView respects the
        // actual layout (sticky header, toolbar) that pure index math can't see,
        // so the entry is never left hidden above the fold. Blazor Server renders
        // the virtualized row after a scroll round-trip, so poll briefly for it.
        let tries = 0;
        const settle = () => {
            const row = host.querySelector('.file-row--selected');
            if (row) {
                row.scrollIntoView({ block: 'center', behavior: 'smooth' });
            } else if (tries++ < 20) {
                setTimeout(settle, 50);
            }
        };
        setTimeout(settle, 50);
    }

    return { initialize, dispose, scrollToIndex };
})();
