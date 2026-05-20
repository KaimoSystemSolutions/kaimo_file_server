window.columnResize = {
    _dotNetRef: null,
    _onMouseMove: null,
    _onMouseUp: null,
 
    start: function (dotNetRef) {
        // Vorherige Listener aufräumen (Sicherheit)
        columnResize._cleanup();
 
        columnResize._dotNetRef = dotNetRef;
 
        columnResize._onMouseMove = function (e) {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OnResizeMove', e.clientX);
        };
 
        columnResize._onMouseUp = function (e) {
            dotNetRef.invokeMethodAsync('OnResizeEnd');
            columnResize._cleanup();
        };
 
        document.addEventListener('mousemove', columnResize._onMouseMove);
        document.addEventListener('mouseup', columnResize._onMouseUp);
 
        // Während des Resize: kein Text-Selektieren
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