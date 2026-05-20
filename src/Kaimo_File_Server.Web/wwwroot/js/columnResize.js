
window.columnResize = {
    _dotNetRef: null,
    _onMouseMove: null,
    _onMouseUp: null,

    /**
     * @param {DotNetObjectReference} dotNetRef
     * @param {string} colClass - CSS-Klasse der Spalte (z.B. "col-size")
     */
    start: function (dotNetRef, colClass) {
        columnResize._cleanup();
        columnResize._dotNetRef = dotNetRef;

        // Aktuelle Breite der Spalte messen (wichtig für flex-Spalten)
        var headerCell = document.querySelector('.file-list-header .' + colClass);
        var actualWidth = headerCell ? headerCell.getBoundingClientRect().width : 0;

        // Blazor mitteilen, welche Breite die Spalte aktuell tatsächlich hat
        dotNetRef.invokeMethodAsync('OnResizeStartMeasured', actualWidth);

        columnResize._onMouseMove = function (e) {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OnResizeMove', e.clientX);
        };

        columnResize._onMouseUp = function () {
            dotNetRef.invokeMethodAsync('OnResizeEnd');
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
        columnResize._dotNetRef = null;
    }
};