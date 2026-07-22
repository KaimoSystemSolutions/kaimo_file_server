function initInternalDragDrop() {
    window.kaimoInternalDragDropAbortController?.abort();
    const controller = new AbortController();
    window.kaimoInternalDragDropAbortController = controller;
    const listenerOptions = { signal: controller.signal };

    let draggingPaths = [];

    function isOsFileDrag(e) {
        return !!e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
    }

    function isDescendantOrSelf(path, ofPath) {
        return path === ofPath || path.startsWith(ofPath.replace(/\/$/, '') + '/');
    }

    function canDropOn(row) {
        if (row.dataset.isDir !== 'true') return false;
        const targetPath = row.dataset.path;
        if (draggingPaths.length === 0) return false;
        return !draggingPaths.some(p => isDescendantOrSelf(targetPath, p));
    }

    document.addEventListener('dragstart', e => {
        const nameEl = e.target.closest('.col-name[draggable="true"]');
        if (!nameEl) return;

        const row = nameEl.closest('.file-grid-row');
        const selectedRows = document.querySelectorAll('.file-grid-row.file-row--selected');

        draggingPaths = (row?.classList.contains('file-row--selected') && selectedRows.length > 0)
            ? Array.from(selectedRows).map(r => r.dataset.path)
            : [row?.dataset.path].filter(Boolean);
    }, listenerOptions);

    document.addEventListener('dragend', () => {
        draggingPaths = [];
        document.querySelectorAll('.file-row--drop-target')
            .forEach(el => el.classList.remove('file-row--drop-target'));
    }, listenerOptions);

    document.addEventListener('dragenter', e => {
        if (isOsFileDrag(e)) return;
        const row = e.target.closest('.file-grid-row.file-row-dir');
        if (row && canDropOn(row)) {
            row.classList.add('file-row--drop-target');
        }
    }, listenerOptions);

    document.addEventListener('dragleave', e => {
        if (isOsFileDrag(e)) return;
        const row = e.target.closest('.file-grid-row.file-row-dir');
        if (row && !row.contains(e.relatedTarget)) {
            row.classList.remove('file-row--drop-target');
        }
    }, listenerOptions);

    document.addEventListener('drop', e => {
        if (isOsFileDrag(e)) return;
        document.querySelectorAll('.file-row--drop-target')
            .forEach(el => el.classList.remove('file-row--drop-target'));
    }, listenerOptions);
}
