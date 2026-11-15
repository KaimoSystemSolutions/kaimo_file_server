/**
 * Column resize v9 – 5 columns (no actions column)
 *
 * The handle sits on the right edge of column [i].
 * Dragging trades width between column [i] (left) and [i+1] (right).
 * No other column changes.
 *
 * Columns: name(0), size(1), created(2), modified(3), lastaccess(4)
 * Handles: 0→name|size, 1→size|created, 2→created|modified, 3→modified|lastaccess
 */
window.columnResize = {
    _onMouseMove: null,
    _onMouseUp: null,

    /**
     * @param leftIndex – column index to the LEFT of the handle
     */
    start: function (dotNetRef: DotNetObjectReference, leftIndex: number, startX: number, minW: number) {
        columnResize._cleanup();

        var wrap = document.querySelector<HTMLElement>('.file-table-wrap');
        if (!wrap) return;

        var rightIndex = leftIndex + 1;

        // Measure visible header cells only
        var headers = wrap.querySelectorAll('.file-grid-header > div');
        if (rightIndex >= headers.length) return;

        // Check that both columns are visible (responsive hiding)
        var leftHeader = headers[leftIndex];
        var rightHeader = headers[rightIndex];
        if (!leftHeader || !rightHeader) return;

        var leftStyle = window.getComputedStyle(leftHeader);
        var rightStyle = window.getComputedStyle(rightHeader);
        if (leftStyle.display === 'none' || rightStyle.display === 'none') return;

        var allWidths: number[] = [];
        headers.forEach(function (h) {
            allWidths.push(h.getBoundingClientRect().width);
        });

        var leftStartW = allWidths[leftIndex];
        var rightStartW = allWidths[rightIndex];
        var budget = leftStartW + rightStartW;

        columnResize._onMouseMove = function (e) {
            e.preventDefault();
            var delta = e.clientX - startX;

            var newLeft = leftStartW + delta;
            var newRight = budget - newLeft;

            if (newLeft < minW) { newLeft = minW; newRight = budget - minW; }
            if (newRight < minW) { newRight = minW; newLeft = budget - minW; }

            allWidths[leftIndex] = newLeft;
            allWidths[rightIndex] = newRight;

            var parts = allWidths.map(function (w) {
                return Math.round(w) + 'px';
            });
            // Header + all data rows read this variable, so updating it once resizes
            // the whole (now per-row) grid in lockstep.
            wrap!.style.setProperty('--grid-cols', parts.join(' '));
        };

        columnResize._onMouseUp = function () {
            var finalHeaders = wrap!.querySelectorAll('.file-grid-header > div');
            var colNames = ['name', 'size', 'created', 'modified', 'lastaccess'];
            var result: Record<string, number> = {};
            finalHeaders.forEach(function (h, i) {
                if (i < colNames.length) {
                    result[colNames[i]] = Math.round(h.getBoundingClientRect().width);
                }
            });
            dotNetRef.invokeMethodAsync('OnResizeEnd', JSON.stringify(result));
            columnResize._cleanup();
        };

        document.addEventListener('mousemove', columnResize._onMouseMove);
        document.addEventListener('mouseup', columnResize._onMouseUp);
        document.body.style.userSelect = 'none';
        document.body.style.cursor = 'col-resize';
    },

    _cleanup: function () {
        if (columnResize._onMouseMove) {
            document.removeEventListener('mousemove', columnResize._onMouseMove);
            columnResize._onMouseMove = null;
        }
        if (columnResize._onMouseUp) {
            document.removeEventListener('mouseup', columnResize._onMouseUp);
            columnResize._onMouseUp = null;
        }
        document.body.style.userSelect = '';
        document.body.style.cursor = '';
    }
};
