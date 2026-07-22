function initFileUpload(elementSelector) {

    // FileBrowser can be destroyed and recreated while the layout (and its file
    // input) stays alive. Abort the previous document-level handlers so one OS drop
    // can never dispatch several concurrent uploads after navigating around.
    window.kaimoFileUploadAbortController?.abort();
    const controller = new AbortController();
    window.kaimoFileUploadAbortController = controller;
    const listenerOptions = { signal: controller.signal };

    function isOsFileDrag(e) {
        return !!e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
    }

    document.addEventListener('dragenter', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        e.preventDefault();
        if (el?.contains(e.target)) {
            el.classList.add('file-dragged-over');
        }
    }, listenerOptions);

    document.addEventListener("dragstart", e => {
        e.dataTransfer.setData("text/html", "...")
    }, listenerOptions)

    document.addEventListener('dragover', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        e.preventDefault();

        if (el?.contains(e.target)) {
            el.classList.add('file-dragged-over');
        }
    }, listenerOptions);

    document.addEventListener('dragleave', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        if (el && !el.contains(e.relatedTarget)) {
            el.classList.remove('file-dragged-over');
        }
    }, listenerOptions);

    document.addEventListener('drop', e => {
        if (!isOsFileDrag(e)) return;

        e.preventDefault();
        const el = document.querySelector(elementSelector);
        if (el?.contains(e.target)) {
            el.classList.remove('file-dragged-over');
            const input = document.getElementById('global-file-input');
            if (!input) return;
            input.files = e.dataTransfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }
    }, listenerOptions);
}
