/**
 * Column Resize v9 – 5 Spalten (ohne Actions-Spalte)
 *
 * Handle sitzt am rechten Rand von Spalte [i].
 * Drag tauscht Breite zwischen Spalte [i] (links) und [i+1] (rechts).
 * Keine andere Spalte ändert sich.
 *
 * Spalten: name(0), size(1), created(2), modified(3), lastaccess(4)
 * Handles: 0→name|size, 1→size|created, 2→created|modified, 3→modified|lastaccess
 */
window.columnResize = {
    _onMouseMove: null,
    _onMouseUp: null,

    /**
     * @param {DotNetObjectReference} dotNetRef
     * @param {number} leftIndex – Spaltenindex LINKS vom Handle
     * @param {number} startX
     * @param {number} minW
     */
    start: function (dotNetRef, leftIndex, startX, minW) {
        columnResize._cleanup();

        var wrap = document.querySelector('.file-table-wrap');
        if (!wrap) return;

        var rightIndex = leftIndex + 1;

        // Nur sichtbare Header-Zellen messen
        var headers = wrap.querySelectorAll('.file-grid-header > div');
        if (rightIndex >= headers.length) return;

        // Prüfen ob die Spalten sichtbar sind (responsive hiding)
        var leftHeader = headers[leftIndex];
        var rightHeader = headers[rightIndex];
        if (!leftHeader || !rightHeader) return;

        var leftStyle = window.getComputedStyle(leftHeader);
        var rightStyle = window.getComputedStyle(rightHeader);
        if (leftStyle.display === 'none' || rightStyle.display === 'none') return;

        var allWidths = [];
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
            wrap.style.gridTemplateColumns = parts.join(' ');
        };

        columnResize._onMouseUp = function () {
            var finalHeaders = wrap.querySelectorAll('.file-grid-header > div');
            var colNames = ['name', 'size', 'created', 'modified', 'lastaccess'];
            var result = {};
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